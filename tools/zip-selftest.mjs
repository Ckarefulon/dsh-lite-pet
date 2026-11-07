#!/usr/bin/env node
/**
 * Exercises the ambient-set archive end to end: packs a tiny catalog with the real
 * packer, then extracts it with the plugin's own reader and checks the result.
 * No network involved, so it can run in CI.
 */
import { spawnSync } from 'node:child_process'
import { existsSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = resolve(fileURLToPath(new URL('..', import.meta.url)))
const TMP = join(ROOT, 'runtime', 'zip-selftest')
rmSync(TMP, { recursive: true, force: true })
mkdirSync(join(TMP, 'src'), { recursive: true })

const clip = Buffer.from('RIFF0000WEBPVP8 fake-clip-bytes-for-the-selftest-check')
writeFileSync(join(TMP, 'src', '001.webp'), clip)
writeFileSync(
  join(TMP, 'src', 'index.json'),
  JSON.stringify(
    {
      version: 3,
      tier: 'full',
      width: 640,
      bodyHeight: 360,
      animations: [{ id: '001', name: 'selftest', group: 'test', kind: 'action', file: '001.webp' }],
    },
    null,
    2,
  ),
)

const pack = spawnSync(
  process.execPath,
  [join(ROOT, 'tools', 'pack-media.mjs'), '--full', join(TMP, 'src'), '--out', join(TMP, 'asset.zip')],
  { encoding: 'utf8' },
)
if (pack.status !== 0) {
  console.error(pack.stdout, pack.stderr)
  process.exit(1)
}

const { extractZipTo } = await import('file:///' + join(ROOT, 'index.js').replace(/\\/g, '/'))
const outDir = join(TMP, 'out')
await extractZipTo(readFileSync(join(TMP, 'asset.zip')), outDir)

const failures = []
if (!existsSync(join(outDir, 'index.json'))) failures.push('index.json was not extracted')
if (!existsSync(join(outDir, '001.webp'))) failures.push('the clip was not extracted')
if (failures.length === 0) {
  const index = JSON.parse(readFileSync(join(outDir, 'index.json'), 'utf8'))
  if (index.animations.length !== 1) failures.push('wrong clip count in the extracted catalog')
  if (readFileSync(join(outDir, '001.webp')).length !== clip.length) failures.push('clip bytes differ after extraction')
}

if (failures.length > 0) {
  for (const failure of failures) console.error(`[zip] ${failure}`)
  process.exit(1)
}
console.log(`[zip] ok — extracted ${readdirSync(outDir).join(', ')}`)
rmSync(TMP, { recursive: true, force: true })
