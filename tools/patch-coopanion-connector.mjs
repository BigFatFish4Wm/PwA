/**
 * 让 Coopanion 桌宠只收到「回复 / 审批 / 提问 / 错误」，不再被工具调用和回合进度刷屏。
 *
 * 背景：桌面端的 coopanion-connector-dsh（Host 半区）会把每个 `tool/call` 推成
 * `{t:"tool"}` 帧，`turn/start`、`turn/end` 也会推成提示；桌宠逐条显示，真正要看
 * 的回复正文被淹掉。这个脚本给已安装的 dist/index.cjs 打补丁：
 *   - 静音 tool 帧；
 *   - 静音 turn-start / turn-end（noticeOf 直接返回 null）。
 * 设环境变量 COOPANION_DSH_VERBOSE=1 可恢复全部通知。
 *
 * 用法：node tools/patch-coopanion-connector.mjs [--revert]
 * 注意：插件升级/重装会覆盖 dist，升级后需要重新跑一次；改完要重启 DSH/Coopanion 桌面端。
 */
import { readFileSync, writeFileSync, existsSync, copyFileSync } from 'node:fs'
import { join } from 'node:path'
import { homedir } from 'node:os'

const args = process.argv.slice(2)
const revert = args.includes('--revert')
const dist = join(homedir(), '.dsh', 'profiles', 'desktop', 'node_modules', 'coopanion-connector-dsh', 'dist', 'index.cjs')
const backup = `${dist}.bak-quiet`

if (!existsSync(dist)) {
  console.log(`找不到插件产物：${dist}`)
  process.exit(1)
}

if (revert) {
  if (!existsSync(backup)) {
    console.log('没有备份可还原')
    process.exit(1)
  }
  copyFileSync(backup, dist)
  console.log('已还原原始产物（重启桌面端后恢复全部通知）')
  process.exit(0)
}

// 备份只在第一次打补丁时保存原始文件；之后都以备份为基准重新生成，保证可重复执行。
if (!existsSync(backup)) {
  copyFileSync(dist, backup)
}
let source = readFileSync(backup, 'utf8')

const replacements = [
  [
    'notify({ t: "tool", name, detail: argumentNames(data.arguments) });',
    'if (process.env.COOPANION_DSH_VERBOSE === "1") notify({ t: "tool", name, detail: argumentNames(data.arguments) });',
  ],
  [
    'function noticeOf(sessionId, event) {\n  const data = event.data ?? {};',
    'function noticeOf(sessionId, event) {\n  if (process.env.COOPANION_DSH_VERBOSE !== "1") return null;\n  const data = event.data ?? {};',
  ],
]

const missing = []
for (const [needle] of replacements) {
  if (!source.includes(needle)) missing.push(needle.slice(0, 60))
}

if (missing.length > 0) {
  console.log('找不到补丁位置（插件版本可能变了）：')
  for (const item of missing) console.log(`  - ${item}`)
  process.exit(1)
}

for (const [needle, replacement] of replacements) {
  source = source.replace(needle, replacement)
}

writeFileSync(dist, source, 'utf8')
console.log('已静音：工具调用、回合开始/结束。桌宠只会收到 回复 / 审批 / 提问 / 错误。')
console.log('恢复：node tools/patch-coopanion-connector.mjs --revert（或设 COOPANION_DSH_VERBOSE=1）')
console.log('改完需要完全退出并重开 Coopanion/DSH 桌面端才生效。')
