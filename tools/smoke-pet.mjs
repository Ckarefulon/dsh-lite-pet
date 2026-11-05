#!/usr/bin/env node
/**
 * Smoke test for the native pet: starts it, expects the ready line, drives a
 * couple of states, then asks it to quit and checks the exit code.
 *
 * The pet opens a real (transparent) window, so this needs an interactive
 * Windows desktop session; it is a local tool, not a headless CI check.
 */
import { spawn } from 'node:child_process'
import { existsSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const EXE = join(ROOT, 'runtime', 'pet', 'DshLitePet.exe')
const MEDIA = join(ROOT, 'runtime', 'media')

if (!existsSync(EXE) || !existsSync(MEDIA)) {
  console.error('[smoke] build the pet and the media first (`npm run build`)')
  process.exit(1)
}

const child = spawn(EXE, ['--media', MEDIA], { stdio: ['pipe', 'pipe', 'inherit'], windowsHide: true })
child.stdout.setEncoding('utf8')

const timeout = (ms, label) =>
  new Promise((_, reject) => {
    const id = setTimeout(() => reject(new Error(`${label} timed out after ${ms}ms`)), ms)
    id.unref?.()
  })

let output = ''
const ready = new Promise((resolveReady, rejectReady) => {
  child.stdout.on('data', (chunk) => {
    output += chunk
    for (const line of String(chunk).split(/\r?\n/)) {
      if (line.trim() === '') continue
      console.log('[smoke] pet:', line.trim())
      const message = JSON.parse(line)
      if (message.t === 'ready') resolveReady()
    }
  })
  child.on('error', rejectReady)
})

const send = (payload) => child.stdin.write(JSON.stringify(payload) + '\n')

try {
  await Promise.race([ready, timeout(15000, 'ready')])
  send({ t: 'state', mode: 'working', text: '正在读取项目文件，先看一下这个模块的依赖关系。' })
  await new Promise((resolve) => setTimeout(resolve, 600))
  send({ t: 'state', mode: 'waiting', text: '这一步需要你确认一下：是否允许改动 package.json？' })
  await new Promise((resolve) => setTimeout(resolve, 600))
  send({ t: 'state', mode: 'done', text: '“修复登录页样式”已完成' })
  await new Promise((resolve) => setTimeout(resolve, 600))
  send({ t: 'quit' })
  const code = await Promise.race([new Promise((resolve) => child.on('exit', resolve)), timeout(5000, 'exit')])
  if (code !== 0) throw new Error(`pet exited with ${code}`)
  console.log('[smoke] ok')
} catch (error) {
  console.error(`[smoke] ${error instanceof Error ? error.message : String(error)}`)
  try {
    child.kill()
  } catch {
    /* already gone */
  }
  process.exit(1)
}
