/**
 * 量一下延迟：普通 view 的耗时、结束回合后到「手牌出现」的时间、以及我们自己的稳定窗口开销。
 * 用法：node test/latency.mjs
 */
import world from '../index.js'

const host = { pushEvent: async () => ({ cursor: 0 }), store: {}, modelFacts: {}, blob: () => null, log: () => {} }
const ctx = {
  id: world.id,
  // waitTurnSeconds=0：结束回合立刻返回，方便观察「回合刚开始」的窗口
  cfg: { ...world.defaults(), enabled: true, watch: false, auto: false, waitTurnSeconds: 0 },
  timezone: 'Asia/Shanghai', botName: '汐', botDir: process.cwd(), packageDir: process.cwd(),
  dataDir: process.cwd(), repoRoot: process.cwd(), secret: () => '', storeSecret: () => {}, persist: async () => {}, restart: async () => {},
}
const instance = world.create(ctx)
const tools = new Map(instance.tools().map((tool) => [tool.name, tool]))
const call = async (name, args = {}) => JSON.parse(await tools.get(`sts2_${name}`).handler(args, { role: 'lat' }))
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

async function timed(name, args) {
  const start = Date.now()
  const result = await call(name, args)
  return { result, ms: Date.now() - start }
}

await instance.start(host)
try {
  let state = (await call('view', { full: true }))
  for (let i = 0; i < 25 && state.screen !== 'COMBAT'; i += 1) {
    if (state.screen === 'MAIN_MENU') await call('act', { action: state.actions.includes('continue_run') ? 'continue_run' : 'open_singleplayer' })
    else if (state.screen === 'SINGLEPLAYER_MENU') await call('act', { action: 'start_standard_run' })
    else if (state.screen === 'CHARACTER_SELECT') await call('act', { action: 'embark' })
    else if (state.screen === 'EVENT') await call('act', { action: state.event?.finished ? 'proceed' : 'choose_event_option', index: 0 })
    else if (state.screen === 'MAP') await call('act', { action: 'choose_map_node', index: state.map.options[0].i })
    else if (state.screen === 'REWARD') await call('act', { action: state.reward?.can_proceed ? 'proceed' : 'claim_reward', index: 0 })
    else if (state.screen === 'CARD_SELECTION') {
      const card = state.selection?.cards?.[0]
      if (card) await call('act', { action: 'select_card', card_ref: card.ref })
      await call('act', { action: 'confirm_selection' })
    }
    await sleep(1500)
    state = await call('view', { full: true })
  }
  if (state.screen !== 'COMBAT') { console.log('not in combat:', state.screen); process.exit(0) }

  for (let i = 0; i < 3; i += 1) {
    const { ms } = await timed('view', { full: true })
    console.log(`普通 view #${i + 1}: ${ms}ms`)
    await sleep(300)
  }

  const end = await timed('act', { action: 'end_turn' })
  console.log(`end_turn: ${end.ms}ms side=${end.result.state?.combat?.side} hand=${end.result.state?.combat?.hand?.length}`)

  let firstHand = null
  const t0 = Date.now()
  for (let i = 0; i < 40; i += 1) {
    const s = await call('view', { full: true })
    const hand = s.combat?.hand?.length ?? 0
    if (s.screen !== 'COMBAT' || (s.combat?.my_turn && hand > 0)) {
      firstHand = { ms: Date.now() - t0, hand, turn: s.combat?.turn, screen: s.screen }
      break
    }
    await sleep(100)
  }
  console.log('从 end_turn 到「手牌出现」:', firstHand)
} finally {
  await instance.stop()
}
