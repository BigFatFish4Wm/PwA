/** 校验重复提交防护：同一动作在未落定时第二次应被拒。 */
import world from '../index.js'

const host = { pushEvent: async () => ({ cursor: 0 }), store: {}, modelFacts: {}, blob: () => null, log: () => {} }
const ctx = {
  id: world.id,
  cfg: { ...world.defaults(), enabled: true, watch: false, auto: false },
  timezone: 'Asia/Shanghai', botName: '汐', botDir: process.cwd(), packageDir: process.cwd(),
  dataDir: process.cwd(), repoRoot: process.cwd(), secret: () => '', storeSecret: () => {}, persist: async () => {}, restart: async () => {},
}
const instance = world.create(ctx)
const tools = new Map(instance.tools().map((tool) => [tool.name, tool]))
const call = async (name, args = {}) => JSON.parse(await tools.get(`sts2_${name}`).handler(args, { role: 'dup' }))

await instance.start(host)
try {
  const state = await call('view', { full: true })
  const card = (state.combat?.hand ?? []).find((c) => c.playable && c.targets?.length)
  if (!card) {
    console.log('no playable card:', state.screen)
  } else {
    const args = { action: 'play_card', card_ref: card.ref, target_index: card.targets[0] }
    const first = await call('act', args)
    console.log('first:', first.ok, first.status, first.extra?.needs_selection ?? '')
    const second = await call('act', args)
    console.log('second:', second.ok, second.code, second.message)
  }
} finally {
  await instance.stop()
}
