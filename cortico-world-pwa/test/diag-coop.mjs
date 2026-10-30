/** 联机窗口发现自检：打印 world 选中的窗口（应为 client / 8092）。 */
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
const call = async (name, args = {}) => JSON.parse(await tools.get(`sts2_${name}`).handler(args, { role: 'diag' }))

await instance.start(host)
try {
  const diag = await call('diag')
  console.log(JSON.stringify({ build: diag.build, base: diag.base, role: diag.role, windows: (diag.windows ?? []).map((w) => `${w.role}@${w.port}`) }))
  const state = await call('view', { full: true })
  console.log('view:', state.screen, 'floor=', state.run?.floor, 'players=', state.run?.players?.length)
} finally {
  await instance.stop()
}
