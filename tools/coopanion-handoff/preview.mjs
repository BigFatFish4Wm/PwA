/**
 * Renders the filtered handoff note from a session log, so the filter can be looked at without
 * waiting for a handoff.
 *
 *   node tools/coopanion-handoff/preview.mjs
 *   node tools/coopanion-handoff/preview.mjs --file "<...>\session-main.jsonl" --out note.md
 *
 * Reads the records as Core wrote them (`data/session-main.jsonl`, one JSON object per line), not
 * the operator's handoff notes, and prints the note plus a size report.
 */
import { existsSync, readFileSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { dirname } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

const argv = process.argv.slice(2)
const arg = (name, fallback) => {
  const at = argv.indexOf(`--${name}`)
  return at >= 0 && argv[at + 1] ? argv[at + 1] : fallback
}

const app = arg('app', 'D:\\Program Files\\coopanion\\resources\\app')
const defaultLog = join('D:\\Program Files\\coopanion', 'data', 'home', 'companion', 'data', 'session-main.jsonl')
const file = arg('file', defaultLog)
const out = arg('out', '')

const here = dirname(fileURLToPath(import.meta.url))
const installed = join(app, 'core', 'handoff-filter.ts')
const modulePath = existsSync(installed) ? installed : join(here, 'handoff-filter.ts')

const { renderFilteredNote, DEFAULT_HANDOFF_FILTER } = await import(pathToFileURL(modulePath).href)

const lines = readFileSync(file, 'utf8').split(/\r?\n/).filter((line) => line.trim())
const snapshot = lines.map((line) => JSON.parse(line))
const note = renderFilteredNote(snapshot, DEFAULT_HANDOFF_FILTER, new Date(), {
  // the desktop pet World's speaking tools; the tool list of the live run has these tags
  speakTools: new Set(['pet_say', 'pet_ask']),
})

const records = snapshot.reduce((sum, record) => sum + JSON.stringify(record).length, 0)
console.log(`records       ${snapshot.length}`)
console.log(`snapshot      ${records} chars`)
console.log(`note          ${note.text.length} chars`)
console.log(`entries       ${note.entries}`)
console.log(`dropped       ${note.dropped}`)
console.log(`ratio         ${(note.text.length / records * 100).toFixed(1)}% of the snapshot`)
console.log('')
if (out) {
  writeFileSync(out, note.text, 'utf8')
  console.log(`written to ${out}`)
} else {
  console.log(note.text)
}
