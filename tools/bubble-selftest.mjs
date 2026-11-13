#!/usr/bin/env node
/**
 * Guards the bubble's state model. Every rule here came from a report, and every one of
 * them can be broken again by an innocuous-looking edit, so each is pinned to the shape of
 * the code rather than to a description of it.
 *
 *   1. Before the model has replied, the bubble shows the PROMPT. Empty bubbles at turn
 *      start looked like the pet had missed the message.
 *   2. Green (done) shows the session TITLE.
 *   3. Red (error) exists, and its priority is between yellow and green.
 *   4. A RUNNING session never goes stale. The 60 s activity window is for abandoned
 *      sessions; applying it to a running one made the bubble vanish during any slow turn.
 *   5. The stacking order is an invariant, not a per-call raise: pet at the back, bubble
 *      above it, menu in front.
 */
import { readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { decideBubble } from '../index.js'

const ROOT = resolve(fileURLToPath(new URL('..', import.meta.url)))
const host = readFileSync(join(ROOT, 'index.js'), 'utf8')
const program = readFileSync(join(ROOT, 'src', 'pet', 'Program.cs'), 'utf8')
const bubble = readFileSync(join(ROOT, 'src', 'pet', 'BubbleWindow.cs'), 'utf8')
const canvas = readFileSync(join(ROOT, 'src', 'pet', 'PetCanvas.cs'), 'utf8')
const layered = readFileSync(join(ROOT, 'src', 'pet', 'LayeredPopup.cs'), 'utf8')
const failures = []

const fail = (message) => failures.push(message)

/**
 * Returns the brace-matched body of the declaration whose text starts with `needle`.
 *
 * The needle must include the return type (C#) or the `function` keyword (JS): matching on
 * the bare name finds the CALL site instead, and the call site's enclosing block is then
 * returned as if it were the function body.
 */
function functionBody(source, needle) {
  const start = source.indexOf(needle)
  if (start < 0) return null
  const open = source.indexOf('{', start + needle.length)
  if (open < 0) return null
  let depth = 0
  for (let i = open; i < source.length; i++) {
    if (source[i] === '{') depth++
    else if (source[i] === '}') {
      depth--
      if (depth === 0) return source.slice(open, i + 1)
    }
  }
  return null
}

// ——— Behaviour: run the real decision function ————————————————————————

const NOW = 1_000_000
/** Runs decideBubble over literal session states. */
function decide(sessions, extra = {}, now = NOW) {
  return decideBubble({ agents: new Map(Object.entries(sessions)), doneText: null, doneAt: 0, ...extra }, now)
}
const one = (fields) => ({ running: false, waiting: 0, text: '', prompt: '', error: '', touched: NOW, ...fields })

/** Asserts a decision; reports the whole object so a failure is diagnosable. */
function expect(label, actual, mode, text) {
  const modeOk = actual.mode === mode
  const textOk = text === undefined ? true : actual.text === text
  if (modeOk && textOk) return
  fail(`${label}: expected ${JSON.stringify({ mode, text })}, got ${JSON.stringify(actual)}`)
}

// The prompt stands in until the model replies, and real copy takes over afterwards.
expect('prompt-only while working', decide({ a: one({ running: true, prompt: '我发的那句话' }) }), 'working', '我发的那句话')
expect('assistant copy wins over the prompt', decide({ a: one({ running: true, prompt: '我发的那句话', text: '模型正在说的话' }) }), 'working', '模型正在说的话')
expect('placeholder only when there is nothing', decide({ a: one({ running: true }) }), 'working', '正在工作…')

// A long silent turn keeps its bubble. This is the regression that made it vanish.
const longSilence = now => ({ a: one({ running: true, prompt: '很慢的一次生成', touched: now - 10 * 60_000 }) })
expect('running after 10 silent minutes', decide(longSilence(NOW), {}, NOW), 'working', '很慢的一次生成')
expect('running after 5 silent hours', decide(longSilence(NOW), {}, NOW), 'working', '很慢的一次生成')
// ...but a NON-running session that went quiet stops narrating.
expect('idle session goes quiet', decide({ a: one({ text: '旧消息', touched: NOW - 5 * 60_000 }) }), 'idle', undefined)

// Priority: waiting > error > done > working.
const both = {
  w: one({ running: true, waiting: 1, text: '等确认', touched: NOW }),
  e: one({ error: '炸了', touched: NOW - 10 }),
  d: one({ running: true, text: '在干活', touched: NOW - 20 }),
}
expect('yellow beats red and green and working', decide(both, { doneText: '某个标题', doneAt: NOW }), 'waiting', '等确认')
expect('red beats green and working', decide({ e: both.e, d: both.d }, { doneText: '某个标题', doneAt: NOW }), 'error', '炸了')
expect('green beats working', decide({ d: both.d }, { doneText: '某个标题', doneAt: NOW }), 'done', '某个标题')
expect('working when nothing else', decide({ d: both.d }, { doneText: null }), 'working', '在干活')
expect('everything idle', decide({}), 'idle', undefined)

// Red is a per-session fact and clears on the next turn, so the newest error wins.
expect('newest error wins', decide({
  old: one({ error: '旧故障', touched: NOW - 5000 }),
  fresh: one({ error: '刚坏的', touched: NOW }),
}), 'error', '刚坏的')
expect('a restarted turn clears the red dot', decide({ a: one({ running: true, error: '', prompt: '重试中' }) }), 'working', '重试中')
// The green and red bubbles expire; a long-abandoned notice must not stick forever.
expect('red expires', decide({ e: one({ error: '炸了', touched: NOW - 13 * 60_000 }) }), 'idle', undefined)
expect('green expires', decide({}, { doneText: '某标题', doneAt: NOW - 13 * 60_000 }), 'idle', undefined)

// ——— Source contracts that the behaviour above cannot see ——————————————

// Only a real user message may become the bubble text: the turn also carries injected
// context (`runtime-context`, `system-prompt`), and quoting that back is noise.
if (!/source\?\.kind !== 'user'/.test(host)) {
  fail('user/message is accepted without checking the source, so injected context is quoted back')
}
// "绿点时内容为会话标题": the text IS the title. Wrapping it in quotes plus a completion
// suffix pushed the title itself towards the ellipsis.
if (!/runtime\.doneText = state\.title/.test(host)) {
  fail('the done bubble does not show the session title as its text')
}
if (/已完成`/.test(host)) {
  fail('the done bubble still wraps the title in a “…”已完成 sentence')
}
if (!/case 'session\/title'/.test(host)) {
  fail('the host does not read session/title, so a renamed session keeps its first title')
}
// An abnormal end paints; a manual interrupt must NOT. `aborted` is the interrupt, so
// treating it as an error would leave a red bubble every time the user stops a session.
{
  const end = functionBody(host, "case 'turn/end'") ?? ''
  if (!/kind === 'error' \|\| kind === 'blocked' \|\| kind === 'max-tokens'/.test(host)) {
    fail('no abnormal-end kinds set the error state')
  }
  if (/'aborted'/.test(end)) {
    fail("'aborted' is treated as an abnormal end, so interrupting a session paints it red")
  }
  if (!/'turn\/start'/.test(host) || !/state\.error = ''/.test(host)) {
    fail('a new turn does not clear the previous error, so the red dot never goes away')
  }
}
if (/'interrupted'/.test(functionBody(host, 'function failureText(') ?? '')) {
  fail('failureText treats a shell restart artefact as a user-visible error')
}

// ——— 5) the stacking order is an invariant, not a per-tick repair ——————

// Ownership is the mechanism that makes the bubble immune to the pet being raised at all:
// an owned window is always above its owner, so a drag cannot bury it for a tick. If the
// bubble is ever created without the pet as its owner, the flicker comes back.
if (!/internal void Create\(IntPtr owner\)/.test(bubble)) {
  fail('BubbleWindow.Create no longer takes the pet as owner, so nothing keeps it above the pet')
}
if (!/CreateWindow\(Width, _height, owner\)/.test(bubble)) {
  fail("BubbleWindow.Create does not pass the owner through to CreateWindowEx")
}
if (!/_bubble\.Create\(_hwnd\)/.test(program)) {
  fail('the bubble is not created as an owned window of the pet')
}
// A band-wide raise is order-unstable by construction: it demotes everything that is not
// raised in the same breath. That is what made the bubble blink through the pet during a
// drag, so neither satellite may reach for the front of the band any more.
if (/HWND_TOPMOST/.test(bubble)) {
  fail('BubbleWindow still raises itself to the front of the topmost band')
}
{
  const popup = readFileSync(join(ROOT, 'src', 'pet', 'PopupMenu.cs'), 'utf8')
  if (/internal void RaisePopup\(\)[\s\S]{0,200}SetWindowPos/.test(popup)) {
    fail('the menu still raises itself with a band-wide raise instead of asking the pet to order it')
  }
  if (!/PlaceInFront\(\)/.test(popup)) {
    fail('the menu never asks for its place in the stack, so it can open behind the bubble')
  }
  if (!/internal IntPtr HandleWhenOpen/.test(popup)) {
    fail('the menu hides its handle, so the pet cannot order it against the bubble')
  }
}
// The relative placement must exist and be installed by the pet, which is the only window
// that knows all three handles.
if (!/private static bool PlacePetBehind\(IntPtr above\)/.test(program)) {
  fail('Program has no relative z-order placement, so ordering stays a per-tick race')
}
if (!/LayeredPopup\.RestackRequest = Restack/.test(program)) {
  fail('the restack is never installed, so a satellite cannot get its place restored')
}
if (!/Action restack = RestackRequest;[\s\S]{0,120}restack\(\); return;/.test(layered)) {
  fail('LayeredPopup.PlaceInFront does not ask the pet to restate the order')
}
// Dragging must not raise the pet: every such raise used to put it in front of its bubble.
{
  const drag = functionBody(program, 'private static void UpdateDrag(') ?? ''
  if (/SetWindowPos\(_hwnd, \(IntPtr\)\(-1\)/.test(drag)) {
    fail('the drag still raises the pet to the front of the band on every move')
  }
  if (!/Restack\(\)/.test(drag)) {
    fail('dragging the pet does not restate the stacking order afterwards')
  }
}

// ——— 6) the red dot exists on the C# side too ————————————————————

if (!/Error = 3/.test(canvas)) fail('BubbleDot has no Error member')
if (!/BubbleDot\.Error/.test(bubble)) fail('the bubble cannot paint a red dot')
if (!/_mode == "error"/.test(program)) fail('the pet has no error mode branch')
if (!/work-error/.test(program)) fail('the pet does not play the error reaction')

// ——— 7) a click acknowledges the red bubble ————————————————————

// Clicking the pet already clears the green bubble; the red one has to go with it, or the
// pet keeps reporting a failure the user has just gone to look at.
{
  const open = functionBody(host, 'function handlePetLine(') ?? ''
  const click = open.slice(open.indexOf("message?.t === 'open'"))
  if (!/state\.error = ''/.test(click.slice(0, 900))) {
    fail('clicking the pet does not clear the error state, so the red dot outlives the acknowledgement')
  }
}

if (failures.length > 0) {
  for (const failure of failures) console.error(`[bubble] ${failure}`)
  process.exit(1)
}
console.log('[bubble] ok — prompt stands in until the reply, title on green, red between yellow and green, running never stale, stack ordered')
