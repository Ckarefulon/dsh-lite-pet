#!/usr/bin/env node
/**
 * Guards the three client/host wiring facts this round depended on.
 *
 *   1. The host must acquire `connection` through `ctx.inject`, never a synchronous
 *      `ctx.get` at apply time. Services are still being assembled then, so a `get`
 *      returns undefined, the routes never register, and the settings row renders
 *      fine while its fetch gets a 401 — which is what "读取状态失败" was.
 *
 *   2. The sidebar shortcut is DOM augmentation, so it must follow the discipline the
 *      official plugins use: an attribute-contains selector keyed on the CSS-module
 *      SUFFIX (the hash changes per build), inject-only, a full undo on dispose.
 *
 *   3. The settings row must stay the single in-app switch: exactly one
 *      `settings.general.item` registration.
 */
import { existsSync, readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = resolve(fileURLToPath(new URL('..', import.meta.url)))
const host = readFileSync(join(ROOT, 'index.js'), 'utf8')
const client = readFileSync(join(ROOT, 'client', 'index.js'), 'utf8')
const failures = []

// ——— 1) connection acquisition ————————————————————————————————————————

if (!/ctx\.inject\(\['connection'\]/.test(host)) {
  failures.push("index.js does not acquire connection through ctx.inject(['connection'])")
}
if (/ctx\.get\('connection'/.test(host)) {
  failures.push("index.js still reads connection synchronously; at apply time it is undefined")
}
// The routes must be registered inside that callback, not at module scope.
const injectIndex = host.indexOf("ctx.inject(['connection']")
const stateRouteIndex = host.indexOf('path: STATE_ROUTE')
if (injectIndex < 0 || stateRouteIndex < 0 || stateRouteIndex < injectIndex) {
  failures.push('the state route is registered outside the ctx.inject callback')
}

// ——— 2) DOM augmentation discipline ————————————————————————————————

// A hashed CSS-module class must never be matched literally.
if (/class="[a-zA-Z0-9]*_[a-zA-Z]+"/.test(client)) {
  failures.push('client/index.js matches a literal hashed class')
}
for (const suffix of ['SEARCH_SLOT_SUFFIX', 'HEADER_ACTIONS_SUFFIX', 'SECTION_HEADER_SUFFIX']) {
  if (!client.includes(suffix)) failures.push(`client/index.js does not use ${suffix}`)
}
if (!/class\*="\$\{/.test(client)) {
  failures.push('client/index.js does not use an attribute-contains selector for the host classes')
}
// Inject-only: an observer that disconnects, and a teardown that removes our node.
if (!/new MutationObserver\(/.test(client)) failures.push('client/index.js has no MutationObserver')
if (!/observer\.disconnect\(\)/.test(client)) failures.push('the observer is never disconnected')
if (!/data-dsh-lite-pet-header/.test(client)) {
  failures.push('the injected node is not tagged with a data-dsh-lite-pet-* attribute')
}
if (!/removeChild\(button\)/.test(client)) failures.push('the injected button is never removed on dispose')
// It must insert BEFORE the header actions group (the "left of 搜索" seat).
if (!/insertBefore\(button, actions\)/.test(client)) {
  failures.push('the button is not inserted immediately before the header actions group')
}

// ——— 3) exactly one in-app switch ——————————————————————————————————

const itemRegs = (client.match(/name: 'settings\.general\.item'/g) ?? []).length
if (itemRegs !== 1) failures.push(`expected exactly 1 settings.general.item registration, found ${itemRegs}`)

// ——— 4) the plugin icon exists and is a real PNG ————————————————

const iconPath = join(ROOT, 'assets', 'icon.png')
if (!existsSync(iconPath)) {
  failures.push('assets/icon.png is missing')
} else {
  const head = readFileSync(iconPath).subarray(0, 8)
  const pngSignature = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a])
  if (!head.equals(pngSignature)) failures.push('assets/icon.png is not a PNG')
}

if (failures.length > 0) {
  for (const failure of failures) console.error(`[wiring] ${failure}`)
  process.exit(1)
}
console.log('[wiring] ok — connection via inject, suffix-matched augmentation with undo, one switch, icon present')
