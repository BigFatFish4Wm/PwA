/** 验证一键联机：调用 sts2_coop_start，等两个窗口就绪后打印状态。 */
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

await instance.start(host)
try {
  console.log('before:', JSON.stringify(await call('coop_state')))
  const started = await call('coop_start', { withClient: true })
  console.log('start:', JSON.stringify(started))
  const state = await call('coop_state')
  console.log('after:', JSON.stringify(state))
} finally {
  await instance.stop()
}
