#!/usr/bin/env node
/**
 * Packs the optional animation set into the archive the plugin downloads on first use.
 *
 * The npm package ships only the core clips (idle, work states, balance, click, sleep).
 * The 90+ ambient actions are published as a GitHub Release asset instead, so the
 * package stays small while the pet still gets its full animation library.
 *
 * Usage:
 *   node tools/pack-media.mjs [--out <file>]
 */
import { createWriteStream, existsSync, mkdirSync, readdirSync, readFileSync, statSync } from 'node:fs'
import { dirname, join, relative, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { deflateRawSync } from 'node:zlib'

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const args = process.argv.slice(2)
const arg = (name, fallback) => {
  const index = args.indexOf(name)
  return index >= 0 && args[index + 1] ? args[index + 1] : fallback
}

const SOURCE = resolve(arg('--from', join(ROOT, 'vendor/webp')))
const FULL = resolve(arg('--full', join(ROOT, 'runtime/media-full')))
const OUT = resolve(arg('--out', join(ROOT, 'runtime', 'dsh-lite-pet-media-full.zip')))

if (!existsSync(FULL) || !existsSync(join(FULL, 'index.json'))) {
  console.error(
    `[pack] ${FULL} has no index.json — run \`node tools/build-media.mjs --tier full --out ${FULL}\` first`,
  )
  process.exit(1)
}

// The archive mirrors runtime/media so the pet can be pointed at the extracted folder.
const files = readdirSync(FULL)
  .filter((name) => name.endsWith('.webp') || name === 'index.json')
  .sort()
if (files.length === 0) {
  console.error(`[pack] ${FULL} has nothing to pack`)
  process.exit(1)
}

function crc32(buffer) {
  let crc = 0xffffffff
  for (let i = 0; i < buffer.length; i++) {
    crc ^= buffer[i]
    for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ (0xedb88320 & -(crc & 1))
  }
  return (crc ^ 0xffffffff) >>> 0
}

const entries = []
let offset = 0
const chunks = []

for (const name of files) {
  const path = join(FULL, name)
  const data = readFileSync(path)
  const compressed = deflateRawSync(data, { level: 6 })
  // WebP is already compressed; if deflate does not help, store the bytes as-is.
  const useDeflate = compressed.length < data.length
  const payload = useDeflate ? compressed : data
  const method = useDeflate ? 8 : 0
  const crc = crc32(data)

  const localHeader = Buffer.alloc(30)
  localHeader.writeUInt32LE(0x04034b50, 0)
  localHeader.writeUInt16LE(20, 4) // version needed
  localHeader.writeUInt16LE(0x0800, 6) // UTF-8 names
  localHeader.writeUInt16LE(method, 8)
  localHeader.writeUInt16LE(0, 10) // time
  localHeader.writeUInt16LE(0, 12) // date
  localHeader.writeUInt32LE(crc, 14)
  localHeader.writeUInt32LE(payload.length, 18)
  localHeader.writeUInt32LE(data.length, 22)
  localHeader.writeUInt16LE(Buffer.byteLength(name), 26)
  localHeader.writeUInt16LE(0, 28)
  const nameBuffer = Buffer.from(name, 'utf8')

  entries.push({ name: nameBuffer, crc, method, compressedSize: payload.length, size: data.length, offset })
  chunks.push(localHeader, nameBuffer, payload)
  offset += localHeader.length + nameBuffer.length + payload.length
}

const centralStart = offset
const centralChunks = []
for (const entry of entries) {
  const header = Buffer.alloc(46)
  header.writeUInt32LE(0x02014b50, 0)
  header.writeUInt16LE(20, 4) // version made by
  header.writeUInt16LE(20, 6) // version needed
  header.writeUInt16LE(0x0800, 8)
  header.writeUInt16LE(entry.method, 10)
  header.writeUInt16LE(0, 12)
  header.writeUInt16LE(0, 14)
  header.writeUInt32LE(entry.crc, 16)
  header.writeUInt32LE(entry.compressedSize, 20)
  header.writeUInt32LE(entry.size, 24)
  header.writeUInt16LE(entry.name.length, 28)
  header.writeUInt16LE(0, 30) // extra
  header.writeUInt16LE(0, 32) // comment
  header.writeUInt16LE(0, 34) // disk
  header.writeUInt16LE(0, 36) // internal attrs
  header.writeUInt32LE(0, 38) // external attrs
  header.writeUInt32LE(entry.offset, 42)
  centralChunks.push(header, entry.name)
  offset += header.length + entry.name.length
}

const end = Buffer.alloc(22)
end.writeUInt32LE(0x06054b50, 0)
end.writeUInt16LE(0, 4)
end.writeUInt16LE(0, 6)
end.writeUInt16LE(entries.length, 8)
end.writeUInt16LE(entries.length, 10)
end.writeUInt32LE(offset - centralStart, 12)
end.writeUInt32LE(centralStart, 16)
end.writeUInt16LE(0, 20)

mkdirSync(dirname(OUT), { recursive: true })
await new Promise((resolvePromise, rejectPromise) => {
  const stream = createWriteStream(OUT)
  stream.on('error', rejectPromise)
  stream.on('finish', resolvePromise)
  for (const chunk of chunks) stream.write(chunk)
  for (const chunk of centralChunks) stream.write(chunk)
  stream.end(end)
})

const bytes = statSync(OUT).size
console.log(`[pack] ${files.length} files -> ${relative(ROOT, OUT)} (${(bytes / 1048576).toFixed(1)} MiB)`)
