/**
 * 双窗口联机 soak：同时驱动 host/client，逐步对比两边状态。
 * 用法：node tools/soak-coop.mjs [--rounds 400] [--host http://127.0.0.1:8091] [--client http://127.0.0.1:8092]
 */
const args = process.argv.slice(2)
const arg = (name, fallback) => {
  const i = args.indexOf(name)
  return i >= 0 && args[i + 1] ? args[i + 1] : fallback
}
const HOST = arg('--host', 'http://127.0.0.1:8091')
const CLIENT = arg('--client', 'http://127.0.0.1:8092')
const ROUNDS = Number(arg('--rounds', '400'))

const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

async function getState(base) {
  const response = await fetch(`${base}/state`)
  if (!response.ok) return null
  return response.json()
}

async function act(base, body) {
  const response = await fetch(`${base}/act`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body),
  })
  return response.json()
}

function pickAction(state) {
  if (!state) return null
  const actions = state.actions ?? []
  const screen = state.screen
  if (screen === 'MODAL') return { action: 'confirm_modal' }
  if (screen === 'COMBAT') {
    const combat = state.combat
    if (!combat || combat.side !== 'Player' || combat.my_turn !== true) return null
    const card = (combat.hand ?? []).find((item) => item.playable)
    if (card) {
      const body = { action: 'play_card', card_ref: card.ref }
      if (card.target_type === 'AnyEnemy') {
        const enemy = (combat.enemies ?? []).find((item) => item.alive)
        if (enemy) body.target_id = enemy.combat_id
      }
      return body
    }
    return { action: 'end_turn' }
  }
  if (screen === 'CARD_SELECTION') {
    const selection = state.selection ?? {}
    if (selection.kind === 'bundle') return { action: 'choose_bundle', index: 0 }
    const max = typeof selection.max === 'number' ? selection.max : null
    const count = selection.selected_count ?? 0
    const unselected = (selection.cards ?? []).filter((item) => item.selected !== true)
    if (max != null && count >= max) {
      return selection.confirm_available ? { action: 'confirm_selection' } : null
    }
    if (unselected.length > 0) return { action: 'select_card', card_ref: unselected[0].ref }
    if (selection.confirm_available) return { action: 'confirm_selection' }
    return null
  }
  if (screen === 'REWARD') {
    const reward = state.reward ?? {}
    if (reward.card_choice === true) return { action: 'choose_reward_card', index: 0 }
    const rewardItem = (reward.rewards ?? []).find((item) => item.claimable)
    if (rewardItem) return { action: 'claim_reward', index: rewardItem.i }
    if (actions.includes('proceed')) return { action: 'proceed' }
    return null
  }
  if (screen === 'EVENT') {
    const event = state.event ?? {}
    const option = (event.options ?? []).find((item) => item.was_chosen !== true && item.locked !== true)
    if (!event.finished && option) return { action: 'choose_event_option', index: option.i }
    if (actions.includes('proceed')) return { action: 'proceed' }
    return null
  }
  if (screen === 'CHEST') {
    if (state.chest?.opened !== true && actions.includes('open_chest')) return { action: 'open_chest' }
    if ((state.chest?.relics ?? []).length > 0) return { action: 'choose_treasure_relic', index: 0 }
    if (actions.includes('proceed')) return { action: 'proceed' }
    return null
  }
  if (screen === 'REST') {
    if (actions.includes('choose_rest_option')) return { action: 'choose_rest_option', index: 0 }
    if (actions.includes('proceed')) return { action: 'proceed' }
    return null
  }
  if (screen === 'SHOP') return actions.includes('proceed') ? { action: 'proceed' } : null
  if (screen === 'MAP') {
    const node = (state.map?.options ?? [])[0]
    return node ? { action: 'choose_map_node', index: node.i } : null
  }
  if (screen === 'GAME_OVER') return null
  return null
}

function snapshot(state) {
  if (!state) return { missing: true }
  const combat = state.combat ?? {}
  return {
    screen: state.screen,
    floor: state.run?.floor ?? null,
    act: state.run?.act ?? state.run?.act_index ?? null,
    turn: combat.turn ?? null,
    side: combat.side ?? null,
    hp: combat.me?.hp ?? null,
    hand: (combat.hand ?? []).length,
    enemies: (combat.enemies ?? []).map((enemy) => `${enemy.name}:${enemy.hp}`).join(','),
    gold: state.run?.gold ?? null,
    deck: state.run?.deck_size ?? null,
    event: state.event?.id ?? null,
    map: (state.map?.options ?? []).map((option) => option.coord).join(','),
    reward: (state.reward?.rewards ?? []).map((item) => `${item.kind}:${item.claimable ? 1 : 0}`).join(','),
  }
}

function diff(hostSnap, clientSnap) {
  const keys = Object.keys(hostSnap)
  const diffs = []
  for (const key of keys) {
    if (String(hostSnap[key]) !== String(clientSnap[key])) {
      diffs.push(`${key}: host=${hostSnap[key]} client=${clientSnap[key]}`)
    }
  }
  return diffs
}

let lastKey = ''
let mismatches = 0
let steps = 0

for (let round = 0; round < ROUNDS; round += 1) {
  const [host, client] = await Promise.all([getState(HOST), getState(CLIENT)])
  if (!host || !client) {
    console.log(`[soak] ${round}: 窗口失联 host=${Boolean(host)} client=${Boolean(client)}`)
    break
  }

  const hostSnap = snapshot(host)
  const clientSnap = snapshot(client)
  const key = `${hostSnap.screen}|${hostSnap.floor}|${hostSnap.turn}|${hostSnap.enemies}`
  if (key !== lastKey) {
    lastKey = key
    console.log(`[soak] ${round}: host=${hostSnap.screen}@${hostSnap.floor} turn=${hostSnap.turn} | client=${clientSnap.screen}@${clientSnap.floor} turn=${clientSnap.turn}`)
  }

  const diffs = diff(hostSnap, clientSnap)
  if (diffs.length > 0) {
    mismatches += 1
    console.log(`[soak] 不同步 #${mismatches}: ${diffs.join(' | ')}`)
  }

  if (hostSnap.screen === 'GAME_OVER' && clientSnap.screen === 'GAME_OVER') {
    console.log('[soak] 双方都到 GAME_OVER，结束')
    break
  }

  const hostAction = pickAction(host)
  const clientAction = pickAction(client)
  if (!hostAction && !clientAction) {
    await sleep(800)
    continue
  }

  if (hostAction) {
    const result = await act(HOST, hostAction)
    steps += 1
    if (result.ok === false) console.log(`[soak] host ${hostAction.action} 失败: ${result.code} ${result.message ?? ''}`)
  }
  if (clientAction) {
    const result = await act(CLIENT, clientAction)
    steps += 1
    if (result.ok === false) console.log(`[soak] client ${clientAction.action} 失败: ${result.code} ${result.message ?? ''}`)
  }
  await sleep(350)
}

console.log(`[soak] 结束：steps=${steps} 不同步次数=${mismatches}`)
