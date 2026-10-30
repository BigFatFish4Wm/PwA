/**
 * v0.6 校验：敌人 combat_id 稳定目标、piles.total、end_turn 回执必须是新回合可出牌态。
 * 用法：node test/verify-v6.mjs
 */
import world from '../index.js'

const host = { pushEvent: async () => ({ cursor: 0 }), store: {}, modelFacts: {}, blob: () => null, log: () => {} }
const ctx = {
  id: world.id,
  cfg: { ...world.defaults(), enabled: true, watch: false, auto: false, waitTurnSeconds: 10 },
  timezone: 'Asia/Shanghai', botName: '汐', botDir: process.cwd(), packageDir: process.cwd(),
  dataDir: process.cwd(), repoRoot: process.cwd(), secret: () => '', storeSecret: () => {}, persist: async () => {}, restart: async () => {},
}
const instance = world.create(ctx)
const tools = new Map(instance.tools().map((tool) => [tool.name, tool]))
const call = async (name, args = {}) => JSON.parse(await tools.get(`sts2_${name}`).handler(args, { role: 'v6' }))
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

await instance.start(host)
try {
  let state = await call('view', { full: true })
  for (let i = 0; i < 30 && state.screen !== 'COMBAT'; i += 1) {
    if (state.screen === 'MAIN_MENU') await call('act', { action: state.actions.includes('continue_run') ? 'continue_run' : 'open_singleplayer' })
    else if (state.screen === 'SINGLEPLAYER_MENU') await call('act', { action: 'start_standard_run' })
    else if (state.screen === 'CHARACTER_SELECT') await call('act', { action: 'embark' })
    else if (state.screen === 'EVENT') await call('act', { action: state.event?.finished ? 'proceed' : 'choose_event_option', index: 0 })
    else if (state.screen === 'MAP') await call('act', { action: 'choose_map_node', index: state.map.options[0].i })
    else if (state.screen === 'REWARD') await call('act', { action: state.reward?.can_proceed ? 'proceed' : 'claim_reward', index: 0 })
    else if (state.screen === 'CARD_SELECTION') {
      if (state.selection?.kind === 'bundle') await call('act', { action: 'choose_bundle', index: 0 })
      else {
        const card = state.selection?.cards?.[0]
        if (card) await call('act', { action: 'select_card', card_ref: card.ref })
        if (state.selection?.confirm_available) await call('act', { action: 'confirm_selection' })
      }
    }
    await sleep(1200)
    state = await call('view', { full: true })
  }

  if (state.screen !== 'COMBAT') { console.log('not in combat:', state.screen); process.exit(0) }

  console.log('piles=', JSON.stringify(state.combat.piles))
  const enemy = state.combat.enemies.find((item) => item.alive)
  console.log('enemy:', enemy?.i, enemy?.id, 'combat_id=', enemy?.combat_id)
  const card = (state.combat.hand ?? []).find((c) => c.playable && c.target_type === 'AnyEnemy')
  if (card && enemy) {
    const result = await call('act', { action: 'play_card', card_ref: card.ref, target_id: enemy.combat_id })
    console.log('play by target_id:', result.ok, result.status, result.extra?.card ?? result.message)
  } else {
    console.log('no enemy-target card this turn')
  }

  const before = (await call('view', { full: true })).combat?.turn ?? 0
  const end = await call('act', { action: 'end_turn' })
  const after = end.state?.combat
  console.log(`end_turn: turn ${before} -> ${after?.turn}, phase=${after?.turn_phase}, energy=${after?.me?.energy}, hand=${after?.hand?.length}, status=${end.status}`)
} finally {
  await instance.stop()
}
