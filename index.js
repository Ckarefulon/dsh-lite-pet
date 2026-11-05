/**
 * dsh-lite-pet — Host half.
 *
 * Spawns the native desktop-pet process (Windows) and keeps it in sync with the
 * Harness: who is working, what the assistant last said, whether a decision is
 * pending, and the account balance on demand.
 *
 * The renderer is a separate C# process (runtime/pet/DshLitePet.exe). It talks
 * to this module over newline-delimited JSON on stdio:
 *   host -> pet : {"t":"state","mode":"idle|working|waiting|done|balance","text":"…","level":n}
 *                 {"t":"quit"}
 *   pet -> host : {"t":"ready"} {"t":"open"} {"t":"balance"} {"t":"closed"}
 *
 * Nothing here needs approval to run: no HTTP route is exposed to the browser
 * beyond the two JSON endpoints below, and both are registered on the standard
 * `/api` connection fence.
 *
 * @module dsh-lite-pet
 */
import { spawn } from 'node:child_process'
import { existsSync, readFileSync, writeFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

export const name = 'dsh-lite-pet'

const PACKAGE_ROOT = dirname(fileURLToPath(import.meta.url))
const MEDIA_DIR = join(PACKAGE_ROOT, 'runtime', 'media')
const PET_EXE = join(PACKAGE_ROOT, 'runtime', 'pet', 'DshLitePet.exe')
const STATE_ROUTE = '/api/dsh-lite-pet/state'
const ENABLED_ROUTE = '/api/dsh-lite-pet/enabled'
const PUSH_INTERVAL_MS = 250
const BUBBLE_TEXT_LIMIT = 120
const BALANCE_VISIBLE_MS = 9000
const RESTART_DELAY_MS = 4000
const MAX_RESTARTS = 3

export function apply(ctx, config) {
  const supported = process.platform === 'win32'
  const settings = normalizeConfig(config)
  const profile = { settings, enabled: readEnabled(settings) }
  const runtime = createRuntime()

  ctx.effect(() => () => {
    runtime.disposing = true
    stopPet(runtime)
  }, 'dsh-lite-pet: process lifetime')

  // ——— account / window plumbing ————————————————————————————————————————
  const pushTick = () => pushState(ctx, runtime, profile)
  const timer = setInterval(() => {
    try {
      pushTick()
    } catch (error) {
      ctx.logger?.warn?.(`[dsh-lite-pet] push failed: ${String(error?.message ?? error)}`)
    }
  }, PUSH_INTERVAL_MS)
  timer.unref?.()
  ctx.effect(() => () => clearInterval(timer), 'dsh-lite-pet: push timer')

  wireAgentEvents(ctx, runtime, profile)
  wireRoutes(ctx, runtime, profile)

  if (!supported) {
    ctx.logger?.info?.('[dsh-lite-pet] desktop pet is Windows-only; staying idle on this platform')
    return
  }
  if (profile.enabled) ensurePet(ctx, runtime, profile)
}

// ——— configuration & persistence ————————————————————————————————————————

function normalizeConfig(raw) {
  const source = raw !== null && typeof raw === 'object' ? raw : {}
  return {
    enabled: source.enabled !== false,
    // The pet remembers nothing on its own; this file is the only persisted bit.
    stateFile: typeof source.stateFile === 'string' && source.stateFile !== '' ? source.stateFile : undefined,
  }
}

function stateFile(config) {
  if (config.stateFile) return config.stateFile
  const home = process.env.DSH_HOME || join(homedir(), '.dsh')
  return join(home, 'dsh-lite-pet.json')
}

function readPersistedFlags(config) {
  try {
    const raw = JSON.parse(readFileSync(stateFile(config), 'utf8'))
    return raw !== null && typeof raw === 'object' ? raw : {}
  } catch {
    return {}
  }
}

function readEnabled(config) {
  const persisted = readPersistedFlags(config)
  if (typeof persisted.enabled === 'boolean') return persisted.enabled
  return config.enabled
}

function writeEnabled(config, enabled) {
  const path = stateFile(config)
  try {
    let current = {}
    try {
      current = JSON.parse(readFileSync(path, 'utf8'))
    } catch {
      current = {}
    }
    writeFileSync(path, JSON.stringify({ ...current, enabled }, null, 2) + '\n', 'utf8')
  } catch (error) {
    throw new Error(`无法写入 ${path}: ${error instanceof Error ? error.message : String(error)}`)
  }
}

// ——— runtime state ————————————————————————————————————————————————————

function createRuntime() {
  return {
    pet: null,
    ready: false,
    enabled: false,
    lastSent: '',
    balanceUntil: 0,
    restarts: 0,
    disposing: false,
    agents: new Map(), // sessionId -> { running, waiting, text, title, touched }
    doneText: null,
    doneAt: 0,
  }
}

function ensurePet(ctx, runtime, profile) {
  if (runtime.disposing || runtime.pet) return
  if (!existsSync(PET_EXE) || !existsSync(MEDIA_DIR)) {
    ctx.logger?.warn?.('[dsh-lite-pet] runtime/pet or runtime/media is missing; run `npm run build` first')
    return
  }
  const child = spawn(PET_EXE, ['--media', MEDIA_DIR], { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true })
  runtime.pet = child
  runtime.enabled = true
  runtime.ready = false
  child.stdout?.setEncoding('utf8')
  child.stderr?.setEncoding('utf8')
  child.stdout?.on('data', (chunk) => {
    for (const line of String(chunk).split(/\r?\n/)) {
      if (line.trim() === '') continue
      try {
        handlePetLine(ctx, runtime, profile, JSON.parse(line))
      } catch {
        /* a malformed line must never take the Host down */
      }
    }
  })
  child.stderr?.on('data', (chunk) => {
    const text = String(chunk).trim()
    if (text !== '') ctx.logger?.warn?.(`[dsh-lite-pet] ${text}`)
  })
  child.on('error', (error) => {
    ctx.logger?.warn?.(`[dsh-lite-pet] cannot start the pet: ${String(error?.message ?? error)}`)
  })
  child.on('exit', () => {
    runtime.pet = null
    runtime.ready = false
    if (runtime.disposing || !runtime.enabled) return
    if (runtime.restarts >= MAX_RESTARTS) {
      ctx.logger?.warn?.('[dsh-lite-pet] the pet exited repeatedly; staying off until re-enabled')
      profile.enabled = false
      try {
        writeEnabled(profile.settings, false)
      } catch {
        /* best effort */
      }
      return
    }
    runtime.restarts += 1
    const delay = RESTART_DELAY_MS * runtime.restarts
    setTimeout(() => ensurePet(ctx, runtime, profile), delay).unref?.()
  })
}

function handlePetLine(ctx, runtime, profile, message) {
  if (message?.t === 'ready') {
    runtime.ready = true
    // Re-send whatever the pet should be showing right now.
    runtime.lastSent = ''
    pushState(ctx, runtime, profile)
    return
  }
  if (message?.t === 'open') {
    openHarness(ctx)
    return
  }
  if (message?.t === 'balance') {
    void sendBalance(ctx, runtime, profile)
    return
  }
  if (message?.t === 'closed') {
    runtime.pet = null
  }
}

function sendPet(runtime, payload) {
  const child = runtime.pet
  if (child === null || child === undefined) return false
  try {
    child.stdin.write(JSON.stringify(payload) + '\n')
    return true
  } catch {
    return false
  }
}

// ——— Harness events ————————————————————————————————————————————————————

function wireAgentEvents(ctx, runtime, profile) {
  const sessions = () => {
    try {
      return ctx.get('sessions', false)
    } catch {
      return undefined
    }
  }
  const titles = () => {
    try {
      return ctx.get('sessionTitle', false)
    } catch {
      return undefined
    }
  }
  const isRootAgent = (agent) => {
    try {
      const session = sessions()?.get(agent.id)
      return session === undefined || session?.header?.parentSession === undefined
    } catch {
      return true
    }
  }
  const stateOf = (agentId) => {
    let state = runtime.agents.get(agentId)
    if (state === undefined) {
      state = { running: false, waiting: 0, text: '', title: '', touched: 0 }
      runtime.agents.set(agentId, state)
    }
    return state
  }
  const titleOf = (agentId) => {
    try {
      const session = sessions()?.get(agentId)
      if (session === undefined) return ''
      return titles()?.get(session)?.title ?? ''
    } catch {
      return ''
    }
  }

  ctx.on('agent/status', (payload) => {
    const agent = payload?.agent
    if (!agent || !isRootAgent(agent)) return
    const state = stateOf(agent.id)
    state.touched = Date.now()
    state.title = titleOf(agent.id) || state.title
    state.running = payload.status === 'running'
    if (state.running) {
      runtime.doneText = null
      state.waiting = 0
    } else if (state.text !== '' || state.title !== '') {
      // A finished turn leaves the completion bubble up until the next turn.
      runtime.doneText = `“${state.title || '会话'}”已完成`
      runtime.doneAt = Date.now()
    }
  })

  ctx.on('agent/assistant-stream', (payload) => {
    const agent = payload?.agent
    const frame = payload?.frame
    if (!agent || !isRootAgent(agent) || frame?.type !== 'chunk') return
    if (frame.chunk?.type !== 'text-delta') return
    const state = stateOf(agent.id)
    state.touched = Date.now()
    state.text = (state.text + frame.chunk.text).slice(-400)
  })

  const trackWaiting = (agentId) => {
    const state = stateOf(agentId)
    state.touched = Date.now()
    state.waiting += 1
    return () => {
      state.waiting = Math.max(0, state.waiting - 1)
    }
  }

  ctx.on('approval/request', async (req, next) => {
    const done = req?.agent ? trackWaiting(req.agent.id) : () => {}
    try {
      return await next()
    } finally {
      done()
    }
  })

  ctx.on('user-questions/request', async (request, next) => {
    const done = request?.agent ? trackWaiting(request.agent.id) : () => {}
    try {
      return await next()
    } finally {
      done()
    }
  })

  // Keep titles fresh: they are generated asynchronously after the first prompt.
  ctx.effect(
    () => {
      const id = setInterval(() => {
        for (const [agentId, state] of runtime.agents) {
          const title = titleOf(agentId)
          if (title !== '' && title !== state.title) state.title = title
        }
      }, 5000)
      id.unref?.()
      return () => clearInterval(id)
    },
    'dsh-lite-pet: title refresh',
  )
}

// ——— state push ————————————————————————————————————————————————————————

function pushState(ctx, runtime, profile) {
  if (runtime.pet === null || !runtime.ready) return
  if (Date.now() < runtime.balanceUntil) return

  const now = Date.now()
  let mode = 'idle'
  let text
  let touched = -1
  let waitingAgent = null
  let runningAgent = null
  for (const [agentId, state] of runtime.agents) {
    if (now - state.touched > 60_000) continue
    if (state.waiting > 0 && (waitingAgent === null || state.touched > touched)) {
      waitingAgent = agentId
      touched = state.touched
    }
    if (state.running && (runningAgent === null || state.touched > touched)) {
      runningAgent = agentId
    }
  }
  if (waitingAgent !== null) {
    mode = 'waiting'
    text = bubbleText(runtime.agents.get(waitingAgent).text) || '需要你确认一下'
  } else if (runningAgent !== null) {
    mode = 'working'
    text = bubbleText(runtime.agents.get(runningAgent).text) || '正在工作…'
  } else if (runtime.doneText !== null && now - runtime.doneAt < 12 * 60_000) {
    mode = 'done'
    text = runtime.doneText
  }

  const payload = JSON.stringify({ t: 'state', mode, text })
  if (payload === runtime.lastSent) return
  if (sendPet(runtime, { t: 'state', mode, text })) runtime.lastSent = payload
}

function bubbleText(raw) {
  if (typeof raw !== 'string') return ''
  const normalized = raw.replace(/\r/g, '').replace(/[ \t]+/g, ' ').trim()
  if (normalized === '') return ''
  const paragraphs = normalized.split('\n').filter((line) => line.trim() !== '')
  const tail = (paragraphs.length > 0 ? paragraphs[paragraphs.length - 1] : normalized).trim()
  return tail.length > BUBBLE_TEXT_LIMIT ? tail.slice(-BUBBLE_TEXT_LIMIT) : tail
}

// ——— balance ——————————————————————————————————————————————————————————

async function sendBalance(ctx, runtime, profile) {
  let text = '查不到余额：当前没有可用的账号服务'
  let level = 3
  try {
    const account = ctx.get('deepseekAccount', false)
    if (account !== undefined && account !== null) {
      const result = await account.getBalance({
        version: '0.1.0',
        locale: 'zh-CN',
        timezoneOffsetSeconds: -new Date().getTimezoneOffset() * 60,
      })
      if (result === null) {
        text = '还没登录，请到 设置 · 账号与余额 登录一下'
        level = 3
      } else if (result.status !== 'ready') {
        text = '余额查询失败，稍后再试'
        level = 3
      } else {
        const wallets = Array.isArray(result.value) ? result.value : []
        const bonuses = Array.isArray(result.bonusWallets) ? result.bonusWallets : []
        const main = wallets[0]
        if (main === undefined) {
          text = '余额为空'
          level = 4
        } else {
          const symbol = main.currency === 'CNY' ? '¥' : '$'
          const amount = Number(main.balance)
          const bonus = bonuses.length > 0
            ? `（赠金 ${bonuses.map((wallet) => `${symbol}${Number(wallet.balance).toFixed(2)}`).join(' / ')}）`
            : ''
          text = `余额 ${symbol}${amount.toFixed(2)}${bonus}`
          level = amount >= 100 ? 0 : amount >= 50 ? 1 : amount >= 20 ? 2 : amount >= 5 ? 3 : amount > 0 ? 4 : 5
        }
      }
    }
  } catch (error) {
    text = '余额查询出错，稍后再试'
    ctx.logger?.warn?.(`[dsh-lite-pet] balance query failed: ${String(error?.message ?? error)}`)
  }
  runtime.balanceUntil = Date.now() + BALANCE_VISIBLE_MS
  runtime.lastSent = ''
  sendPet(runtime, { t: 'state', mode: 'balance', text, level })
}

// ——— open the Harness window ——————————————————————————————————————————

let protocolChecked
function desktopProtocolRegistered() {
  if (protocolChecked !== undefined) return protocolChecked
  try {
    const probe = spawn('reg.exe', ['query', 'HKCU\\Software\\Classes\\dsh', '/ve'], { windowsHide: true })
    let out = ''
    probe.stdout.setEncoding('utf8')
    probe.stdout.on('data', (chunk) => {
      out += chunk
    })
    probe.on('close', () => {
      protocolChecked = out.includes('URL:dsh')
    })
    probe.on('error', () => {
      protocolChecked = false
    })
    return true // optimistic for the first click; the probe settles quickly
  } catch {
    protocolChecked = false
    return false
  }
}

function openHarness(ctx) {
  const url = (() => {
    try {
      return ctx.get('webApp', false)?.getAdvertisedUrl?.()
    } catch {
      return undefined
    }
  })()
  const registered = desktopProtocolRegistered()
  const target = registered ? 'dsh://open' : url
  if (target === undefined || target === null || target === '') return
  try {
    const child = spawn('cmd.exe', ['/c', 'start', '', target], { windowsHide: true, detached: true, stdio: 'ignore' })
    child.unref()
  } catch (error) {
    ctx.logger?.warn?.(`[dsh-lite-pet] cannot open the Harness: ${String(error?.message ?? error)}`)
  }
}

// ——— browser routes ————————————————————————————————————————————————————

function wireRoutes(ctx, runtime, profile) {
  const connection = (() => {
    try {
      return ctx.get('connection', false)
    } catch {
      return undefined
    }
  })()
  if (connection === undefined || connection === null) return
  const fetchRoutes = connection.fetch
  if (fetchRoutes === undefined || typeof fetchRoutes.register !== 'function') return

  const json = (body, status = 200) =>
    new Response(JSON.stringify(body), {
      status,
      headers: { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store' },
    })

  const snapshot = () => ({
    ok: true,
    supported: process.platform === 'win32',
    enabled: runtime.pet !== null || runtime.enabled,
    running: runtime.pet !== null && runtime.ready,
  })

  ctx.effect(
    () =>
      fetchRoutes.register({
        path: STATE_ROUTE,
        methods: ['GET'],
        requestBody: 'buffered',
        fetch: () => Promise.resolve(json(snapshot())),
      }),
    'dsh-lite-pet: state route',
  )

  ctx.effect(
    () =>
      fetchRoutes.register({
        path: ENABLED_ROUTE,
        methods: ['POST'],
        requestBody: 'buffered',
        fetch: async (request) => {
          if (process.platform !== 'win32') {
            return json({ ok: false, supported: false, error: '桌宠目前只支持 Windows' }, 409)
          }
          let body
          try {
            body = await request.json()
          } catch {
            return json({ ok: false, error: '请求体不是合法 JSON' }, 400)
          }
          const enabled = body?.enabled === true
          try {
            writeEnabled(profile.settings ?? {}, enabled)
          } catch (error) {
            return json({ ok: false, error: error instanceof Error ? error.message : String(error) }, 500)
          }
          runtime.enabled = enabled
          if (enabled) {
            ensurePet(ctx, runtime, profile)
          } else {
            stopPet(runtime)
          }
          return json({ ...snapshot(), ok: true })
        },
      }),
    'dsh-lite-pet: enabled route',
  )
}

function stopPet(runtime) {
  const child = runtime.pet
  runtime.pet = null
  runtime.ready = false
  if (child === null || child === undefined) return
  try {
    child.stdin.end()
  } catch {
    /* already gone */
  }
  const timer = setTimeout(() => {
    try {
      child.kill()
    } catch {
      /* already gone */
    }
  }, 1500)
  timer.unref?.()
}
