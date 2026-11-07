#!/usr/bin/env node
/**
 * Checks the three things that silently shortened the animation library in 0.2.4:
 *
 *   1. `trustRoots()` must carry the certificates the machine trusts, not just
 *      Node's bundled set. Without this a TLS-terminating local GitHub accelerator
 *      makes every attempt die with UNABLE_TO_VERIFY_LEAF_SIGNATURE — measured on
 *      the machine this was written for, where both browsers work fine.
 *   2. `transfer()` must follow the release redirect, and must reject a body
 *      shorter than its Content-Length instead of unpacking a partial archive.
 *   3. `fetchAmbientSet()` must not latch a failure: the next tick after the
 *      backoff must try again.
 *
 * The transport is stubbed, so this runs offline and in CI. Pass --live to also
 * download the real release asset through the shipped code path.
 */
import { EventEmitter } from 'node:events'
import { mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, existsSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = resolve(fileURLToPath(new URL('..', import.meta.url)))
const plugin = await import('file:///' + join(ROOT, 'index.js').replace(/\\/g, '/'))
const failures = []

// ——— 1) trust roots ————————————————————————————————————————————————————

if (typeof plugin.trustRoots !== 'function') {
  failures.push('trustRoots is not exported')
} else {
  const roots = plugin.trustRoots()
  if (!Array.isArray(roots) || roots.length === 0) {
    failures.push('trustRoots returned nothing usable')
  } else {
    const { getCACertificates } = await import('node:tls')
    const bundled = getCACertificates('default').length
    if (roots.length < bundled) failures.push(`trustRoots (${roots.length}) dropped bundled CAs (${bundled})`)
    const unique = new Set(roots).size
    if (unique !== roots.length) failures.push('trustRoots returned duplicate certificates')
    console.log(`[ambient] trust roots: ${roots.length} (Node bundles ${bundled})`)
  }
}

// ——— 2) redirect + truncation through the real transfer() ————————————————

// A real archive built by the shipped packer, reused by the fallback check in
// section 3b so that "usable" means the extractor actually accepts it.
const ASSET_SCRATCH = mkdtempSync(join(tmpdir(), 'ambient-asset-'))
let ASSET = null
try {
  const { writeFileSync } = await import('node:fs')
  const { execFileSync } = await import('node:child_process')
  mkdirSync(join(ASSET_SCRATCH, 'src'), { recursive: true })
  writeFileSync(join(ASSET_SCRATCH, 'src', '001.webp'), Buffer.from('RIFF0000WEBPVP8 selftest-clip'))
  writeFileSync(
    join(ASSET_SCRATCH, 'src', 'index.json'),
    JSON.stringify({
      version: 3,
      tier: 'full',
      width: 640,
      bodyHeight: 360,
      animations: [{ id: '001', name: 'selftest', group: 'test', kind: 'action', file: '001.webp' }],
    }),
  )
  execFileSync(
    process.execPath,
    [
      join(ROOT, 'tools', 'pack-media.mjs'),
      '--full',
      join(ASSET_SCRATCH, 'src'),
      '--out',
      join(ASSET_SCRATCH, 'asset.zip'),
    ],
    { stdio: 'ignore' },
  )
  ASSET = readFileSync(join(ASSET_SCRATCH, 'asset.zip'))
} catch (error) {
  failures.push(`could not build a test archive: ${String(error?.message ?? error)}`)
} finally {
  rmSync(ASSET_SCRATCH, { recursive: true, force: true })
}

if (typeof plugin.transfer !== 'function') {
  failures.push('transfer is not exported')
} else {
  // A three-part fake socket: redirect -> payload, plus one lying about its length.
  const PAYLOAD = Buffer.alloc(64 * 1024, 7)
  const routes = {
    'https://example.invalid/redirect': { status: 302, headers: { location: '/asset.zip' } },
    'https://example.invalid/asset.zip': { status: 200, body: PAYLOAD },
    'https://example.invalid/short.zip': { status: 200, body: PAYLOAD.subarray(0, 1024), claimed: PAYLOAD.length },
  }
  const calls = []
  plugin.setTransferForTest((url, options, onResponse) => {
    calls.push({ url, options })
    const route = routes[url]
    const emitter = new EventEmitter()
    if (route === undefined) {
      emitter.statusCode = 404
      emitter.headers = {}
      process.nextTick(() => onResponse(emitter))
      process.nextTick(() => emitter.emit('end'))
      return emitter
    }
    emitter.statusCode = route.status
    emitter.headers = {
      ...(route.headers ?? {}),
      ...(route.body ? { 'content-length': route.claimed ?? route.body.length } : {}),
    }
    emitter.resume = () => {}
    process.nextTick(() => onResponse(emitter))
    if (route.body) {
      process.nextTick(() => {
        emitter.emit('data', route.body)
        emitter.emit('end')
      })
    }
    return { on() {}, setTimeout() {}, end() {}, destroy() {} }
  })

  try {
    const body = await plugin.transfer('https://example.invalid/redirect')
    if (!body.equals(PAYLOAD)) failures.push('the redirect was not followed to the payload')
    if (calls.length < 2) failures.push('transfer did not issue a second request for the redirect')
    const first = calls[0]
    if (first.options.method !== 'GET') failures.push('transfer did not use GET')
    if (!first.options.ca || first.options.ca.length === 0) {
      failures.push('transfer did not pass the merged trust roots to the request')
    }

    let truncated = null
    try {
      await plugin.transfer('https://example.invalid/short.zip')
    } catch (error) {
      truncated = String(error?.message ?? error)
    }
    if (truncated === null) failures.push('a truncated body was accepted as complete')
    else if (!/truncated/.test(truncated)) failures.push(`truncation failed for the wrong reason: ${truncated}`)
  } catch (error) {
    failures.push(`transfer check threw: ${String(error?.message ?? error)}`)
  } finally {
    plugin.setTransferForTest(null)
  }
}


// ——— 3) a failed download must be retried, and must fall back ————————

if (typeof plugin.fetchAmbientSetForTest !== 'function') {
  failures.push('fetchAmbientSetForTest is not exported')
} else {
  const scratch = mkdtempSync(join(tmpdir(), 'ambient-retry-'))
  const previousHome = process.env.DSH_HOME
  try {
    process.env.DSH_HOME = scratch
    let attempts = 0
    const urls = []
    plugin.setTransferForTest((url) => {
      attempts += 1
      urls.push(url)
      throw new Error('simulated network failure')
    })
    const runtime = { ambient: { status: 'absent', retryAt: 0 } }
    const ctx = { logger: { info() {}, warn() {} } }

    await plugin.fetchAmbientSetForTest(ctx, runtime, {})
    // One attempt per source: `latest` first, then the pinned tag. Both must be
    // tried before the download is declared failed, because `latest` 404s while a
    // just-published release is missing its asset.
    if (attempts !== 2) failures.push(`expected 2 attempts (one per source), saw ${attempts}`)
    if (urls.length === 2 && urls[0] === urls[1]) failures.push('both attempts used the same URL')
    if (urls.length === 2 && !/\/latest\//.test(urls[0])) {
      failures.push(`the first attempt should be the latest release, got ${urls[0]}`)
    }
    if (runtime.ambient.status !== 'failed') failures.push(`expected status failed, got ${runtime.ambient.status}`)
    if (!(runtime.ambient.retryAt > Date.now())) failures.push('a failure did not schedule a retry')

    // Inside the backoff window: no new attempt.
    await plugin.fetchAmbientSetForTest(ctx, runtime, {})
    if (attempts !== 2) failures.push(`attempted again inside the backoff window (${attempts} total)`)

    // Backoff elapsed: it must try again rather than latch.
    runtime.ambient.retryAt = 0
    await plugin.fetchAmbientSetForTest(ctx, runtime, {})
    if (attempts !== 4) failures.push(`did not retry after the backoff elapsed (${attempts} attempts)`)

    if (!existsSync(scratch)) failures.push('scratch home disappeared')
    else if (readdirSync(scratch).some((name) => name.endsWith('.part'))) {
      failures.push('a failed attempt left a .part directory behind')
    }

    // 3b) The fallback must actually be usable on its own: `latest` 404s, the
    // pinned tag serves a real archive, and the pet ends up with a catalog.
    let call = 0
    plugin.setTransferForTest((url, options, onResponse) => {
      call += 1
      const emitter = new EventEmitter()
      emitter.resume = () => {}
      if (/\/latest\//.test(url)) {
        emitter.statusCode = 404
        emitter.headers = {}
        process.nextTick(() => onResponse(emitter))
        process.nextTick(() => emitter.emit('end'))
      } else {
        emitter.statusCode = 200
        emitter.headers = { 'content-length': ASSET.length }
        process.nextTick(() => onResponse(emitter))
        process.nextTick(() => {
          emitter.emit('data', ASSET)
          emitter.emit('end')
        })
      }
      return { on() {}, setTimeout() {}, end() {}, destroy() {} }
    })
    const runtime2 = { ambient: { status: 'absent', retryAt: 0 } }
    await plugin.fetchAmbientSetForTest(ctx, runtime2, {})
    if (call !== 2) failures.push(`the fallback was not reached (${call} calls)`)
    if (runtime2.ambient.status !== 'ready') {
      failures.push(`the fallback did not produce a usable set (status ${runtime2.ambient.status})`)
    }
    const cacheDir = join(scratch, 'dsh-lite-pet-media')
    if (!existsSync(join(cacheDir, 'index.json'))) failures.push('the fallback did not write a cache catalog')
  } finally {
    plugin.setTransferForTest(null)
    if (previousHome === undefined) delete process.env.DSH_HOME
    else process.env.DSH_HOME = previousHome
    rmSync(scratch, { recursive: true, force: true })
  }
}

// ——— 4) optional live check ——————————————————————————————————————————

if (process.argv.includes('--live')) {
  const url =
    (process.env.DSH_LITE_PET_ASSET_BASE ??
      'https://github.com/Ckarefulon/dsh-lite-pet/releases/latest/download/') + 'dsh-lite-pet-media-full.zip'
  try {
    const body = await plugin.transfer(url)
    if (body.length < 100 * 1024 * 1024) failures.push(`live asset looks too small: ${body.length} bytes`)
    else console.log(`[ambient] live asset: ${(body.length / 1048576).toFixed(1)} MiB`)
  } catch (error) {
    failures.push(`live download failed: ${String(error?.message ?? error)}`)
  }
}


if (failures.length > 0) {
  for (const failure of failures) console.error(`[ambient] ${failure}`)
  process.exit(1)
}
console.log('[ambient] ok — system trust roots merged, redirects followed, truncation rejected, failures retried')
