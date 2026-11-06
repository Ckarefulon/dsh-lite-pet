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
import { createWriteStream, existsSync, mkdirSync, readFileSync, renameSync, rmSync, writeFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

export const name = 'dsh-lite-pet'

const PACKAGE_ROOT = dirname(fileURLToPath(import.meta.url))
const CORE_MEDIA_DIR = join(PACKAGE_ROOT, 'runtime', 'media')
const PET_EXE = join(PACKAGE_ROOT, 'runtime', 'pet', 'DshLitePet.exe')
const STATE_ROUTE = '/api/dsh-lite-pet/state'
const ENABLED_ROUTE = '/api/dsh-lite-pet/enabled'
const PUSH_INTERVAL_MS = 250
const BUBBLE_TEXT_LIMIT = 120
const BALANCE_VISIBLE_MS = 9000
const RESTART_DELAY_MS = 4000
const MAX_RESTARTS = 3

// The npm package carries only the clips the pet needs to be usable (idle, work
// states, balance, click, sleep ≈ 48 MiB). The 92 ambient actions weigh ~230 MiB,
// so they ship as a GitHub Release asset and are cached on first use.
const AMBIENT_ASSET = 'dsh-lite-pet-media-full.zip'
const AMBIENT_RELEASE_BASE =
  process.env.DSH_LITE_PET_ASSET_BASE ??
  'https://github.com/Ckarefulon/dsh-lite-pet/releases/latest/download/'

export function apply(ctx, config) {
  const supported = process.platform === 'win32'
  const settings = normalizeConfig(config)
  const profile = { settings, enabled: readEnabled(settings) }
  const runtime = createRuntime()
  runtime.enabled = profile.enabled

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

  wireAgentEvents(ctx, runtime)
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

/** Where the optional full animation set is cached once downloaded. */
function ambientCacheDir(config) {
  const home = process.env.DSH_HOME || join(homedir(), '.dsh')
  return join(home, 'dsh-lite-pet-media')
}

function hasCatalog(dir) {
  try {
    return existsSync(join(dir, 'index.json'))
  } catch {
    return false
  }
}

/**
 * Media directory the pet should play from: the cached full set when it is present,
 * otherwise the core clips that ship inside the package. The core set is always
 * complete on its own, so a failed download degrades instead of breaking.
 */
function activeMediaDir(config) {
  const cache = ambientCacheDir(config)
  return hasCatalog(cache) ? cache : CORE_MEDIA_DIR
}

/**
 * Exposed for the tier self-test: which media directory the pet should play from.
 * Not part of the plugin API.
 */
export function resolveMediaDirForTest(config) {
  return activeMediaDir(config ?? {})
}

/**
 * Downloads the ambient animation set in the background. Nothing waits on it: the
 * pet keeps playing the core clips and switches over on its next restart.
 */
async function fetchAmbientSet(ctx, runtime, config) {
  if (runtime.ambient.status !== 'absent') return
  const cache = ambientCacheDir(config)
  if (hasCatalog(cache)) {
    runtime.ambient.status = 'ready'
    return
  }
  runtime.ambient.status = 'downloading'
  const url = AMBIENT_RELEASE_BASE + AMBIENT_ASSET
  const staging = cache + '.part'
  try {
    ctx.logger?.info?.(`[dsh-lite-pet] downloading the full animation set from ${url}`)
    const response = await fetch(url, { redirect: 'follow' })
    if (!response.ok || response.body === null) throw new Error(`HTTP ${response.status}`)
    rmSync(staging, { recursive: true, force: true })
    mkdirSync(staging, { recursive: true })
    await extractZipTo(await response.arrayBuffer(), staging)
    if (!hasCatalog(staging)) throw new Error('the archive has no index.json')
    rmSync(cache, { recursive: true, force: true })
    renameSync(staging, cache)
    runtime.ambient.status = 'ready'
    ctx.logger?.info?.('[dsh-lite-pet] the full animation set is ready; it plays from the next restart')
  } catch (error) {
    rmSync(staging, { recursive: true, force: true })
    runtime.ambient.status = 'failed'
    ctx.logger?.warn?.(`[dsh-lite-pet] cannot fetch the full animation set: ${String(error?.message ?? error)}`)
  }
}

/**
 * Minimal ZIP reader: stored and deflated entries, no data descriptors. The asset is
 * produced by our own workflow, so the archive layout is predictable. Exported so the
 * self-test exercises the same code the plugin uses instead of a copy of it.
 */
export async function extractZipTo(buffer, targetDir) {
  const { inflateRawSync } = await import('node:zlib')
  // Accept an ArrayBuffer (from fetch) or a Buffer (from readFileSync).
  const bytes =
    buffer instanceof ArrayBuffer
      ? buffer
      : buffer.buffer.slice(buffer.byteOffset, buffer.byteOffset + buffer.byteLength)
  const view = new DataView(bytes)
  let offset = 0
  while (offset + 4 <= bytes.byteLength) {
    const signature = view.getUint32(offset, true)
    if (signature !== 0x04034b50) break // not a local file header: end of the entries we handle
    const flags = view.getUint16(offset + 6, true)
    const method = view.getUint16(offset + 8, true)
    const compressedSize = view.getUint32(offset + 18, true)
    const uncompressedSize = view.getUint32(offset + 22, true)
    const nameLength = view.getUint16(offset + 26, true)
    const extraLength = view.getUint16(offset + 28, true)
    const nameStart = offset + 30
    const decoder = new TextDecoder()
    const name = decoder.decode(new Uint8Array(bytes, nameStart, nameLength))
    const dataStart = nameStart + nameLength + extraLength
    const data = new Uint8Array(bytes, dataStart, compressedSize)
    const outPath = join(targetDir, name)
    if (name.endsWith('/')) {
      mkdirSync(outPath, { recursive: true })
    } else {
      mkdirSync(dirname(outPath), { recursive: true })
      const content = method === 0 ? data : method === 8 ? inflateRawSync(data) : null
      if (content === null) throw new Error(`unsupported compression method ${method} for ${name}`)
      writeFileSync(outPath, content)
    }
    void flags
    void uncompressedSize
    offset = dataStart + compressedSize
    // Entries are usually followed by a data descriptor when bit 3 is set.
    if ((flags & 0x0008) !== 0) {
      while (offset + 4 <= bytes.byteLength && view.getUint32(offset, true) === 0x08074b50) offset += 16
    }
  }
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
    // absent | downloading | ready | failed — the optional full animation set.
    ambient: { status: 'absent' },
  }
}

function ensurePet(ctx, runtime, profile) {
  if (runtime.disposing || runtime.pet || !runtime.enabled) return
  const mediaDir = activeMediaDir(profile.settings ?? {})
  if (!existsSync(PET_EXE) || !existsSync(mediaDir)) {
    ctx.logger?.warn?.('[dsh-lite-pet] runtime/pet or runtime/media is missing; run `npm run build` first')
    return
  }
  // Fetch the optional ambient set in the background the first time the pet runs.
  void fetchAmbientSet(ctx, runtime, profile.settings ?? {})
  const child = spawn(PET_EXE, ['--media', mediaDir], { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true })
  runtime.pet = child
  runtime.ready = false
  child.stdout?.setEncoding('utf8')
  child.stderr?.setEncoding('utf8')
  child.stdout?.on('data', (chunk) => {
    for (const line of String(chunk).split(/\r?\n/)) {
      if (line.trim() === '') continue
      try {
        const message = JSON.parse(line)
        // A late line from a pet we already replaced must not steer the new one.
        if (message?.t === 'closed' && runtime.pet !== child) continue
        handlePetLine(ctx, runtime, profile, message)
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
    // A crash or a lost window never flips the user's switch: retry while the
    // user still wants the pet, with a capped backoff.
    if (runtime.disposing || !runtime.enabled) return
    runtime.restarts += 1
    const delay = Math.min(RESTART_DELAY_MS * runtime.restarts, 30_000)
    ctx.logger?.warn?.(`[dsh-lite-pet] the pet exited; restarting in ${delay}ms`)
    setTimeout(() => ensurePet(ctx, runtime, profile), delay).unref?.()
  })
}

function handlePetLine(ctx, runtime, profile, message) {
  if (message?.t === 'ready') {
    runtime.ready = true
    runtime.restarts = 0
    // Re-send whatever the pet should be showing right now.
    runtime.lastSent = ''
    pushState(ctx, runtime, profile)
    return
  }
  if (message?.t === 'open') {
    openHarness(ctx, runtime)
    return
  }
  if (message?.t === 'balance') {
    void sendBalance(ctx, runtime, profile)
    return
  }
  if (message?.t === 'closed') {
    runtime.pet = null
    if (runtime.disposing) return
    // 右键菜单「关闭桌宠」是用户的明确选择：记住它，不要让它自己再启动。
    runtime.enabled = false
    profile.enabled = false
    try {
      writeEnabled(profile.settings, false)
    } catch {
      /* best effort */
    }
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

function wireAgentEvents(ctx, runtime) {
  const titles = () => {
    try {
      return ctx.get('sessionTitle', false)
    } catch {
      return undefined
    }
  }
  const stateOf = (sessionId) => {
    let state = runtime.agents.get(sessionId)
    if (state === undefined) {
      state = { running: false, waiting: 0, text: '', title: '', touched: 0 }
      runtime.agents.set(sessionId, state)
    }
    return state
  }
  const titleOf = (session) => {
    try {
      return titles()?.get(session)?.title ?? ''
    } catch {
      return ''
    }
  }

  // `session/event` is the one global, durable feed: the agent-scoped events
  // (agent/status, agent/assistant-stream, approval/request) are filtered to
  // their own scope and never reach a root listener.
  ctx.on('session/event', (session, event) => {
    if (session === undefined || session === null || event === undefined) return
    const header = session.header
    if (header?.parentSession !== undefined) return // subagents stay out of the bubble
    const state = stateOf(session.id)
    state.touched = Date.now()
    const title = titleOf(session)
    if (title !== '') state.title = title

    switch (event.type) {
      case 'turn/start':
        state.running = true
        state.waiting = 0
        runtime.doneText = null
        break
      case 'turn/end': {
        state.running = false
        state.waiting = 0
        const kind = event.data?.reason?.kind
        if (kind === 'completed') {
          runtime.doneText = `“${state.title || '会话'}”已完成`
          runtime.doneAt = Date.now()
        }
        break
      }
      case 'step/start':
      case 'tool/call':
        state.running = true
        if (event.type === 'tool/call' && event.data?.name === 'ask_user_question') state.waiting += 1
        break
      case 'tool/result':
        state.waiting = Math.max(0, state.waiting - 1)
        break
      case 'approval/asked':
        state.waiting += 1
        break
      case 'approval/decided':
        state.waiting = Math.max(0, state.waiting - 1)
        break
      case 'assistant/message': {
        state.running = true
        const text = assistantText(event.data)
        if (text !== '') state.text = text
        break
      }
    }
  })

  // Titles are generated asynchronously after the first prompt.
  ctx.effect(
    () => {
      const id = setInterval(() => {
        for (const state of runtime.agents.values()) {
          if (state.title !== '') continue
          for (const session of liveSessions(ctx)) {
            const title = titleOf(session)
            if (title !== '') state.title = title
          }
        }
      }, 5000)
      id.unref?.()
      return () => clearInterval(id)
    },
    'dsh-lite-pet: title refresh',
  )
}

function liveSessions(ctx) {
  try {
    const sessions = ctx.get('sessions', false)
    return sessions === undefined ? [] : sessions.list()
  } catch {
    return []
  }
}

/** Last assistant text already committed for this session (so the bubble has real copy at turn start). */
function recentAssistantText(session) {
  try {
    const events = session.snapshotEvents()
    const from = Math.max(0, events.length - 400)
    for (let index = events.length - 1; index >= from; index--) {
      const event = events[index]
      if (event?.type !== 'assistant/message') continue
      const text = assistantText(event.data)
      if (text !== '') return text
    }
  } catch {
    /* a session without a readable log just gets the fallback text */
  }
  return ''
}

/** Last text block of the newest assistant message, flattened for the bubble. */
function assistantText(data) {
  const message = data?.message
  const blocks = Array.isArray(message?.content) ? message.content : []
  let last = ''
  for (const block of blocks) {
    if (block?.type === 'text' && typeof block.text === 'string') last = block.text
  }
  if (last !== '') return last.slice(-400)
  const records = Array.isArray(data?.stream) ? data.stream : []
  for (let index = records.length - 1; index >= 0; index--) {
    const record = records[index]
    if (record?.type === 'text-chunks' && Array.isArray(record.texts) && record.texts.length > 0) {
      return record.texts.join('').slice(-400)
    }
  }
  return ''
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

function openHarness(ctx, runtime) {
  const url = (() => {
    try {
      return ctx.get('webApp', false)?.getAdvertisedUrl?.()
    } catch {
      return undefined
    }
  })()
  const registered = desktopProtocolRegistered()
  // The pet does the window work: it is a native process, so it can call
  // ShellExecuteEx on dsh://open (measured to be the only invocation that reaches the
  // running desktop instance — `cmd start` and `rundll32 url.dll` both failed here),
  // and it can restore a minimised window through WM_SYSCOMMAND/SC_RESTORE.
  // When the protocol is not registered, fall back to opening the web GUI.
  if (registered) {
    sendPet(runtime, { t: 'raise' })
    return
  }
  if (url === undefined || url === null || url === '') return
  try {
    const child = spawn('cmd.exe', ['/c', 'start', '', url], { windowsHide: true, detached: true, stdio: 'ignore' })
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









