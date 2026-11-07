#!/usr/bin/env node
/**
 * Builds runtime/pet/DshLitePet.exe with the .NET Framework compiler that ships
 * with Windows. No SDK, no NuGet, no MSBuild: the pet only needs System.Drawing
 * and the Media Foundation / Win32 DLLs that are part of the OS.
 */
import { spawnSync } from 'node:child_process'
import { copyFileSync, existsSync, mkdirSync, readdirSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const SOURCE_DIR = join(ROOT, 'src', 'pet')
const OUT_DIR = join(ROOT, 'runtime', 'pet')
const OUT = join(OUT_DIR, 'DshLitePet.exe')

function findCompiler() {
  if (process.env.CSC) return process.env.CSC
  const windir = process.env.WINDIR || 'C:\\Windows'
  const candidates = [
    join(windir, 'Microsoft.NET', 'Framework64', 'v4.0.30319', 'csc.exe'),
    join(windir, 'Microsoft.NET', 'Framework', 'v4.0.30319', 'csc.exe'),
  ]
  return candidates.find((path) => existsSync(path)) ?? null
}

/**
 * Resolves a .NET Framework assembly that ships in the GAC. UIAutomationClient is what
 * makes the tray icon reachable, and it is part of the framework on every Windows 10/11
 * install, but the reference assemblies are only present with an SDK — so reference the
 * GAC copy directly, which is also what the loader uses at run time.
 */
function findGacAssembly(name) {
  const windir = process.env.WINDIR || 'C:\\Windows'
  const root = join(windir, 'Microsoft.NET', 'assembly', 'GAC_MSIL', name)
  if (!existsSync(root)) return null
  for (const versionDir of readdirSync(root)) {
    const candidate = join(root, versionDir, `${name}.dll`)
    if (existsSync(candidate)) return candidate
  }
  return null
}

const csc = findCompiler()
if (csc === null) {
  console.error('[pet] csc.exe (.NET Framework 4.x) not found — the pet helper only builds on Windows')
  process.exit(1)
}

const uiaAssemblies = ['UIAutomationClient', 'UIAutomationTypes', 'WindowsBase']
  .map(findGacAssembly)
const hasUia = uiaAssemblies.every((path) => path !== null)

mkdirSync(OUT_DIR, { recursive: true })
const sources = readdirSync(SOURCE_DIR)
  .filter((file) => file.endsWith('.cs'))
  .sort()
  .map((file) => join(SOURCE_DIR, file))

const args = [
  '/nologo',
  '/unsafe',
  '/target:winexe',
  '/optimize+',
  `/out:${OUT}`,
  '/r:System.Drawing.dll',
]
if (hasUia) {
  args.push('/define:HAS_UIA')
  for (const path of uiaAssemblies) args.push(`/r:${path}`)
} else {
  console.log('[pet] UI Automation assemblies not found — the tray-icon route compiles out')
}
args.push(...sources)
console.log(`[pet] ${csc} ${args.join(' ')}`)
const result = spawnSync(csc, args, { stdio: 'inherit', windowsHide: true })
if (result.error) {
  console.error(`[pet] ${result.error.message}`)
  process.exit(1)
}
if (result.status !== 0) {
  console.error(`[pet] compiler exited with ${result.status}`)
  process.exit(result.status ?? 1)
}
console.log(`[pet] built ${OUT}`)

// Animated WebP decoding needs the bundled libwebp next to the executable.
const nativeDir = join(ROOT, 'vendor', 'native', 'libwebp')
for (const file of readdirSync(nativeDir)) {
  if (!file.toLowerCase().endsWith('.dll')) continue
  copyFileSync(join(nativeDir, file), join(OUT_DIR, file))
}
console.log(`[pet] native libs from ${nativeDir} copied to ${OUT_DIR}`)

