#!/usr/bin/env node
/** Prepack gate: fail before npm publishes a package whose runtime was never built. */
import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const failures = []

// The npm package must stay small: it carries the core clips only. Anything close to
// the full set means `--tier full` was used by mistake and the package would be huge.
const CORE_BUDGET_MIB = 120

const exe = join(ROOT, 'runtime', 'pet', 'DshLitePet.exe')
if (!existsSync(exe)) failures.push('runtime/pet/DshLitePet.exe missing (run `node tools/build-pet.mjs`)')

// The native side loads libwebp at runtime; the DLLs have to travel with the exe.
for (const dll of ['libwebp.dll', 'libwebpdemux.dll', 'libsharpyuv.dll']) {
  const path = join(ROOT, 'runtime', 'pet', dll)
  if (!existsSync(path)) failures.push(`runtime/pet/${dll} missing (run \`node tools/build-pet.mjs\`)`)
}

const mediaDir = join(ROOT, 'runtime', 'media')
const index = join(mediaDir, 'index.json')
if (!existsSync(index)) {
  failures.push('runtime/media/index.json missing (run `node tools/build-media.mjs`)')
} else {
  const clips = readdirSync(mediaDir).filter((file) => file.endsWith('.webp'))
  if (clips.length === 0) failures.push('runtime/media has no .webp clips')
  const bytes = readdirSync(mediaDir).reduce((sum, file) => sum + statSync(join(mediaDir, file)).size, 0)
  const mib = bytes / 1048576
  console.log(`[prepack] runtime/media: ${clips.length} files, ${mib.toFixed(1)} MiB`)
  if (mib > CORE_BUDGET_MIB) {
    failures.push(
      `runtime/media is ${mib.toFixed(1)} MiB, over the ${CORE_BUDGET_MIB} MiB package budget — ` +
        'the full set belongs in the release asset, not the npm package',
    )
  }
  // Every clip listed in the catalog has to exist, or the pet silently loses animations.
  try {
    const catalog = JSON.parse(readFileSync(index, 'utf8'))
    const missing = (catalog.animations ?? []).filter((entry) => !existsSync(join(mediaDir, entry.file)))
    if (missing.length > 0) failures.push(`catalog lists ${missing.length} clip(s) that are not on disk`)
    // The pet cannot animate without an idle loop.
    if (!(catalog.animations ?? []).some((entry) => entry.kind === 'idle')) failures.push('catalog has no idle clip')
  } catch (error) {
    failures.push(`runtime/media/index.json is not valid JSON: ${String(error?.message ?? error)}`)
  }
}

if (failures.length > 0) {
  for (const failure of failures) console.error(`[prepack] ${failure}`)
  process.exit(1)
}
console.log('[prepack] runtime looks complete')
