/**
 * 校验：plan 跑完不会自动结束回合（即使手牌打空、能量 0）。
 * 用法：node test/plan-endturn.mjs
 */
import world from '../index.js'

const host = { pushEvent: async () => ({ cursor: 0 }), store: {}, modelFacts: {}, blob: () => null, log: () => {} }
const ctx = {
  id: world.id,
  cfg: { ...world.defaults(), enabled: true, watch: false, auto: true, autoEndTurn: true, waitTurnSeconds: 8 },
  timezone: 'Asia/Shanghai', botName: '汐', botDir: process.cwd(), packageDir: process.cwd(),
  dataDir: process.cwd(), repoRoot: process.cwd(), secret: () => '', storeSecret: () => {}, persist: async () => {}, restart: async () => {},
}
const instance = world.create(ctx)
const tools = new Map(instance.tools().map((tool) => [tool.name, tool]))
const call = async (name, args = {}) => JSON.parse(await tools.get(`sts2_${name}`).handler(args, { role: 'plan' }))
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

  const turnBefore = state.combat.turn
  const steps = (state.combat.hand ?? [])
    .filter((card) => card.playable)
    .slice(0, 3)
    .map((card) => {
      const step = { action: 'play_card', card_ref: card.ref }
      if (card.targets?.length) step.target_id = state.combat.enemies.find((enemy) => enemy.alive)?.combat_id
      return step
    })
  if (!steps.length) { console.log('no playable card'); process.exit(0) }

  const plan = await call('plan', { steps })
  const autoActions = (plan.auto ?? []).map((entry) => entry.action ?? entry.checked)
  const turnAfter = plan.state?.combat?.turn
  console.log('plan steps:', steps.length, 'ok=', plan.ok, 'auto=', JSON.stringify(autoActions))
  console.log(`turn ${turnBefore} -> ${turnAfter}, hand=${plan.state?.combat?.hand?.length}, energy=${plan.state?.combat?.me?.energy}`)
  console.log(autoActions.includes('end_turn') ? 'FAIL: plan 自动结束了回合' : 'PASS: plan 没有替你结束回合')
} finally {
  await instance.stop()
}
