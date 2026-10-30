/**
 * 把 cortico-world-pwa 装进 Coopanion 的 extensions，并在 companion 配置里启用它。
 * 只改文件，不重启进程：装完要重启 Coopanion 才会加载（ESM 模块不热更）。
 *
 * 用法：node tools/enable-world.mjs [--disable]
 */
import { readFile, writeFile } from 'node:fs/promises'
import { join } from 'node:path'

const WORKSPACE = 'D:\\workspace\\deepseek\\new10-30'
const COOPANION = 'D:\\Program Files\\coopanion'
const EXTENSIONS = join(COOPANION, 'data', 'extensions')
const CONFIG = join(COOPANION, 'data', 'home', 'companion', 'config.json')
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
pkg.dependencies['cortico-world-pwa'] = `link:${join(WORKSPACE, 'cortico-world-pwa')}`
await writeJson(pkgPath, pkg)
console.log('[enable-world] extensions/package.json 已登记 cortico-world-pwa')

const cfg = await readJson(CONFIG)
cfg.worlds ??= {}
cfg.worlds.pwa = {
  ...(cfg.worlds.pwa ?? {}),
  enabled: !disable,
  baseUrl: '',
  pollSeconds: 4,
  watch: true,
  delta: true,
  timeoutMs: 20000,
  maxChars: 6000,
  auto: true,
  autoSteps: 6,
  waitTurnSeconds: 8,
}
if (cfg.worlds.sts2) {
  cfg.worlds.sts2.enabled = false
  console.log('[enable-world] 旧的 sts2 world 已停用（工具名会撞车）')
}
await writeJson(CONFIG, cfg)
console.log(`[enable-world] config.json: worlds.pwa.enabled=${!disable}`)
console.log('[enable-world] 还要建立 node_modules 链接并重启 Coopanion：')
console.log('   powershell -ExecutionPolicy Bypass -File tools\\link-world.ps1')
