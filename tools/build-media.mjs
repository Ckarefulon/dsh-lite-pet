#!/usr/bin/env node
/**
 * Builds runtime/media from a directory of animated WebP clips.
 *
 * The pet decodes animated WebP directly with the bundled libwebp (no re-encoding,
 * no Media Foundation, no ffmpeg). This step only classifies the files and writes
 * the catalog the pet reads.
 *
 * Usage:
 *   node tools/build-media.mjs [--from <webp dir>] [--out <media dir>] [--force]
 */
import { copyFileSync, existsSync, mkdirSync, readdirSync, statSync, writeFileSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const args = process.argv.slice(2)
const arg = (name, fallback) => {
  const index = args.indexOf(name)
  return index >= 0 && args[index + 1] ? args[index + 1] : fallback
}

const SOURCE = resolve(arg('--from', join(ROOT, 'vendor/webp')))
const OUT = resolve(arg('--out', join(ROOT, 'runtime/media')))

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
// Extra clips the pet plays when asked (balance check and click feedback).
const KINDS_BY_NAME = (name) => {
  if (name.startsWith('查余额')) return { group: '查余额', kind: 'balance' }
  if (name.startsWith('点击回应') || name.startsWith('点击')) return { group: '点击', kind: 'click' }
  return null
}

const names = readdirSync(SOURCE)
  .filter((file) => file.toLowerCase().endsWith('.webp'))
  .map((file) => file.slice(0, -5))
  .filter((name) => !SKIP.has(name))
  .sort((a, b) => a.localeCompare(b, 'zh-Hans-CN'))

if (names.length === 0) {
  console.error(`[media] no .webp files in ${SOURCE} — drop the transcoded clips there first`)
  process.exit(1)
}

mkdirSync(OUT, { recursive: true })
const animations = []
let index = 0
let bytes = 0
for (const name of names) {
  const meta = CLASSIFY.get(name) ?? KINDS_BY_NAME(name)
  if (!meta) continue
  index += 1
  const id = String(index).padStart(3, '0')
  const fileName = `${id}.webp`
  const sourcePath = join(SOURCE, `${name}.webp`)
  const targetPath = join(OUT, fileName)
  if (!existsSync(targetPath) || statSync(targetPath).size !== statSync(sourcePath).size) {
    copyFileSync(sourcePath, targetPath)
  }
  bytes += statSync(targetPath).size
  animations.push({ id, name, group: meta.group, kind: meta.kind, file: fileName })
}

writeFileSync(
  join(OUT, 'index.json'),
  JSON.stringify({ version: 3, width: 640, bodyHeight: 360, animations }, null, 2) + '\n',
)
console.log(`[media] ${animations.length} clips -> ${OUT} (${(bytes / 1048576).toFixed(1)} MiB)`)
