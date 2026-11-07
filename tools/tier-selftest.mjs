#!/usr/bin/env node
/**
 * Checks the two-tier media resolution without touching the real ~/.dsh cache:
 * with nothing cached the plugin must use the package's core clips; once a catalog
 * appears in the cache it must prefer that instead.
 */
import { existsSync, mkdirSync, rmSync, writeFileSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = resolve(fileURLToPath(new URL('..', import.meta.url)))
const scratchHome = join(ROOT, 'runtime', 'tier-selftest')
rmSync(scratchHome, { recursive: true, force: true })
mkdirSync(scratchHome, { recursive: true })

// Point the plugin at a scratch DSH_HOME so it never sees the user's real cache.
process.env.DSH_HOME = scratchHome
const plugin = await import('file:///' + join(ROOT, 'index.js').replace(/\\/g, '/'))
const { resolveMediaDirForTest } = plugin

const failures = []

// 1) No cache: the packaged core catalog must be chosen.
const core = resolveMediaDirForTest({})
const expectedCore = join(ROOT, 'runtime', 'media')
if (core !== expectedCore) failures.push(`expected the packaged core dir, got ${core}`)
if (!existsSync(join(core, 'index.json'))) failures.push('the packaged core dir has no index.json')

// 2) With a cached catalog: the cache must win.
const cache = join(scratchHome, 'dsh-lite-pet-media')
mkdirSync(cache, { recursive: true })
writeFileSync(join(cache, 'index.json'), JSON.stringify({ version: 3, tier: 'full', animations: [] }))
const withCache = resolveMediaDirForTest({})
if (withCache !== cache) failures.push(`expected the cached full dir, got ${withCache}`)

// 3) A cache without a catalog must not be trusted.
writeFileSync(join(cache, 'index.json'), '{}')
rmSync(join(cache, 'index.json'))
const brokenCache = resolveMediaDirForTest({})
if (brokenCache !== expectedCore) failures.push(`a cache without index.json must fall back, got ${brokenCache}`)

rmSync(scratchHome, { recursive: true, force: true })

if (failures.length > 0) {
  for (const failure of failures) console.error(`[tier] ${failure}`)
  process.exit(1)
}
console.log('[tier] ok — core by default, cache preferred when complete, core fallback when broken')
