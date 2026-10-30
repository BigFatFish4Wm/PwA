/** 验证 一键联机 → 关闭 → 再开。 */
import world from '../index.js'

const host = { pushEvent: async () => ({ cursor: 0 }), store: {}, modelFacts: {}, blob: () => null, log: () => {} }
const ctx = {
  id: world.id,
  cfg: { ...world.defaults(), enabled: true, watch: false },
  timezone: 'Asia/Shanghai', botName: '汐', botDir: process.cwd(), packageDir: process.cwd(),
  dataDir: process.cwd(), repoRoot: process.cwd(), secret: () => '', storeSecret: () => {}, persist: async () => {}, restart: async () => {},
}
const instance = world.create(ctx)
const tools = new Map(instance.tools().map((tool) => [tool.name, tool]))
const call = async (name, args = {}) => JSON.parse(await tools.get(`sts2_${name}`).handler(args, { role: 'coop' }))
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

await instance.start(host)
try {
  console.log('1 现状:', JSON.stringify(await call('coop_state')))
  console.log('2 关闭:', JSON.stringify(await call('coop_stop')))
  await sleep(4000)
  console.log('3 关闭后:', JSON.stringify(await call('coop_state')))
  const started = await call('coop_start', { withClient: true })
  console.log('4 再开:', JSON.stringify(started))
  await sleep(3000)
  console.log('5 最终:', JSON.stringify(await call('coop_state')))
} finally {
  await instance.stop()
}
