#!/usr/bin/env node
/**
 * Builds the animation catalog the pet reads.
 *
 * Source of truth is `vendor/webm/`: the upstream VP9 masters with an alpha side
 * stream (~55 MiB, the only animation data the repository carries). This step
 * transcodes each clip to an animated WebP and writes index.json. Already-built
 * WebP files are reused, so a rebuild only pays for what changed.
 *
 * The masters are VP9 with `ALPHA_MODE=1`. ffmpeg only exposes that alpha plane
 * when the libvpx decoder is requested explicitly (`-c:v libvpx-vp9`); without it
 * the alpha reads back as fully opaque and the pet loses its silhouette.
 *
 * Usage:
 *   node tools/build-media.mjs [--from <webm dir>] [--out <media dir>]
 *                              [--tier core|full] [--ffmpeg <path>] [--skip-existing]
 */
import { copyFileSync, existsSync, mkdirSync, readdirSync, statSync, writeFileSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const args = process.argv.slice(2)
const arg = (name, fallback) => {
  const index = args.indexOf(name)
  return index >= 0 && args[index + 1] ? args[index + 1] : fallback
}

const SOURCE = resolve(arg('--from', join(ROOT, 'vendor/webm')))
const OUT = resolve(arg('--out', join(ROOT, 'runtime/media')))

// Transcode settings. 15 fps is what the pet's playback expects (151 frames over
// ~10 s), 640x360 is the authored canvas. Do NOT add libwebp's -cr_threshold:
// conditional replenishment corrupts alpha-carrying animations — it punches white
// holes through the artwork and turns the silhouette edge into black blocks.
const FPS = arg('--fps', '15')
const QUALITY = arg('--quality', '75')

function findFfmpeg() {
  const explicit = arg('--ffmpeg', process.env.FFMPEG)
  if (explicit) return explicit
  const probe = spawnSync('ffmpeg', ['-version'], { encoding: 'utf8' })
  if (probe.status === 0) return 'ffmpeg'
  return null
}

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

const GROUP_LISTS = {
  小动作: [
    '悠闲哼歌', '超大伸懒腰', '原地敲击桌面互动', '原地重力下蹲压缩', '哈欠连天', '原地小憩沉眠',
    '女仆屈膝礼仪', '被吓一跳', '小幅度原地360度旋转展示', '偷吃零食被抓住', '用鲸鱼尾巴拍打地面',
    '打瞌睡被惊醒', '照镜子', '整体换装试色', '轻快记录', '写代码', '摇扇纳凉', '晨间刷牙',
    // Clips upstream added after the first catalog: they are ordinary ambient actions.
    '东张西望', '侧身急躁跑', '原地左转奔跑', '原地漂浮踏步', '开心小跑', '螃蟹走路',
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

const CLASSIFY = new Map()
for (const [name, kind] of Object.entries(WORK)) CLASSIFY.set(name, { group: '工作', kind })
CLASSIFY.set('待机呼吸休闲', { group: '待机', kind: 'idle' })
for (const [group, names] of Object.entries(GROUP_LISTS)) {
  for (const name of names) {
    const kind = group === '睡觉' ? 'sleep' : group === '文字' ? 'whisper' : 'action'
    CLASSIFY.set(name, { group, kind })
  }
}
// Extra clips the pet plays when asked. Upstream names them by prefix, so match the
// prefixes rather than listing every file (the set grows upstream).
const KINDS_BY_NAME = (name) => {
  if (name.startsWith('余额-')) return { group: '余额', kind: 'balance' }
  if (name.startsWith('点击回应-')) return { group: '点击', kind: 'click' }
  if (name.startsWith('碎碎念-')) return { group: '碎碎念', kind: 'whisper' }
  if (name.startsWith('睡觉-')) return { group: '睡觉', kind: 'sleep' }
  return null
}

const names = readdirSync(SOURCE)
  .filter((file) => file.toLowerCase().endsWith('.webm') || file.toLowerCase().endsWith('.webp'))
  .map((file) => file.slice(0, -5))
  .filter((name) => !SKIP.has(name))
  .sort((a, b) => a.localeCompare(b, 'zh-Hans-CN'))

if (names.length === 0) {
  console.error(`[media] no .webm/.webp clips in ${SOURCE} — the upstream masters belong there`)
  process.exit(1)
}

const ffmpeg = findFfmpeg()
const needsTranscode = names.some((name) => !existsSync(join(SOURCE, `${name}.webp`)))
if (needsTranscode && ffmpeg === null) {
  console.error('[media] ffmpeg is required to transcode vendor/webm; install it or pass --ffmpeg <path>')
  process.exit(1)
}

/**
 * Transcodes one WebM master into an animated WebP. Existing output is reused so a
 * rebuild only pays for clips that changed or are new.
 */
function transcode(name, targetPath) {
  const webm = join(SOURCE, `${name}.webm`)
  const webp = join(SOURCE, `${name}.webp`)
  // A pre-made WebP next to the masters wins: no transcode needed.
  if (existsSync(webp)) {
    copyFileSync(webp, targetPath)
    return
  }
  if (existsSync(targetPath)) return
  const result = spawnSync(
    ffmpeg,
    [
      '-v', 'error',
      '-c:v', 'libvpx-vp9', // exposes the VP9 alpha side stream
      '-i', webm,
      '-vf', `fps=${FPS},scale=640:360`,
      '-c:v', 'libwebp_anim',
      '-lossless', '0',
      '-q:v', QUALITY,
      '-loop', '0',
      '-an',
      '-y',
      targetPath,
    ],
    { encoding: 'utf8' },
  )
  if (result.status !== 0 || !existsSync(targetPath)) {
    throw new Error(`ffmpeg failed for ${name}: ${(result.stderr ?? '').trim().split('\n').pop() ?? 'unknown error'}`)
  }
}

// Two tiers, because the full set is ~280 MiB and npm packages should stay small:
//   core — the clips the pet needs to be usable at all (idle, work states, balance,
//          click, sleep). Shipped inside the npm package.
//   full — every clip, including the 87 ambient actions. Published as a GitHub
//          Release asset and cached on the user's machine the first time the switch
//          is turned on.
// `--tier full` writes the complete catalog; the default is core.
const TIER = arg('--tier', 'core')
const CORE_KINDS = new Set(['idle', 'work-busy', 'work-thinking', 'work-waiting', 'work-done', 'work-error', 'balance', 'click', 'sleep'])

mkdirSync(OUT, { recursive: true })
const animations = []
let index = 0
let bytes = 0
let skipped = 0
for (const name of names) {
  const meta = CLASSIFY.get(name) ?? KINDS_BY_NAME(name)
  if (!meta) continue
  if (TIER === 'core' && !CORE_KINDS.has(meta.kind)) {
    skipped += 1
    continue
  }
  index += 1
  const id = String(index).padStart(3, '0')
  const fileName = `${id}.webp`
  const targetPath = join(OUT, fileName)
  transcode(name, targetPath)
  bytes += statSync(targetPath).size
  animations.push({ id, name, group: meta.group, kind: meta.kind, file: fileName })
}

writeFileSync(
  join(OUT, 'index.json'),
  JSON.stringify({ version: 3, tier: TIER, width: 640, bodyHeight: 360, animations }, null, 2) + '\n',
)
console.log(
  `[media] tier=${TIER}: ${animations.length} clips -> ${OUT} (${(bytes / 1048576).toFixed(1)} MiB)` +
    (skipped > 0 ? `; ${skipped} ambient clips left for the release asset` : ''),
)
