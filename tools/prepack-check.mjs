#!/usr/bin/env node
/** Prepack gate: fail before npm publishes a package whose runtime was never built. */
import { existsSync, readdirSync, statSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const failures = []

const exe = join(ROOT, 'runtime', 'pet', 'DshLitePet.exe')
if (!existsSync(exe)) failures.push('runtime/pet/DshLitePet.exe missing (run `node tools/build-pet.mjs`)')

const mediaDir = join(ROOT, 'runtime', 'media')
const index = join(mediaDir, 'index.json')
if (!existsSync(index)) {
  failures.push('runtime/media/index.json missing (run `node tools/build-media.mjs`)')
} else {
  const clips = readdirSync(mediaDir).filter((file) => file.endsWith('.webp'))
  if (clips.length === 0) failures.push('runtime/media has no .webp clips')
  const bytes = readdirSync(mediaDir).reduce((sum, file) => sum + statSync(join(mediaDir, file)).size, 0)
  console.log(`[prepack] runtime/media: ${clips.length} files, ${(bytes / 1048576).toFixed(1)} MiB`)
}

if (failures.length > 0) {
  for (const failure of failures) console.error(`[prepack] ${failure}`)
  process.exit(1)
}
console.log('[prepack] runtime looks complete')

