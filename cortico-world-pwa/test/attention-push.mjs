/** 验证 World 真的把 attention 推成消息（用假的 host.pushEvent 收集）。 */
import world from '../index.js'

const pushed = []
const host = {
  pushEvent: async (event) => { pushed.push(event); console.log(`[push] ${event.type}: ${event.text}`); return { cursor: pushed.length } },
  store: {}, modelFacts: {}, blob: () => null, log: () => {},
}
const ctx = {
  id: world.id,
  cfg: { ...world.defaults(), enabled: true, watch: true, pollSeconds: 2, auto: false },
  timezone: 'Asia/Shanghai', botName: '汐', botDir: process.cwd(), packageDir: process.cwd(),
  dataDir: process.cwd(), repoRoot: process.cwd(), secret: () => '', storeSecret: () => {}, persist: async () => {}, restart: async () => {},
}
const instance = world.create(ctx)
const tools = new Map(instance.tools().map((tool) => [tool.name, tool]))
const call = async (name, args = {}) => JSON.parse(await tools.get(`sts2_${name}`).handler(args, { role: 't' }))
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))
const rest = async (body) => {
  const r = await fetch('http://127.0.0.1:8090/act', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) })
  return r.json()
}

await instance.start(host)
try {
  await sleep(5000)
  let state = await call('view', { full: true })
  console.log('screen=', state.screen, 'turn=', state.combat?.turn)
  if (state.screen === 'COMBAT' && state.combat?.my_turn) {
    const r = await rest({ action: 'end_turn' })
    console.log('end_turn ->', r.status)
    await sleep(9000)
  }
  console.log(`共推送 ${pushed.length} 条`)
} finally {
  await instance.stop()
}
