/**
 * Installs the filtered handoff-note renderer into an installed Coopanion.
 *
 *   node tools/coopanion-handoff/install.mjs
 *   node tools/coopanion-handoff/install.mjs --revert
 *   node tools/coopanion-handoff/install.mjs --app "D:\path\to\resources\app"
 *
 * Writes `core/handoff-filter.ts` and replaces `core/persona.ts` and `core/companion.ts` with the
 * patched copies in `patched/`. The first run keeps `.bak-handoff` copies of the two files it
 * replaces; `--revert` puts them back and removes the filter. Restart Coopanion afterwards: the Core
 * child process loads these files at start.
 *
 * The patched copies belong to one app version. A different version refuses to install unless
 * `--force` is given: the handoff code may have moved since.
 */
import { copyFileSync, existsSync, readFileSync, rmSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
const argv = process.argv.slice(2)
const arg = (name, fallback) => {
  const at = argv.indexOf(`--${name}`)
  return at >= 0 && argv[at + 1] ? argv[at + 1] : fallback
}
const app = arg('app', 'D:\\Program Files\\coopanion\\resources\\app')
const revert = argv.includes('--revert')
const force = argv.includes('--force')

const core = join(app, 'core')
const targets = ['persona.ts', 'companion.ts']
const filter = join(core, 'handoff-filter.ts')
const backup = (name) => join(core, `${name}.bak-handoff`)

if (!existsSync(join(app, 'package.json')) || !existsSync(core)) {
  console.error(`not a Coopanion app directory: ${app}`)
  process.exit(2)
}

if (revert) {
  let restored = 0
  for (const name of targets) {
    if (!existsSync(backup(name))) continue
    copyFileSync(backup(name), join(core, name))
    rmSync(backup(name), { force: true })
    restored++
  }
  rmSync(filter, { force: true })
  console.log(restored ? `reverted ${restored} file(s); restart Coopanion` : 'nothing to revert')
  process.exit(0)
}

const version = JSON.parse(readFileSync(join(app, 'package.json'), 'utf8')).version
const expected = JSON.parse(readFileSync(join(here, 'patched', 'version.json'), 'utf8')).app
if (version !== expected && !force) {
  console.error(`this patch is for Coopanion ${expected}, the app is ${version}.`)
  console.error('Check that the handoff code still matches, then rerun with --force.')
  process.exit(3)
}

for (const name of targets) {
  const target = join(core, name)
  const saved = backup(name)
  if (!existsSync(saved)) copyFileSync(target, saved)
  copyFileSync(join(here, 'patched', name), target)
}
copyFileSync(join(here, 'handoff-filter.ts'), filter)

console.log(`installed into ${app}`)
console.log('  core/handoff-filter.ts   (new)')
console.log('  core/persona.ts          (onHandoff renders the filtered note)')
console.log('  core/companion.ts        (handoff config group and defaults)')
console.log('restart Coopanion: the Core process reads these files at start')
console.log('undo: node tools/coopanion-handoff/install.mjs --revert')
