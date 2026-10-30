/**
 * 把 cortico-world-memory 登记进 Coopanion 的 extensions，并在 companion 配置里启用它。
 * 只改文件，不重启进程：装完要重启 Coopanion 才会加载（ESM 模块不热更）。
 *
 * 用法：
 *   node tools/enable-memory-world.mjs           启用
 *   node tools/enable-memory-world.mjs --disable 停用
 *
 * 与 enable-world.mjs（PwA）同一套路；两个 World 可以同时开：
 * 工具名不冲突（sts2_* / memory_*），前缀各占一段。
 */
import { readFile, writeFile } from 'node:fs/promises'
import { join } from 'node:path'

const WORKSPACE = 'D:\\workspace\\deepseek\\new10-30'
const COOPANION = 'D:\\Program Files\\coopanion'
const EXTENSIONS = join(COOPANION, 'data', 'extensions')
const CONFIG = join(COOPANION, 'data', 'home', 'companion', 'config.json')
const PACKAGE = 'cortico-world-memory'
const disable = process.argv.includes('--disable')

async function readJson(path) {
  const text = await readFile(path, 'utf8')
  return JSON.parse(text.replace(/^\uFEFF/, ''))
}

async function writeJson(path, value) {
  await writeFile(path, `${JSON.stringify(value, null, 2)}\n`, 'utf8')
}

const pkgPath = join(EXTENSIONS, 'package.json')
const pkg = await readJson(pkgPath)
pkg.dependencies ??= {}
pkg.dependencies[PACKAGE] = `link:${join(WORKSPACE, PACKAGE)}`
await writeJson(pkgPath, pkg)
console.log(`[enable-memory-world] extensions/package.json 已登记 ${PACKAGE}`)

const cfg = await readJson(CONFIG)
cfg.worlds ??= {}
cfg.worlds.memory = {
  ...(cfg.worlds.memory ?? {}),
  enabled: !disable,
  workspace: '',
  inject: true,
  topK: 5,
  budgetTokens: 2000,
  minCoverage: 0.12,
  halfLifeDays: 90,
  refBoost: 0.3,
  vector: 'auto',
  embedModel: '',
  scanSeconds: 30,
  maxAgeDays: 0,
  queryHint: '',
  maxChars: 4000,
}
await writeJson(CONFIG, cfg)
console.log(`[enable-memory-world] config.json: worlds.memory.enabled=${!disable}`)
console.log('[enable-memory-world] 还要建立 node_modules 链接并重启 Coopanion：')
console.log('   powershell -ExecutionPolicy Bypass -File tools\\link-world.ps1 -Name cortico-world-memory')
