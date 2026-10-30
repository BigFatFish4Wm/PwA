/**
 * 校验「回执里的 state 是否已经是动作之后的局面」：
 * 出牌后敌人血量应立刻下降，结束回合后不应再报「我方回合 + 空手牌」。
 * 用法：node test/verify-settle.mjs
 */
import world from '../index.js'

const host = { pushEvent: async () => ({ cursor: 0 }), store: {}, modelFacts: {}, blob: () => null, log: () => {} }
const ctx = {
  id: world.id,
  cfg: { ...world.defaults(), enabled: true, watch: false },
  timezone: 'Asia/Shanghai', botName: '汐', botDir: process.cwd(), packageDir: process.cwd(),
  dataDir: process.cwd(), repoRoot: process.cwd(), secret: () => '', storeSecret: () => {}, persist: () => {},
  restart: async () => {},
}
const instance = world.create(ctx)
const tools = new Map(instance.tools().map((tool) => [tool.name, tool]))
const call = async (name, args = {}) => JSON.parse(await tools.get(`sts2_${name}`).handler(args, { role: 'verify' }))
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

await instance.start(host)
try {
  let state = await call('view', { full: true })
  if (state.screen === 'MAIN_MENU') {
    await call('act', { action: state.actions.includes('continue_run') ? 'continue_run' : 'open_singleplayer' })
    await sleep(1500)
    state = await call('view', { full: true })
  }
  if (state.screen === 'SINGLEPLAYER_MENU') {
    await call('act', { action: 'start_standard_run' }); await sleep(1200)
    state = await call('view', { full: true })
  }
  if (state.screen === 'CHARACTER_SELECT') {
    await call('act', { action: 'embark' }); await sleep(3000)
    state = await call('view', { full: true })
  }
  for (let i = 0; i < 6 && state.screen === 'EVENT'; i += 1) {
    await call('act', { action: state.event?.finished ? 'proceed' : 'choose_event_option', index: 0 })
    await sleep(900)
    state = await call('view', { full: true })
  }
  while (state.screen === 'MAP') {
    await call('act', { action: 'choose_map_node', index: state.map.options[0].i })
    await sleep(1800)
    state = await call('view', { full: true })
  }

  if (state.screen !== 'COMBAT') {
    console.log('not in combat, stop:', state.screen)
  } else {
    const card = (state.combat.hand ?? []).find((c) => c.playable && c.targets?.length)
      ?? (state.combat.hand ?? []).find((c) => c.playable)
    const enemyBefore = state.combat.enemies[0]
    console.log(`before: hp=${state.combat.me.hp} energy=${state.combat.me.energy} enemy=${enemyBefore.hp} hand=${state.combat.hand.length}`)
    const result = await call('act', { action: 'play_card', card_ref: card.ref, target_index: card.targets?.[0] })
    const after = result.state
    const view = await call('view', { full: true })
    console.log(`act state: energy=${after.combat?.me?.energy} enemy=${after.combat?.enemies?.[0]?.hp} hand=${after.combat?.hand?.length}`)
    console.log(`view state: energy=${view.combat?.me?.energy} enemy=${view.combat?.enemies?.[0]?.hp} hand=${view.combat?.hand?.length}`)
    console.log(`enemy damage visible in act response: ${enemyBefore.hp - (after.combat?.enemies?.[0]?.hp ?? enemyBefore.hp)}`)

    const end = await call('act', { action: 'end_turn' })
    const afterEnd = end.state
    console.log(`after end_turn: side=${afterEnd.combat?.side} my_turn=${afterEnd.combat?.my_turn} energy=${afterEnd.combat?.me?.energy} hand=${afterEnd.combat?.hand?.length}`)
  }
} finally {
  await instance.stop()
}
