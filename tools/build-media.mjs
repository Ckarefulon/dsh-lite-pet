#!/usr/bin/env node
/**
 * Builds runtime/media from the upstream animation sources.
 *
 * Every WebM is transcoded into two H.264 tracks the pet process can decode with
 * Media Foundation: a color track and a grayscale alpha track. The pet window is
 * layered, so per-pixel alpha is required; MF cannot decode VP9 alpha, which is
 * why this step exists at all.
 *
 * Usage:
 *   node tools/build-media.mjs [--from <webm dir>] [--out <media dir>] [--only <substr>]
 *                              [--crf <color crf>] [--alpha-crf <alpha crf>] [--force]
 */
import { spawnSync } from 'node:child_process'
import { existsSync, mkdirSync, readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const args = process.argv.slice(2)
const arg = (name, fallback) => {
  const index = args.indexOf(name)
  return index >= 0 && args[index + 1] ? args[index + 1] : fallback
}
const flag = (name) => args.includes(name)

const SOURCE = resolve(arg('--from', join(ROOT, 'vendor/upstream/dsh-pet/assets/webm')))
const OUT = resolve(arg('--out', join(ROOT, 'runtime/media')))
const ONLY = arg('--only', '')
const CRF = arg('--crf', '23')
const FORCE = flag('--force')
const FFMPEG = process.env.FFMPEG || 'ffmpeg'
const FFPROBE = process.env.FFPROBE || 'ffprobe'

// —— upstream grouping (mirrors dsh-pet's assets/config.jsonc) ————————————————
const WORK = {
  '工作状态-思考冒泡': 'work-thinking',
  '工作状态-忙碌点按': 'work-busy',
  '工作状态-清点归档': 'work-busy',
  '工作状态-原地踱步张望': 'work-waiting',
  '工作状态-雀跃庆祝': 'work-done',
  '工作状态-垂头叹气冒汗': 'work-error',
}
const SKIP = new Set(['被鼠标拖拽悬空反馈'])
// The idle loop is the only clip that runs continuously, so halve its frame rate.
const FPS_OVERRIDE = { 待机呼吸休闲: 12 }

const GROUP_LISTS = {
  小动作: [
    '悠闲哼歌', '超大伸懒腰', '原地敲击桌面互动', '原地重力下蹲压缩', '哈欠连天', '原地小憩沉眠',
    '女仆屈膝礼仪', '被吓一跳', '小幅度原地360度旋转展示', '偷吃零食被抓住', '用鲸鱼尾巴拍打地面',
    '打瞌睡被惊醒', '照镜子', '整体换装试色', '轻快记录', '写代码', '摇扇纳凉', '晨间刷牙',
  ],
  玩耍: [
    '原地专心玩魔方', '原地蹲下玩玩具汽车', '鲸鱼吐泡泡特效', '原地跳跃抓碎头顶物品', '玩游戏气急败坏',
    '玩水枪', '小提琴演奏', '蓝鲸现世', '优雅女仆舞', '轻快摇摆舞', '可爱宅舞', '吹气球', '动物环绕',
    '放风筝', '拆礼物', '变鸽子', '扑克魔术', '抽陀螺', '吹笛子', '蝴蝶蜜蜂环绕头顶开花', '撸猫',
    '凭空生花', '骑木马', '三球抛接', '踢毽子', '下五子棋', '荡秋千',
  ],
  吃什么: [
    '吃白饭', '大口吃零食', '吃Token', '吃早餐', '吃午餐', '吃晚餐', '吃冰淇淋融化', '吃大闸蟹',
    '吃糖葫芦', '吃长寿面', '吃西瓜', '涮火锅', '吃饭·碗饭', '吃饭团', '期待开饭',
  ],
  时节: [
    '被落叶淹没', '中秋赏月吃月饼', '堆雪人', '放烟花', '吃粽子', '吃年糕', '吃青团', '吃腊八粥',
    '吃重阳糕', '收红包', '写福字', '穿针乞巧', '舞狮头', '讨糖南瓜灯', '插茱萸赏菊', '放河灯',
    '萌化小幽灵', '装点圣诞树', '放孔明灯', '吃汤圆', '吃饺子',
  ],
  文字: ['是啊，吃什么', '深度思考碎碎念'],
  睡觉: ['睡觉-开始', '睡觉-循环', '睡觉-起来'],
}

const groupOf = new Map()
for (const [group, names] of Object.entries(GROUP_LISTS)) for (const name of names) groupOf.set(name, group)

function classify(name) {
  if (SKIP.has(name)) return null
  if (name === '待机呼吸休闲') return { kind: 'idle', group: '待机' }
  if (WORK[name]) return { kind: WORK[name], group: '工作状态' }
  if (name.startsWith('余额-')) return { kind: 'balance', group: '余额' }
  if (name.startsWith('点击回应-')) return { kind: 'click', group: '点击回应' }
  if (name.startsWith('碎碎念-')) return { kind: 'whisper', group: '碎碎念' }
  const group = groupOf.get(name)
  if (group) return { kind: 'action', group }
  return { kind: 'action', group: '其他' }
}

const run = (bin, params) => {
  const result = spawnSync(bin, params, { stdio: 'ignore', windowsHide: true })
  if (result.error) throw new Error(`${bin} failed: ${result.error.message}`)
  if (result.status !== 0) throw new Error(`${bin} exited with ${result.status}: ${params.join(' ')}`)
}

const pace = () => new Promise((resolve) => setImmediate(resolve))

async function main() {
  if (!existsSync(SOURCE)) {
    console.error(`[media] source directory not found: ${SOURCE}`)
    console.error('[media] run `git subtree add --prefix=vendor/upstream <upstream> main --squash` first, or pass --from <dir>')
    process.exit(1)
  }
  mkdirSync(OUT, { recursive: true })

  const names = readdirSync(SOURCE)
    .filter((file) => file.endsWith('.webm'))
    .map((file) => file.slice(0, -'.webm'.length))
    .filter((name) => !ONLY || name.includes(ONLY))
    .sort((a, b) => a.localeCompare(b, 'zh-Hans-CN'))

  const animations = []
  let index = 0
  let bytes = 0
  for (const name of names) {
    const meta = classify(name)
    if (!meta) continue
    index += 1
    const id = String(index).padStart(3, '0')
    const fileName = `${id}.mp4`
    const filePath = join(OUT, fileName)
    const sourcePath = join(SOURCE, `${name}.webm`)
    const fps = FPS_OVERRIDE[name] ?? 24
    const sourceMtime = statSync(sourcePath).mtimeMs

    // One clip = one video: the color frame on top and its alpha mask stacked
    // underneath. The mask is thresholded first: a binary band costs almost nothing
    // to encode (large flat areas) and the compositor already treats alpha as a mask.
    const fresh = existsSync(filePath) && !FORCE && statSync(filePath).mtimeMs >= sourceMtime
    if (!fresh) {
      run(FFMPEG, ['-v', 'error', '-y', '-c:v', 'libvpx-vp9', '-i', sourcePath, '-an',
        '-filter_complex',
        `[0:v]fps=${fps},format=rgba,split=2[c][a];` +
          '[c]scale=640:360,format=yuv420p[col];' +
          "[a]alphaextract,scale=640:360,lut=y='if(gt(val,120),255,0)',format=yuv420p[alp];" +
          '[col][alp]vstack=inputs=2,format=yuv420p[v]',
        '-map', '[v]',
        '-c:v', 'libx264', '-preset', 'slow', '-crf', CRF, '-pix_fmt', 'yuv420p',
        '-movflags', '+faststart', filePath])
    }

    const duration = probeDurationMs(filePath, sourcePath)
    animations.push({ id, name, group: meta.group, kind: meta.kind, file: fileName, ms: duration, fps })
    bytes += statSync(filePath).size
    process.stdout.write(`[media] ${id} ${meta.group}/${meta.kind} ${name} (${duration}ms)\n`)
    await pace()
  }

  writeFileSync(join(OUT, 'index.json'), JSON.stringify({ version: 2, width: 640, bodyHeight: 360, animations }, null, 2) + '\n')
  console.log(`[media] ${animations.length} clips -> ${OUT} (${(bytes / 1048576).toFixed(1)} MiB)`)
}

function probeDurationMs(colorPath, fallbackPath) {
  try {
    const result = spawnSync(FFPROBE, ['-v', 'error', '-show_entries', 'format=duration', '-of', 'csv=p=0', colorPath], { encoding: 'utf8', windowsHide: true })
    const value = Number.parseFloat(String(result.stdout || '').trim())
    if (Number.isFinite(value) && value > 0) return Math.round(value * 1000)
  } catch {
    /* fall through */
  }
  const raw = readFileSync(fallbackPath).length
  return Math.min(10040, Math.max(1000, Math.round(raw / 50)))
}

main().catch((error) => {
  console.error(`[media] ${error.message}`)
  process.exit(1)
})


