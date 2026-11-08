#!/usr/bin/env node
// Verify the cascade rule: no panel may overlap a panel that is not its immediate parent.
//
// Adjacent levels touch by design — a submenu is anchored to its parent row and their
// rectangles meet at the shared edge, exactly like a native menu. What must never happen
// is a level-3 panel landing on level 1, because then the pointer sits over two panels at
// once and the deeper one is dismissed as "you are browsing level 1". That was the
// reported bug, and it happened because each level chose its own direction.
//
// This mirrors CascadeSubmenus exactly (same expressions) so a regression shows up here.

const W = 1920, H = 1080
const work = { left: 0, top: 0, right: W, bottom: H }
const PadX = 5
const PadY = 5
const ItemHeight = 29
const PanelH = 200

function placeChain(rootX, rootY, widths) {
  const panels = [{ x: rootX, y: rootY, w: widths[0], h: PanelH, parent: -1 }]
  const widest = Math.max(...widths)
  const openLeft = rootX + widths[0] + widest > work.right

  for (let i = 1; i < widths.length; i++) {
    const parent = panels[i - 1]
    const w = widths[i]
    let x = openLeft ? parent.x - w + PadX : parent.x + parent.w - PadX
    let y = parent.y + ItemHeight * i - PadY
    if (y + PanelH > work.bottom) y = Math.max(work.top, work.bottom - PanelH)
    if (y < work.top) y = work.top
    if (x < work.left) x = work.left
    if (x + w > work.right) x = Math.max(work.left, work.right - w)
    panels.push({ x, y, w, h: PanelH, parent: i - 1 })
  }
  return { panels, openLeft }
}

function overlaps(a, b) {
  // Treat shared edges as non-overlapping; require a real area intersection.
  const ix = Math.min(a.x + a.w, b.x + b.w) - Math.max(a.x, b.x)
  const iy = Math.min(a.y + a.h, b.y + b.h) - Math.max(a.y, b.y)
  return ix > 1 && iy > 1
}

const failures = []
let checked = 0

for (const rootX of [0, 200, 900, 1500, 1780, W - 140]) {
  for (const widths of [
    [140, 140, 140],
    [200, 320, 160],
    [320, 160, 300],
    [140, 380, 120],
  ]) {
    const { panels, openLeft } = placeChain(rootX, 300, widths)
    for (let i = 0; i < panels.length; i++) {
      for (let j = i + 1; j < panels.length; j++) {
        if (panels[j].parent === i || panels[i].parent === j) continue // neighbours may touch
        checked++
        if (overlaps(panels[i], panels[j])) {
          failures.push(
            `rootX=${rootX} widths=${widths.join('/')} openLeft=${openLeft}: level ${i} overlaps level ${j}`,
          )
        }
      }
    }
  }
}

// The original defect: per-level direction choice. Reproduce it to prove the test bites.
function placeChainPerLevel(rootX, rootY, widths) {
  const panels = [{ x: rootX, y: rootY, w: widths[0], h: PanelH }]
  for (let i = 1; i < widths.length; i++) {
    const parent = panels[i - 1]
    const w = widths[i]
    let x = parent.x + parent.w - PadX
    if (x + w > work.right) x = Math.max(work.left, parent.x - w + PadX) // flips per level
    const y = parent.y + ItemHeight * i - PadY
    panels.push({ x, y, w, h: PanelH })
  }
  return panels
}

let oldBugFound = false
for (const rootX of [1500, 1780]) {
  const panels = placeChainPerLevel(rootX, 300, [140, 380, 120])
  if (overlaps(panels[0], panels[2])) oldBugFound = true
}
if (!oldBugFound) {
  failures.push('the per-level-direction variant no longer reproduces the bug — test lost its teeth')
}

if (failures.length > 0) {
  for (const f of failures) console.error(`[cascade] ${f}`)
  process.exit(1)
}
console.log(`[cascade] ok — ${checked} non-adjacent pairs, no overlap; old rule still reproduces the bug`)
