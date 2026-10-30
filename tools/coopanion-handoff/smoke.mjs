/**
 * Loads the installed Persona class and runs one handoff over a made-up window.
 *
 *   node tools/coopanion-handoff/smoke.mjs
 *
 * Checks the wiring the way the app uses it: the note is written to `handoffs/`, the wake line and
 * the note go to Core as injections, the session tail is empty, and the filter's rules hold —
 * 30 partner lines, 10 bubbles, one line per call, repeats counted, noise and blob bodies gone,
 * day-old calls dropped.
 */
import { existsSync, mkdirSync, readFileSync, rmSync } from 'node:fs'
import { mkdtempSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'
import assert from 'node:assert/strict'

const app = process.argv[2] ?? 'D:\\Program Files\\coopanion\\resources\\app'
const modulePath = join(app, 'core', 'persona.ts')
if (!existsSync(modulePath)) {
  console.error(`persona not found: ${modulePath}`)
  process.exit(2)
}

const { coopanionPersona } = await import(pathToFileURL(modulePath).href)

// --- a stand-in for Cormini, holding only what the override touches -------------
class FakeBase {
  constructor() {
    this.memoryDir = mkdtempSync(join(tmpdir(), 'handoff-smoke-'))
    this.core = null
    this.lastHandoffFile = null
    this.pressureWarned = true
    this.pressureSplitAt = Date.now()
    this.handedOff = false
    this.injected = []
  }
  handoffNote() {
    return `[system] handoff done, note at ${this.lastHandoffFile ?? 'handoffs/'}`
  }
  handoffTail() {
    return []
  }
}

const core = {
  injected: [],
  toolsTagged: (tag) => new Set(tag === 'speak' ? ['pet_say', 'pet_ask'] : []),
  injectInternal: (text, kind) => core.injected.push({ kind, text }),
  injectExternal: (text, type) => core.injected.push({ kind: `external:${type}`, text }),
  log: { info: () => {}, warn: () => {}, error: () => {} },
}

// --- a window: 40 partner lines, 15 bubbles, noise, calls ----------------------
const t = (minute) => `2026-10-30T19:${String(minute).padStart(2, '0')}:00.000+08:00`
const user = (text, minute) => ({ item: { type: 'message', role: 'user', content: [{ type: 'input_text', text }] }, context: { ts: t(minute % 60) } })
const call = (id, name, args, minute, output) => [
  { item: { type: 'function_call', call_id: id, name, arguments: JSON.stringify(args) }, context: { ts: t(minute % 60) } },
  ...(output === undefined ? [] : [{ item: { type: 'function_call_output', call_id: id, output }, context: { ts: t(minute % 60) } }]),
]

const snapshot = []
const twoDaysAgo = '2026-10-28T10:00:00.000+08:00'
snapshot.push(
  { item: { type: 'function_call', call_id: 'old1', name: 'grep_files', arguments: '{"pattern":"zzz"}' }, context: { ts: twoDaysAgo } },
  { item: { type: 'function_call_output', call_id: 'old1', output: '0 个文件命中' }, context: { ts: twoDaysAgo } },
)
for (let i = 0; i < 40; i++) snapshot.push(user(`[19:${String(i).padStart(2, '0')}] [typed] 阿梦:第 ${i} 句话`, i))
snapshot.push(user('[19:40] [system] 3 new events.', 40))
for (let i = 0; i < 15; i++) snapshot.push(...call(`say${i}`, 'pet_say', { script: `气泡 ${i}` }, 41, '显示 3 秒'))
snapshot.push(...call('view1', 'sts2_view', { full: true }, 42, JSON.stringify({ ok: true, state: { screen: 'COMBAT', floor: 45, turn: 8, me: { hp: 20, max_hp: 80 } } })))
snapshot.push(...call('view2', 'sts2_view', { full: true }, 43, JSON.stringify({ ok: true, state: { screen: 'REWARD', floor: 46, turn: 0, me: { hp: 20, max_hp: 80 } } })))
snapshot.push(...call('shot', 'pet_cua_screenshot', {}, 44, '截图 1280×720 [blob log:abc123.jpg image/jpeg screen.jpg] 屏幕内容正文不该出现'))
snapshot.push(...call('wait1', 'sts2_wait', { seconds: 5 }, 45, JSON.stringify({ ok: true, state: { screen: 'MAP' } })))
snapshot.push(...call('wait2', 'sts2_wait', { seconds: 5 }, 46, JSON.stringify({ ok: true, state: { screen: 'MAP' } })))
snapshot.push(...call('edit', 'edit_file', { path: 'notes/x.md', old_string: 'x'.repeat(2000), new_string: 'y'.repeat(2000) }, 47, '[edited] notes/x.md 第 1 行起换了 1 处'))

const Persona = coopanionPersona(FakeBase, () => undefined)
const persona = new Persona()
persona.core = core
const result = await persona.onHandoff(snapshot, { hardTokens: 1_000_000 })

const notePath = join(persona.memoryDir, persona.lastHandoffFile)
const note = readFileSync(notePath, 'utf8')
const lines = note.split('\n')

const problems = []
const check = (ok, message) => { if (!ok) problems.push(message) }

check(result.tail.length === 0, 'tail should be empty')
check(persona.handedOff === true, 'handedOff should be set, or the next batch end asks for another handoff')
check(persona.pressureWarned === false && persona.pressureSplitAt === null, 'pressure state should reset')
check(core.injected.some((item) => item.kind === 'handoff' && item.text.includes('note at')), 'wake line should be injected')
check(core.injected.some((item) => item.kind === 'external:handoff-note'), 'note should be delivered as an event')

const partner = lines.filter((line) => line.startsWith('- 19:') && line.includes('[typed]'))
check(partner.length === 30, `partner lines: expected 30, got ${partner.length}`)
check(partner.at(-1).includes('第 39 句话'), 'the newest partner line should be kept')
check(!partner.some((line) => line.includes('第 9 句话')), 'lines older than the last 30 should be gone')

const bubbles = lines.slice(lines.indexOf('## 汐最近说的话(10 条)') + 1).filter((line) => line.startsWith('- 气泡'))
check(bubbles.length === 10, `bubbles: expected 10, got ${bubbles.length}`)

check(!note.includes('[system]'), 'system lines should not reach the note')
check(note.includes('- 上次说到:'), 'resume block should be there')
check(note.includes('## 做过的事'), 'tool section should be there')
check(/sts2_wait\(seconds=5\) → ok .*\(共 2 次\)/.test(note), 'two identical waits should collapse with a count')
check(!note.includes('grep_files'), 'a call from two days ago that nothing referred to should be dropped')
check(!note.includes('屏幕内容正文'), 'blob body should be reduced to its marker')
check(note.includes('[blob log:abc123.jpg]'), 'blob marker should stay')
check(note.includes('sts2_view(full=true) → ok · screen REWARD'), 'state readings should keep the last one, compressed')
check(note.split('sts2_view').length - 1 === 1, 'only the last state reading should be shown')
check(!note.includes('xxxx'), 'file bodies should not be in the argument digest')
check(note.includes('edit_file(path=notes/x.md)'), 'the path should survive in the digest')

const tooLong = lines.filter((line) => line.startsWith('- ') && line.length > 230)
check(tooLong.length === 0, `lines over 230 chars: ${tooLong.map((line) => line.slice(0, 40)).join(' | ')}`)

rmSync(persona.memoryDir, { recursive: true, force: true })

if (problems.length > 0) {
  console.error('FAIL')
  for (const problem of problems) console.error(`  - ${problem}`)
  console.error(`\nnote:\n${note}`)
  process.exit(1)
}
console.log(`ok — note ${note.length} chars, ${lines.length} lines, ${core.injected.length} injections`)
console.log(`     ${persona.lastHandoffFile}`)
