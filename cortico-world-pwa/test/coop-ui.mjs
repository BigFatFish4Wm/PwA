/** 验证新的联机 UI 语义：host+汐(本机) + 远程位（不额外开窗）。 */
import world from '../index.js'

const sleep = (ms) => new Promise((r) => setTimeout(r, ms))
const host = {
  pushEvent: async () => ({ cursor: 0 }),
  store: {}, modelFacts: {}, blob: () => null, log: () => {},
}
const ctx = {
  id: world.id,
  cfg: { ...world.defaults(), enabled: true, watch: false, pollSeconds: 4, auto: false },
  timezone: 'Asia/Shanghai', botName: '汐', botDir: process.cwd(), packageDir: process.cwd(),
  dataDir: process.cwd(), repoRoot: process.cwd(), secret: () => '', storeSecret: () => {}, persist: async () => {}, restart: async () => {},
}
const instance = world.create(ctx)
const tools = new Map(instance.tools().map((tool) => [tool.name, tool.raw ?? tool]))
const call = async (name, args = {}) => JSON.parse(await tools.get(`sts2_${name}`).handler(args, { role: 't' }))

try {
  const started = await call('coop_start', { localClients: 1, remoteSlots: 2 })
  console.log('phase:', started.phase, 'detail:', started.detail)
  console.log('localClients:', started.localClients, 'remoteSlots:', started.remote.slots, 'token:', started.remote.token)
  console.log('endpoints:', started.remote.endpoints)
  console.log('command:\n' + started.remote.command)
  await sleep(4000)
  const status = await call('coop_state')
  console.log('peers:', status.peers, 'remote connected:', status.remote.connected)
  console.log('host pid:', status.host?.pid, 'client pid:', status.client?.pid)
  const hostHealth = await (await fetch('http://127.0.0.1:8091/health')).json()
  console.log('health.coop_peers:', hostHealth.coop_peers)
  const stopped = await call('coop_stop')
  console.log('stopped:', stopped.detail)
} finally {
  try { await instance.stop() } catch {}
}
