/** 验证联机启动时把创意工坊 Mod 链进 mods\ 并加载。 */
import world from '../index.js'
import { readFileSync } from 'node:fs'
import { join } from 'node:path'

const sleep = (ms) => new Promise((r) => setTimeout(r, ms))
const host = { pushEvent: async () => ({ cursor: 0 }), store: {}, modelFacts: {}, blob: () => null, log: () => {} }
const ctx = {
  id: world.id,
  cfg: { ...world.defaults(), enabled: true, watch: false, pollSeconds: 4, auto: false },
  timezone: 'Asia/Shanghai', botName: '汐', botDir: process.cwd(), packageDir: process.cwd(),
  dataDir: process.cwd(), repoRoot: process.cwd(), secret: () => '', storeSecret: () => {}, persist: async () => {}, restart: async () => {},
}
const instance = world.create(ctx)
const tools = new Map(instance.tools().map((tool) => [tool.name, tool]))

try {
  const started = await tools.get('sts2_coop_start').handler(
    { localClients: 1, remoteSlots: 0, withWorkshopMods: true, workshopOnly: 'silentSkin' },
    { role: 't' },
  )
  const parsed = JSON.parse(started)
  console.log('phase:', parsed.phase)
  console.log('workshop:', JSON.stringify(parsed.workshop))
  const logPath = join(process.env.APPDATA, 'SlayTheSpire2', 'logs', 'godot.log')
  const lines = readFileSync(logPath, 'utf8').split(/\r?\n/)
  console.log(lines.filter((line) => /Loaded \d+ mods/.test(line)).slice(-2).join('\n'))
  const stopped = JSON.parse(await tools.get('sts2_coop_stop').handler({}, { role: 't' }))
  console.log('stop:', stopped.detail)
} finally {
  try { await instance.stop() } catch {}
}
