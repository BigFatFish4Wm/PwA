/** 验证 attention 事件覆盖：推进一局，打印每个新出现的 attention。 */
const BASE = process.argv[2] || 'http://127.0.0.1:8090'
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

async function state() {
  const r = await fetch(`${BASE}/state`)
  return r.ok ? r.json() : null
}
async function act(body) {
  const r = await fetch(`${BASE}/act`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) })
  return r.json()
}
let since = 0
const seen = []
async function drainEvents() {
  const r = await fetch(`${BASE}/events?since=${since}`)
  if (!r.ok) return
  const j = await r.json()
  for (const e of j.events ?? []) {
    if (e.type === 'attention') {
      const d = e.data ?? {}
      const key = `${d.kind}|${d.screen}|${d.turn}|${d.detail}`
      if (!seen.includes(key)) { seen.push(key); console.log(`  [attention] ${key}`) }
    }
  }
  since = Math.max(since, Number(j.last_seq) ?? 0)
}

let s = await state()
for (let i = 0; i < 45; i += 1) {
  await drainEvents()
  if (!s) break
  const actions = s.actions ?? []
  const combat = s.combat ?? {}
  console.log(`${i}: ${s.screen} floor=${s.run?.floor} actions=${actions.join(',')}`)
  let action = null
  if (s.screen === 'MAIN_MENU') action = actions.includes('continue_run') ? { action: 'continue_run' } : { action: 'open_singleplayer' }
  else if (s.screen === 'SINGLEPLAYER_MENU') action = { action: 'start_standard_run' }
  else if (s.screen === 'CHARACTER_SELECT') action = { action: 'embark' }
  else if (s.screen === 'EVENT') action = s.event?.finished ? { action: 'proceed' } : { action: 'choose_event_option', index: (s.event?.options ?? []).find((o) => !o.locked)?.i ?? 0 }
  else if (s.screen === 'MAP' && (s.map?.options ?? []).length) action = { action: 'choose_map_node', index: s.map.options[0].i }
  else if (s.screen === 'REWARD') {
    if (s.reward?.card_choice) action = { action: 'choose_reward_card', index: 0 }
    else if ((s.reward?.rewards ?? []).some((r) => r.claimable)) action = { action: 'claim_reward', index: s.reward.rewards.find((r) => r.claimable).i }
    else if (actions.includes('proceed')) action = { action: 'proceed' }
  } else if (s.screen === 'CARD_SELECTION') {
    const sel = s.selection ?? {}
    if (sel.kind === 'bundle') action = { action: 'choose_bundle', index: 0 }
    else {
      const card = (sel.cards ?? []).find((c) => c.selected !== true)
      if (card && (sel.max == null || (sel.selected_count ?? 0) < sel.max)) action = { action: 'select_card', card_ref: card.ref }
      else if (sel.confirm_available) action = { action: 'confirm_selection' }
    }
  } else if (s.screen === 'COMBAT') {
    const card = (combat.hand ?? []).find((c) => c.playable)
    if (combat.side === 'Player' && combat.my_turn && card) {
      const body = { action: 'play_card', card_ref: card.ref }
      if (card.target_type === 'AnyEnemy') {
        const enemy = (combat.enemies ?? []).find((e) => e.alive)
        if (enemy) body.target_id = enemy.combat_id
      }
      action = body
    } else if (combat.side === 'Player' && combat.my_turn) action = { action: 'end_turn' }
  } else if (s.screen === 'CHEST') {
    if (s.chest?.opened !== true) action = { action: 'open_chest' }
    else if ((s.chest?.relics ?? []).length) action = { action: 'choose_treasure_relic', index: 0 }
    else action = { action: 'proceed' }
  } else if (s.screen === 'REST') action = actions.includes('choose_rest_option') ? { action: 'choose_rest_option', index: 0 } : { action: 'proceed' }
  else if (s.screen === 'SHOP') action = { action: 'proceed' }
  else if (s.screen === 'MODAL') action = { action: 'confirm_modal' }

  if (action) {
    const r = await act(action)
    if (r.ok === false) console.log(`  act ${action.action} 失败: ${r.code} ${r.message ?? ''}`)
  }
  await sleep(1200)
  s = await state()
  await drainEvents()
}
console.log('seen attention:', seen.length)
