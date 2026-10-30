/** 端到端验证：World 的提醒推送（attention → host.pushEvent）。 */
import world from '../index.js'

const BASE = 'http://127.0.0.1:8090'
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
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))
const getState = async () => (await fetch(`${BASE}/state`)).json()
const act = async (body) => {
  const r = await fetch(`${BASE}/act`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) })
  return r.json()
}

await instance.start(host)
try {
  let s = await getState()
  for (let i = 0; i < 30; i += 1) {
    let body = null
    if (s.screen === 'MAIN_MENU') body = { action: s.actions?.includes('continue_run') ? 'continue_run' : 'open_singleplayer' }
    else if (s.screen === 'SINGLEPLAYER_MENU') body = { action: 'start_standard_run' }
    else if (s.screen === 'CHARACTER_SELECT') body = { action: 'embark' }
    else if (s.screen === 'EVENT') body = s.event?.finished ? { action: 'proceed' } : { action: 'choose_event_option', index: (s.event?.options ?? []).find((o) => !o.locked)?.i ?? 0 }
    else if (s.screen === 'MAP' && (s.map?.options ?? []).length) body = { action: 'choose_map_node', index: s.map.options[0].i }
    else if (s.screen === 'REWARD') {
      if (s.reward?.card_choice) body = { action: 'choose_reward_card', index: 0 }
      else if ((s.reward?.rewards ?? []).some((r) => r.claimable)) body = { action: 'claim_reward', index: s.reward.rewards.find((r) => r.claimable).i }
      else if (s.actions?.includes('proceed')) body = { action: 'proceed' }
    } else if (s.screen === 'GAME_OVER') body = { action: 'continue_game_over' }
    else if (s.screen === 'COMBAT' && s.combat?.my_turn) {
      const card = (s.combat.hand ?? []).find((c) => c.playable)
      if (card) {
        body = { action: 'play_card', card_ref: card.ref }
        if (card.target_type === 'AnyEnemy') {
          const enemy = (s.combat.enemies ?? []).find((e) => e.alive)
          if (enemy) body.target_id = enemy.combat_id
        }
      } else body = { action: 'end_turn' }
    }
    else if (s.screen === 'CARD_SELECTION') body = s.selection?.confirm_available ? { action: 'confirm_selection' } : null
    if (body) {
      const r = await act(body)
      if (r.ok === false) console.log(`  (${body.action} -> ${r.code})`)
    }
    await sleep(2600)
    s = await getState()
  }
  console.log(`共推送 ${pushed.length} 条`)
  for (const item of pushed) console.log(' -', item.text)
} finally {
  await instance.stop()
}
