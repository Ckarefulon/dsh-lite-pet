#!/usr/bin/env node
/**
 * Guards the two playback bugs that made the pet look broken:
 *
 *   1. Menu command ids must be unique across the whole 动作 tree. They used to be
 *      `AnimationBase + index` with `index` restarting per group, so every group's
 *      first entry was id 100 and the dictionary kept only the last group's clip —
 *      picking any group's first item played the balance animation instead.
 *
 *   2. Ambient selection must be a shuffled bag, not a fresh random index. A redraw
 *      per pick let short early clips come up again and again while others waited.
 *
 * The C# side is checked by reading the shipped sources, so this fails if the bug is
 * reintroduced rather than if are-implementation drifts.
 */
import { readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = resolve(fileURLToPath(new URL('..', import.meta.url)))
const menu = readFileSync(join(ROOT, 'src', 'pet', 'Menu.cs'), 'utf8')
const program = readFileSync(join(ROOT, 'src', 'pet', 'Program.cs'), 'utf8')
const failures = []

// ——— 1) menu command ids ————————————————————————————————————————————

if (/AnimationBase\s*\+\s*index/.test(menu)) {
  failures.push('Menu.cs still builds command ids as AnimationBase + a per-group index')
} else if (!/nextCommand\+\+/.test(menu)) {
  failures.push('Menu.cs does not allocate a unique command id per entry')
}

// `_commands.Clear()` inside Show is harmless, but the map must be keyed by that id.
if (!/_commands\[command\]\s*=\s*entry/.test(menu)) {
  failures.push('Menu.cs does not record the entry under its unique command id')
}

// ——— 2) fair ambient selection ——————————————————————————————————————

if (/pool\[Rng\.Next\(pool\.Count\)\]/.test(program)) {
  failures.push('Program.cs still picks an ambient clip by redrawing a random index')
}
if (!/ActionBag/.test(program)) failures.push('Program.cs has no shuffled bag for ambient clips')
// Fisher-Yates is what makes a bag a permutation rather than "mostly random".
if (!/for\s*\(int\s+i\s*=\s*ActionBag\.Count\s*-\s*1;\s*i\s*>\s*0;\s*i--\)/.test(program)) {
  failures.push('Program.cs does not shuffle the bag with Fisher-Yates')
}

// ——— 3) the idle state push must not cut an ambient clip short ——————

/**
 * Returns the body of the multi-line `if (_mode == "<mode>") { ... }`, brace-matched.
 *
 * Two traps this avoids:
 *   - a lazy regex stops at the first inner `}`, so the branch looks truncated;
 *   - the same test appears earlier in one-line form inside OnClipEnd
 *     (`if (_mode == "working") { PlayKind(...); return; }`), and matching that
 *     would check the wrong block entirely.
 */
function branchOf(mode) {
  const needle = `if (_mode == "${mode}")`
  let from = 0
  while (true) {
    const start = program.indexOf(needle, from)
    if (start < 0) return null
    const afterParen = program.indexOf(')', start) + 1
    // A one-line branch closes on the same line it opened.
    const open = program.indexOf('{', afterParen)
    const newline = program.indexOf('\n', open)
    const close = program.indexOf('}', open)
    if (close >= 0 && close < newline) {
      from = close + 1
      continue // one-liner: keep looking for the state handler
    }
    let depth = 0
    for (let i = open; i < program.length; i++) {
      if (program[i] === '{') depth++
      else if (program[i] === '}') {
        depth--
        if (depth === 0) return program.slice(open, i + 1)
      }
    }
    return null
  }
}

// The host pushes state every 250 ms. If the idle branch forces idle whenever the
// current kind is not idle, every ambient clip dies the instant it starts.
const idleBranch = branchOf('idle')
if (idleBranch === null) {
  failures.push('could not find the idle branch in Program.cs')
} else if (!/_playingAmbient/.test(idleBranch)) {
  failures.push('the idle branch forces idle without checking whether an ambient clip is playing')
}

// Ambient must be retried from the tick, not only at the end of an idle loop.
if (!/Ambient playback is driven from the tick/.test(program)) {
  failures.push('the ambient trigger is not driven from the tick')
}

// ——— 4) work must outrank ambient ——————————————————————————————————

for (const mode of ['working', 'waiting', 'done']) {
  const branch = branchOf(mode)
  if (branch === null) {
    failures.push(`could not find the ${mode} branch`)
  } else if (!/_playingAmbient\s*=\s*false/.test(branch)) {
    failures.push(`the ${mode} branch does not clear the ambient flag, so it may be skipped`)
  }
}

if (failures.length > 0) {
  for (const failure of failures) console.error(`[playback] ${failure}`)
  process.exit(1)
}
console.log('[playback] ok — unique menu ids, shuffled bag, ambient survives state pushes, work outranks ambient')
