#!/usr/bin/env node
/**
 * Runs the stacking probe: builds tools/stack-probe/StackProbe.cs with the .NET Framework
 * compiler and executes it.
 *
 * The probe creates three topmost layered windows, reproduces the reported triggers (a drag,
 * a bubble repaint, a scale change), and asserts that each one breaks the order on its own
 * and that Restack repairs it. It runs on the real desktop, which is the only place the
 * topmost band can be observed — reordering it inside one process proves nothing.
 *
 * Windows-only, and it flashes three small windows for a moment.
 */
import { spawnSync } from 'node:child_process'
import { existsSync, mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = resolve(fileURLToPath(new URL('..', import.meta.url)))
const SOURCE = join(ROOT, 'tools', 'stack-probe', 'StackProbe.cs')

if (process.platform !== 'win32') {
  console.log('[stack] skipped — the topmost band can only be measured on Windows')
  process.exit(0)
}

function findCompiler() {
  if (process.env.CSC) return process.env.CSC
  const windir = process.env.WINDIR || 'C:\\Windows'
  return [
    join(windir, 'Microsoft.NET', 'Framework64', 'v4.0.30319', 'csc.exe'),
    join(windir, 'Microsoft.NET', 'Framework', 'v4.0.30319', 'csc.exe'),
  ].find((path) => existsSync(path)) ?? null
}

const csc = findCompiler()
if (csc === null) {
  console.log('[stack] skipped — no .NET Framework compiler, so the native probe cannot build')
  process.exit(0)
}

const work = mkdtempSync(join(tmpdir(), 'dsh-lite-pet-stack-'))
const exe = join(work, 'StackProbe.exe')
try {
  const build = spawnSync(csc, ['/nologo', '/target:exe', `/out:${exe}`, SOURCE], {
    stdio: 'inherit',
    windowsHide: true,
  })
  if (build.status !== 0) {
    console.error('[stack] probe did not compile')
    process.exit(1)
  }
  const run = spawnSync(exe, [], { stdio: 'inherit', windowsHide: true })
  process.exit(run.status ?? 1)
} finally {
  rmSync(work, { recursive: true, force: true })
}
