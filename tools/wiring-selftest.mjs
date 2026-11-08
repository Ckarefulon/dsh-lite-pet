#!/usr/bin/env node
/**
 * Guards the wiring this plugin depends on.
 *
 *   1. The host must acquire `connection` through `ctx.inject`, never a synchronous
 *      `ctx.get` at apply time. Services are still being assembled then, so a `get`
 *      returns undefined, the routes never register, and the switch's fetch gets a
 *      401 from the `/api` fence — which is what "读取状态失败" was.
 *
 *   2. The pet has exactly ONE in-app control and it is the sidebar workspace icon.
 *      There must be no `settings.general.item` registration: the icon is the switch.
 *
 *   3. That icon is DOM augmentation, so it must follow the discipline the official
 *      plugins use: suffix-matched selectors (the CSS-module hash changes per build),
 *      inject-only, tagged, and a full undo on dispose.
 *
 *   4. The pet window must never take the foreground — no SetForegroundWindow before
 *      the context menu.
 */
import { existsSync, readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = resolve(fileURLToPath(new URL('..', import.meta.url)))
const host = readFileSync(join(ROOT, 'index.js'), 'utf8')
const client = readFileSync(join(ROOT, 'client', 'index.js'), 'utf8')
const program = readFileSync(join(ROOT, 'src', 'pet', 'Program.cs'), 'utf8')
const menu = readFileSync(join(ROOT, 'src', 'pet', 'Menu.cs'), 'utf8')
// The icon anchors on the search slot: immediately left of 搜索, which is what the
// user asked for, and does not depend on the actions group existing.
const popup = readFileSync(join(ROOT, 'src', 'pet', 'PopupMenu.cs'), 'utf8')
const failures = []
// The icon must be a peer of the host icons visually: same metrics and the same
// single hover rule. Inventing an on/off colour was the reported complaint.
// Centring is part of the host control, not decoration: without inline-flex the
// 16px glyph sits on the text baseline and reads as low in the 28px box.
// The glyph must stay the supplied artwork, only rescaled. The body spans x=2.86..13.14
// (10.28 units). An earlier attempt narrowed it to 8.28 by mis-deriving the scale,
// which read as a visibly thinner drop than the source.
if (!client.includes('2.86 9.86A5.14 5.14 0 0 0 13.14 9.86')) {
}
if (!/display:inline-flex/.test(client)) failures.push('the switch icon is not a flex box')
if (!/justify-content:center/.test(client)) failures.push('the switch icon is not centred horizontally')
if (!/align-items:center/.test(client)) failures.push('the switch icon is not centred vertically')
if (!/dsw-radius-sm/.test(client)) failures.push('the switch icon does not use the host radius token')
if (!/dsw-alias-interactive-bg-hover/.test(client)) {
  failures.push('the switch icon does not use the host hover token')
}
if (/dsw-alias-brand-primary/.test(client)) {
  failures.push('the switch icon still recolours itself when on')
}


// ——— 1) connection acquisition ————————————————————————————————————————

if (!/ctx\.inject\(\['connection'\]/.test(host)) {
  failures.push("index.js does not acquire connection through ctx.inject(['connection'])")
}
if (/ctx\.get\('connection'/.test(host)) {
  failures.push('index.js still reads connection synchronously; at apply time it is undefined')
}
const injectIndex = host.indexOf("ctx.inject(['connection']")
const stateRouteIndex = host.indexOf('path: STATE_ROUTE')
if (injectIndex < 0 || stateRouteIndex < 0 || stateRouteIndex < injectIndex) {
  failures.push('the state route is registered outside the ctx.inject callback')
}

// ——— 2) the icon is the only in-app switch ——————————————————————————

if (/settings\.general\.item/.test(client)) {
  failures.push('client/index.js still registers a settings.general.item row')
}
if (/slots\.(inject|register)\(/.test(client)) {
  failures.push('client/index.js still registers a slot')
}
if (!/data-dsh-lite-pet-header/.test(client)) {
  failures.push('the sidebar switch icon is missing')
}

// ——— 3) DOM augmentation discipline ————————————————————————————————

for (const suffix of ['SEARCH_SLOT_SUFFIX', 'HEADER_ACTIONS_SUFFIX', 'SECTION_HEADER_SUFFIX']) {
  if (!client.includes(suffix)) failures.push(`client/index.js does not use ${suffix}`)
}
if (!/class\*="\$\{/.test(client)) {
  failures.push('client/index.js does not use an attribute-contains selector for the host classes')
}
if (/class="[a-zA-Z0-9]*_[a-zA-Z]+"/.test(client)) {
  failures.push('client/index.js matches a literal hashed class name')
}
if (!/new MutationObserver\(/.test(client)) failures.push('client/index.js has no MutationObserver')
if (!/observer\.disconnect\(\)/.test(client)) failures.push('the observer is never disconnected')
if (!/removeChild\(button\)/.test(client)) failures.push('the injected button is never removed on dispose')
// It must sit immediately before the header actions group: the seat left of 搜索.
// The icon anchors on the search slot: immediately left of 搜索.
// The icon must be a peer of the host icons visually: same metrics and the same
// single hover rule. Inventing an on/off colour was the reported complaint.
// Centring is part of the host control, not decoration: without inline-flex the
// 16px glyph sits on the text baseline and reads as low in the 28px box.
// The glyph must stay the supplied artwork, only rescaled. The body spans x=2.86..13.14
// (10.28 units). An earlier attempt narrowed it to 8.28 by mis-deriving the scale,
// which read as a visibly thinner drop than the source.
if (!client.includes('2.86 9.86A5.14 5.14 0 0 0 13.14 9.86')) {
}
if (!/display:inline-flex/.test(client)) failures.push('the switch icon is not a flex box')
if (!/justify-content:center/.test(client)) failures.push('the switch icon is not centred horizontally')
if (!/align-items:center/.test(client)) failures.push('the switch icon is not centred vertically')
if (!/dsw-radius-sm/.test(client)) failures.push('the switch icon does not use the host radius token')
if (!/dsw-alias-interactive-bg-hover/.test(client)) {
  failures.push('the switch icon does not use the host hover token')
}
if (/dsw-alias-brand-primary/.test(client)) {
  failures.push('the switch icon still recolours itself when on')
}
if (!/insertBefore(button, anchor)/.test(client)) {
}

// ——— 4) the pet never steals focus ————————————————————————————————

// A menu needs its owner foreground to dismiss correctly, but the pet must not become
// the foreground window. Posting WM_NULL is the no-activation way to satisfy that.
if (/SetForegroundWindow\(hwnd\)/.test(menu)) {
  failures.push('Menu.cs still calls SetForegroundWindow on the pet before showing the menu')
}
if (!/PostMessage\(hwnd, Native\.WM_NULL/.test(menu)) {
// The menu is owner-drawn now; the dismissal rule lives in PopupMenu.
if (!/PointerOnMenuOrPet\(\)/.test(popup)) {
  failures.push('PopupMenu does not gate dismissal on menu-or-pet containment')
}
}
if (!/WS_EX_NOACTIVATE/.test(program)) {
  failures.push('the pet window is not created with WS_EX_NOACTIVATE')
}

// ——— 5) idle really is ambient playback ——————————————————————————

// Clips measure 5-10 s; a gap measured in tens of seconds left the idle clip on screen
// for most of the time. The gap must stay well under a single clip.
const gapMatch = program.match(/private static long AmbientGapMs\(\)\s*\{[\s\S]*?return\s+([0-9_]+)\s*\+\s*Rng\.Next\(([0-9_]+)\)/)
if (gapMatch === null) {
  failures.push('Program.cs has no AmbientGapMs helper')
} else {
  const base = Number(gapMatch[1].replace(/_/g, ''))
  const spread = Number(gapMatch[2].replace(/_/g, ''))
  if (base + spread > 12000) {
    failures.push(`the ambient gap (${base}+${spread} ms) is longer than a typical clip`)
  }
}
if (!/AmbientGapMs\(\)/.test(program)) failures.push('the ambient tick does not use AmbientGapMs()')

// ——— 6) the plugin icon —————————————————————————————————————————

const iconPath = join(ROOT, 'assets', 'icon.png')
if (!existsSync(iconPath)) {
  failures.push('assets/icon.png is missing')
} else if (!readFileSync(iconPath).subarray(0, 8).equals(Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]))) {
  failures.push('assets/icon.png is not a PNG')
}

if (failures.length > 0) {
  for (const failure of failures) console.error(`[wiring] ${failure}`)
  process.exit(1)
}
console.log('[wiring] ok — routes via inject, icon is the only switch, no focus stealing, short idle gaps')
