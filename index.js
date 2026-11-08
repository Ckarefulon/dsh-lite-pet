/**
 * dsh-lite-pet — Host half.
 *
 * Spawns the native desktop-pet process (Windows) and keeps it in sync with the
 * Harness: who is working, what the assistant last said, whether a decision is
 * pending, and the account balance on demand.
 *
 * The renderer is a separate C# process (runtime/pet/DshLitePet.exe). It talks
 * to this module over newline-delimited JSON on stdio:
 *   host -> pet : {"t":"state","mode":"idle|working|waiting|done|error|balance","text":"…","level":n}
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
import { request as httpsRequest } from 'node:https'
import { homedir } from 'node:os'
import { dirname, join } from 'node:path'
import * as tls from 'node:tls'
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
// Backstop for the "completed" bubble. Clicking the pet clears it immediately; this only
// stops a forgotten message from sitting there for the rest of the session.
const DONE_VISIBLE_MS = 12 * 60_000
// Same backstop for an abnormal end. It is deliberately not shorter: an error is the one
// thing the user has to read, so it waits for them (or for their click) rather than
// expiring on its own while they are looking elsewhere.
const ERROR_VISIBLE_MS = 12 * 60_000
// How long a session that is neither running, waiting, finished nor failed is remembered.
// Sessions have to be forgotten eventually — the map would grow for the life of the
// process — but only once they have nothing left to say.
const FORGET_AFTER_MS = 30 * 60_000
// How long an idle session keeps narrating the bubble. A running session is exempt: its
// turn ends with a durable event, so a long silence is a slow turn, not an abandoned one.
const ACTIVITY_WINDOW_MS = 60_000
const RESTART_DELAY_MS = 4000
const MAX_RESTARTS = 3

// The npm package carries only the clips the pet needs to be usable (idle, work
// states, balance, click, sleep ≈ 48 MiB). The 92 ambient actions weigh ~230 MiB,
// so they ship as a GitHub Release asset and are cached on first use.
const AMBIENT_ASSET = 'dsh-lite-pet-media-full.zip'
// Where the full animation set is fetched from. `latest` is tried first so the
// plugin never has to be republished just to refresh its media, but it is not
// trusted on its own: `releases/latest` resolves to whatever release is newest,
// and a newest release without the asset answers 404 — which is exactly what a
// release published while its upload failed looks like. The pinned tag below is
// a fallback that always had the asset.
const AMBIENT_PINNED_TAG = 'v0.2.4'
const AMBIENT_RELEASE_BASE =
  process.env.DSH_LITE_PET_ASSET_BASE ??
  'https://github.com/Ckarefulon/dsh-lite-pet/releases/latest/download/'
const AMBIENT_FALLBACK_BASE = `https://github.com/Ckarefulon/dsh-lite-pet/releases/download/${AMBIENT_PINNED_TAG}/`
// A failed download is not permanent — most are a flaky proxy or a dropped
// connection. Retry on this interval while the pet keeps running.
const AMBIENT_RETRY_MS = 10 * 60_000

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
  const pushTick = () => {
    pushState(ctx, runtime, profile)
    // Cheap no-op unless a previous attempt failed and its backoff has elapsed, so a
    // dropped download recovers on its own instead of waiting for a pet restart.
    if (runtime.enabled && runtime.pet !== null) void fetchAmbientSet(ctx, runtime, profile.settings)
  }
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
 * Trust roots for the animation download: Node's bundled list plus whatever the
 * machine itself trusts.
 *
 * The bundled list alone is not enough on a normal Chinese desktop. GitHub is
 * routinely fronted by a local accelerator that terminates TLS on 127.0.0.1 and
 * re-signs the connection with a root that lives in the Windows certificate store.
 * Node's built-in CAs never contain that root, so `fetch` dies with
 * UNABLE_TO_VERIFY_LEAF_SIGNATURE before a single byte is requested — measured on
 * this machine, where the accelerator listens on 127.0.0.1:443 and both browsers
 * work fine.
 *
 * Merged explicitly instead of calling `tls.setDefaultCACertificates`, because that
 * would change TLS defaults for every other plugin in this process.
 */
export function trustRoots() {
  try {
    const { getCACertificates } = tls
    if (typeof getCACertificates !== 'function') return undefined
    const merged = [...getCACertificates('default')]
    const seen = new Set(merged)
    for (const system of getCACertificates('system')) {
      if (!seen.has(system)) {
        seen.add(system)
        merged.push(system)
      }
    }
    return merged
  } catch {
    return undefined // fall back to Node's defaults rather than failing the download
  }
}

// The HTTPS request function. Swappable so the self-test can drive `transfer` over a
// fake socket instead of standing up a TLS server, which needs an openssl that a
// stock Windows box does not have.
let httpsTransport = httpsRequest

/** Test hook. Pass null to restore the real transport. Not part of the plugin API. */
export function setTransferForTest(fn) {
  httpsTransport = fn === null ? httpsRequest : fn
}

/**
 * GETs a URL and resolves with the full body, following redirects.
 *
 * `node:https` rather than `fetch` because only it accepts a per-request `ca` list;
 * handing the merged trust roots to `fetch` means mutating the process-wide TLS
 * defaults, and this download has no business doing that to the rest of the Host.
 */
export function transfer(url, redirectsLeft = 5) {
  return new Promise((resolve, reject) => {
    let settled = false
    const done = (fn, value) => {
      if (settled) return
      settled = true
      fn(value)
    }
    const request = httpsTransport(
      url,
      { method: 'GET', ca: trustRoots(), headers: { 'accept': 'application/octet-stream' } },
      (response) => {
        const status = response.statusCode ?? 0
        const location = response.headers.location
        if (status >= 300 && status < 400 && typeof location === 'string' && redirectsLeft > 0) {
          response.resume() // drain so the socket is released
          done(resolve, transfer(new URL(location, url).href, redirectsLeft - 1))
          return
        }
        if (status !== 200) {
          response.resume()
          done(reject, new Error(`HTTP ${status}`))
          return
        }
        const chunks = []
        let received = 0
        const expected = Number(response.headers['content-length'] ?? 0)
        response.on('data', (chunk) => {
          chunks.push(chunk)
          received += chunk.length
        })
        response.on('end', () => {
          // A truncated archive must not be unpacked onto a good cache.
          if (expected > 0 && received !== expected) {
            done(reject, new Error(`truncated download: ${received} of ${expected} bytes`))
            return
          }
          done(resolve, Buffer.concat(chunks))
        })
        response.on('error', (error) => done(reject, error))
      },
    )
    request.on('error', (error) => done(reject, error))
    request.setTimeout(120_000, () => {
      request.destroy(new Error('timed out'))
    })
    request.end()
  })
}

/** Test hook for the retry/backoff contract. Not part of the plugin API. */
export function fetchAmbientSetForTest(ctx, runtime, config) {
  return fetchAmbientSet(ctx, runtime, config ?? {})
}

/**
 * Downloads the ambient animation set in the background. Nothing waits on it: the
 * pet keeps playing the core clips and switches over on its next restart.
 *
 * A failed attempt is retried later instead of latching until the next restart.
 * The common failure is a transient one, and latching it is how the pet silently
 * played the shortened library for a whole session.
 */
async function fetchAmbientSet(ctx, runtime, config) {
  if (runtime.ambient.status === 'downloading' || runtime.ambient.status === 'ready') return
  const cache = ambientCacheDir(config)
  if (hasCatalog(cache)) {
    runtime.ambient.status = 'ready'
    return
  }
  if (Date.now() < runtime.ambient.retryAt) return
  runtime.ambient.status = 'downloading'
  const staging = cache + '.part'
  const sources = [AMBIENT_RELEASE_BASE]
  if (process.env.DSH_LITE_PET_ASSET_BASE === undefined) sources.push(AMBIENT_FALLBACK_BASE)
  let body = null
  let url = ''
  let lastError = null
  for (const base of sources) {
    url = base + AMBIENT_ASSET
    try {
      ctx.logger?.info?.(`[dsh-lite-pet] downloading the full animation set from ${url}`)
      body = await transfer(url)
      break
    } catch (error) {
      // A 404 on `latest` means the newest release has no asset; the pinned tag
      // still does. Any other failure is worth trying against the fallback too.
      lastError = error
      body = null
      ctx.logger?.warn?.(`[dsh-lite-pet] ${url} did not work: ${String(error?.message ?? error)}`)
    }
  }
  try {
    if (body === null) throw lastError ?? new Error('no download source was usable')
    rmSync(staging, { recursive: true, force: true })
    mkdirSync(staging, { recursive: true })
    await extractZipTo(body, staging)
    if (!hasCatalog(staging)) throw new Error('the archive has no index.json')
    rmSync(cache, { recursive: true, force: true })
    renameSync(staging, cache)
    runtime.ambient.status = 'ready'
    ctx.logger?.info?.(`[dsh-lite-pet] the full animation set is ready (from ${url})`)
    // The pet read its catalog at startup, so hand it the new one right away
    // instead of waiting for a Harness restart the user may never do.
    restartPetForMedia(ctx, runtime, config)
  } catch (error) {
    rmSync(staging, { recursive: true, force: true })
    runtime.ambient.status = 'failed'
    runtime.ambient.retryAt = Date.now() + AMBIENT_RETRY_MS
    ctx.logger?.warn?.(
      `[dsh-lite-pet] cannot fetch the full animation set: ${String(error?.message ?? error)}; ` +
        `the core clips keep playing and the download is retried in ${Math.round(AMBIENT_RETRY_MS / 60_000)} min`,
    )
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
    agents: new Map(), // sessionId -> { running, waiting, text, title, prompt, error, touched }
    doneText: null,
    doneAt: 0,
    // absent | downloading | ready | failed — the optional full animation set.
    // `retryAt` gates the next attempt after a failure.
    ambient: { status: 'absent', retryAt: 0 },
    // The directory the running pet was started with, so a completed download can
    // tell whether the process needs restarting to pick it up.
    mediaDir: null,
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
  runtime.mediaDir = mediaDir
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
    // Clicking the pet is the user acknowledging the result: the completed bubble has
    // done its job once they have gone looking. This runs for every click, whether or not
    // the pet could deal with the window, because clearing the bubble is about the click
    // and not about the window.
    runtime.doneText = null
    runtime.doneAt = 0
    // The red bubble is cleared by the same click, for the same reason: opening the window
    // is the user going to look at the failure. Leaving it up afterwards would make the pet
    // keep reporting a problem the user has already acknowledged.
    for (const state of runtime.agents.values()) state.error = ''
    runtime.lastSent = ''
    // `shell` is set only when the pet's own attempt established that the window is hidden
    // and out of its reach. Without it the click needs nothing from this process.
    if (message.shell === 1 || message.shell === true) void reopenHidden(ctx, runtime)
    pushState(ctx, runtime, profile)
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
      state = { running: false, waiting: 0, text: '', title: '', prompt: '', error: '', touched: 0 }
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
        state.error = ''
        state.text = ''
        runtime.doneText = null
        break
      case 'turn/end': {
        state.running = false
        state.waiting = 0
        const reason = event.data?.reason
        const kind = reason?.kind
        if (kind === 'completed') {
          // Green carries the session TITLE, and nothing else: the dot already says it
          // finished, so decorating the text ("…已完成") only pushed the title towards the
          // ellipsis. The fallback covers a session whose title has not been generated yet.
          runtime.doneText = state.title === '' ? '会话已完成' : state.title
          runtime.doneAt = Date.now()
        } else if (kind === 'error' || kind === 'blocked' || kind === 'max-tokens') {
          // An abnormal end is a red dot, and it outranks both green and working: the
          // session stopped without answering, so the last thing the user saw is a
          // question that will never be answered. `interrupted` is deliberately absent
          // — it is the shell's own restart artefact, not something that happened to
          // this turn, and flagging it would leave a red bubble after every reload.
          state.error = failureText(reason, state.title)
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
      case 'user/message': {
        // The prompt the user just sent is the bubble's text until the model says
        // something: with an empty bubble the pet looks like it missed the message.
        // Only a real user message counts — the turn also carries injected context
        // (`runtime-context`, `model-selection`, `system-prompt`), and quoting that
        // back at the user would be noise.
        const source = event.data?.message?.source
        if (source?.kind !== 'user') break
        const text = blocksText(event.data?.message)
        if (text !== '') state.prompt = text
        break
      }
      case 'session/title': {
        const title = event.data?.title
        if (typeof title === 'string' && title !== '') state.title = title
        break
      }
      case 'assistant/message': {
        state.running = true
        const text = assistantText(event.data)
        if (text !== '') state.text = text
        break
      }
    }
  })

  // Fallback for titles that already existed before this plugin attached: a rename or a
  // generated title arrives as a durable `session/title` event, which the switch above
  // handles for free, but a session opened earlier keeps its title in the session record
  // and never re-emits it. The poll therefore only has work to do while some known session
  // is still untitled, and it stops scanning the moment each one is filled in.
  ctx.effect(
    () => {
      const id = setInterval(() => {
        let untitled = false
        for (const state of runtime.agents.values()) {
          if (state.title === '') { untitled = true; break }
        }
        // Nothing untitled: no session lookup at all, which keeps this timer inert for the
        // whole time the pet is simply sitting next to an already-named conversation.
        if (!untitled) return
        const live = liveSessions(ctx)
        let found = false
        for (const state of runtime.agents.values()) {
          if (state.title !== '') continue
          for (const session of live) {
            const title = titleOf(session)
            if (title !== '') { state.title = title; found = true; break }
          }
        }
        // Only when a title actually appeared: otherwise the push payload is unchanged and
        // re-sending it every 5 s would be exactly the kind of idle work this pet must not
        // do. A green bubble already on screen then catches up on the next real event.
        if (found) runtime.lastSent = ''
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

/** Concatenated text blocks of one message, flattened for the bubble. */
function blocksText(message) {
  const blocks = Array.isArray(message?.content) ? message.content : []
  let text = ''
  for (const block of blocks) {
    if (block?.type === 'text' && typeof block.text === 'string') text += block.text
  }
  return text
}

/**
 * The one-line report for an abnormal turn end, or '' when the reason has nothing to say.
 *
 * `blocked` and `max-tokens` carry no message of their own, so they get a fixed phrase:
 * the point of the red dot is that the turn stopped without an answer, and "why" only
 * has to be as specific as the reason allows.
 */
function failureText(reason, title) {
  const kind = reason?.kind
  const name = title || '会话'
  if (kind === 'error') {
    const detail = bubbleText(reason.error?.message)
    return detail === '' ? `“${name}”出错了` : detail
  }
  if (kind === 'blocked') return `“${name}”被中断了`
  if (kind === 'max-tokens') return `“${name}”超出长度上限`
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
  // Housekeeping first: a session with nothing left to say is dropped so the map does not
  // grow for the life of the process. This is the only mutation here; the decision itself
  // is a pure function so it can be tested without a live Harness.
  for (const [agentId, state] of runtime.agents) {
    if (now - state.touched > FORGET_AFTER_MS) runtime.agents.delete(agentId)
  }
  const decision = decideBubble(runtime, now)
  const payload = JSON.stringify({ t: 'state', mode: decision.mode, text: decision.text })
  if (payload === runtime.lastSent) return
  if (sendPet(runtime, { t: 'state', mode: decision.mode, text: decision.text })) runtime.lastSent = payload
}

/**
 * Decides what the bubble should show right now: `{ mode, text }`.
 *
 * Exported so the priority rules can be tested directly. They have regressed twice, and a
 * regex over the source cannot tell whether a session that went quiet for two minutes keeps
 * its bubble — only running the rule can.
 *
 * Priority, highest first: waiting (yellow) > error (red) > done (green) > working (plain).
 *
 * Error sits between waiting and done because it is the only state describing a session that
 * will not make progress on its own: a pending decision still outranks it (that one is
 * answerable right now), while a red dot must not be hidden by any number of green
 * completions — the user has to know something broke.
 *
 * Green outranks working on purpose. A session that just finished is the thing the user is
 * waiting to hear about, and it would otherwise be drowned out by any other session that
 * happens to be busy — the common case with several chats open.
 *
 * @param {{ agents: Map<string, { running: boolean, waiting: number, text: string, prompt: string, error: string, touched: number }>, doneText: string | null, doneAt: number }} state
 * @param {number} now - wall clock, in the same units as `touched` / `doneAt`.
 * @returns {{ mode: 'idle' | 'waiting' | 'error' | 'done' | 'working', text: string | undefined }}
 */
export function decideBubble(state, now) {
  let waitingAgent = null
  let runningAgent = null
  let errorAgent = null
  // Each direction compares against its own timestamp. Sharing one accumulator made the
  // running candidate inherit the waiting candidate's timestamp, so with no session waiting
  // the accumulator stayed at its initial value and every running session passed the
  // comparison — the last one inserted into the map won, whatever its actual activity. The
  // bubble then narrated a session the user had already left.
  let waitingTouched = -1
  let runningTouched = -1
  let errorTouched = -1
  for (const [agentId, session] of state.agents) {
    // A RUNNING session is never stale. The 60 s activity window exists so the bubble stops
    // narrating a session the user walked away from, and applying it to a running session was
    // what made the bubble vanish after a long silent stretch — a slow model, a long tool
    // call, a big file being read all look identical to "nothing is happening" from here,
    // while the turn is very much alive. A turn always ends with a durable `turn/end`, so
    // this cannot get stuck; a manual interrupt is `aborted`, which clears `running`.
    const fresh = now - session.touched <= ACTIVITY_WINDOW_MS
    if (!fresh && !session.running) continue
    if (session.waiting > 0 && session.touched > waitingTouched) {
      waitingAgent = agentId
      waitingTouched = session.touched
    }
    // An error is a per-session fact that stays true until that session starts again, so it is
    // selected from the session's own state rather than from a single global "last error":
    // two sessions can be broken at once, and the newest one is the one the user just caused.
    if (session.error !== '' && session.touched > errorTouched) {
      errorAgent = agentId
      errorTouched = session.touched
    }
    if (session.running && session.touched > runningTouched) {
      runningAgent = agentId
      runningTouched = session.touched
    }
  }

  if (waitingAgent !== null) {
    const session = state.agents.get(waitingAgent)
    return { mode: 'waiting', text: bubbleText(session.text) || '需要你确认一下' }
  }
  if (errorAgent !== null && now - state.agents.get(errorAgent).touched < ERROR_VISIBLE_MS) {
    const session = state.agents.get(errorAgent)
    return { mode: 'error', text: bubbleText(session.error) || '会话出错了' }
  }
  if (state.doneText !== null && now - state.doneAt < DONE_VISIBLE_MS) {
    return { mode: 'done', text: state.doneText }
  }
  if (runningAgent !== null) {
    // Real assistant copy is preferred, but before the model has produced any this turn the
    // bubble shows the prompt the user just sent. That is the same priority slot rather than a
    // lower tier, so the text does not flicker to a placeholder and back when the stream
    // starts.
    const session = state.agents.get(runningAgent)
    return {
      mode: 'working',
      text: bubbleText(session.text) || bubbleText(session.prompt) || '正在工作…',
    }
  }
  return { mode: 'idle', text: undefined }
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

// ——— reopen the desktop window ————————————————————————————————————————

// Ask the desktop shell to show its window by clicking its own tray icon.
//
// This runs in the plugin process, not in the pet, and that is the whole point. The
// sandbox starts the pet at low integrity; a low-integrity process is blocked by UIPI
// from reading the taskbar's XAML automation tree, so it sees five empty children with
// no tray buttons and every attempt from there fails silently. The plugin host runs at
// medium integrity, where the same query returns all thirty-one elements and the icon
// is invocable. Measured on this machine, both ways.
//
// PowerShell is used as the automation host because it ships with Windows, inherits
// this process's integrity, and keeps the plugin free of a compiled helper that would
// have to be built and shipped alongside it.
const TRAY_CLICK_SCRIPT = `
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$cond = New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty, 'Shell_TrayWnd')
$bar = $AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $bar) { exit 2 }
$all = $bar.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
foreach ($e in $all) {
  try { $cls = $e.Current.ClassName } catch { continue }
  if ($cls -notlike 'SystemTray.*') { continue }
  try { $nm = $e.Current.Name } catch { continue }
  if ($nm.Trim() -like 'DeepSeek Harness*') {
    $inv = $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $inv.Invoke()
    exit 0
  }
}
exit 1
`

function clickTrayIcon() {
  return new Promise((resolve) => {
    const powershell = join(
      process.env.SystemRoot ?? 'C:\\Windows',
      'System32',
      'WindowsPowerShell',
      'v1.0',
      'powershell.exe',
    )
    if (!existsSync(powershell)) {
      resolve(false)
      return
    }
    let child
    try {
      child = spawn(powershell, ['-NoProfile', '-NonInteractive', '-Command', TRAY_CLICK_SCRIPT], {
        windowsHide: true,
        stdio: 'ignore',
      })
    } catch {
      resolve(false)
      return
    }
    let settled = false
    const finish = (value) => {
      if (settled) return
      settled = true
      resolve(value)
    }
    const timer = setTimeout(() => {
      try {
        child.kill()
      } catch {
        /* already gone */
      }
      finish(false)
    }, 6000)
    timer.unref?.()
    child.on('error', () => {
      clearTimeout(timer)
      finish(false)
    })
    child.on('close', (code) => {
      clearTimeout(timer)
      finish(code === 0)
    })
  })
}

/**
 * Click the tray icon on the pet's behalf.
 *
 * Reached only when the pet has reported that the window is hidden and out of its reach, so
 * no visibility check is repeated here: the point of that message is that the cheap routes are
 * already exhausted.
 *
 * There is deliberately no second fallback after this. Asking the pet to try again would send
 * it back through the same "hidden" decision and straight into another tray click, which is a
 * loop that never converges.
 */
async function reopenHidden(ctx, runtime) {
  const clicked = await clickTrayIcon()
  if (!clicked) {
    ctx.logger?.warn?.('[dsh-lite-pet] the shell did not accept the tray click; the window may stay hidden')
  }
}

// ——— browser routes ————————————————————————————————————————————————————

function wireRoutes(ctx, runtime, profile) {
  // `connection` is acquired through `ctx.inject`, not a synchronous `ctx.get` here.
  // At `apply` time the service tree is still being assembled, so a `get` returns
  // undefined and every route silently never registers — the settings row then
  // renders normally but its fetch gets a 401 from the `/api` fence, which is what
  // "读取状态失败" looked like. `inject` runs the callback once the service exists,
  // and the returned fiber is disposed with the plugin.
  ctx.inject(['connection'], (scope) => {
    const connection = scope.get('connection')
    if (connection === undefined || connection === null) return () => {}
    const fetchRoutes = connection.fetch
    if (fetchRoutes === undefined || typeof fetchRoutes.register !== 'function') return () => {}

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

    const disposers = [
      fetchRoutes.register({
        path: STATE_ROUTE,
        methods: ['GET'],
        requestBody: 'buffered',
        fetch: () => Promise.resolve(json(snapshot())),
      }),
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
    ]
    return () => {
      for (const dispose of disposers) {
        try {
          if (typeof dispose === 'function') dispose()
        } catch {
          /* already gone */
        }
      }
    }
  })
}

function stopPet(runtime) {
  const child = runtime.pet
  runtime.pet = null
  runtime.ready = false
  runtime.mediaDir = null
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

/**
 * Brings a running pet onto the media directory it *should* be playing from.
 *
 * The pet reads its catalog once at startup, so a download that lands mid-session
 * would otherwise sit unused until the user happened to restart the Harness. The
 * exit handler respawns the process, so ending it here is the whole restart.
 */
function restartPetForMedia(ctx, runtime, config) {
  if (runtime.pet === null || runtime.mediaDir === null) return
  const wanted = activeMediaDir(config)
  if (wanted === runtime.mediaDir) return
  ctx.logger?.info?.('[dsh-lite-pet] switching the pet onto the full animation set')
  stopPet(runtime)
}










