/**
 * 用 World 自己的工具把一局往下推：战斗、奖励、地图、事件、宝箱、营火、商店。
 * 目的不是打好，而是把每个房间/每类动作在真游戏里跑一遍。
 *
 * 用法：node test/drive.mjs [maxSteps]
 */
import world from '../index.js'

const maxSteps = Number(process.argv[2] ?? 60)

const host = {
  pushEvent: async (event) => {
    console.log(`[event] ${event.type} ${event.text ?? ''}`)
    return { cursor: 0 }
  },
  store: {},
  modelFacts: {},
  blob: () => null,
  log: () => {},
}

const ctx = {
  id: world.id,
  cfg: { ...world.defaults(), enabled: true },
  timezone: 'Asia/Shanghai',
  botName: '汐',
  botDir: process.cwd(),
  packageDir: process.cwd(),
  dataDir: process.cwd(),
  repoRoot: process.cwd(),
  secret: () => '',
  storeSecret: () => {},
  persist: () => {},
  restart: async () => {},
}

const instance = world.create(ctx)
const tools = new Map(instance.tools().map((tool) => [tool.name, tool]))

async function call(name, args = {}) {
  const tool = tools.get(`sts2_${name}`)
  if (!tool) throw new Error(`no tool ${name}`)
  const text = await tool.handler(args, { role: 'drive', log: () => {} })
  try {
    return JSON.parse(text)
  } catch {
    return text
  }
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms))

async function view() {
  return call('view', { full: true })
}

function pickCard(state) {
  const hand = state.combat?.hand ?? []
  const playable = hand.filter((card) => card.playable)
  if (!playable.length) return null
  const enemies = state.combat?.enemies ?? []
  const incoming = enemies.reduce((sum, enemy) => sum + (enemy.intents ?? []).reduce((acc, intent) => acc + (intent.total ?? intent.damage ?? 0), 0), 0)
  const hp = state.combat?.me?.hp ?? 0
  const isBlock = (card) => /DEFEND|BLOCK|SHRUG|GUARD/i.test(card.id ?? '') || /格挡/.test(card.text ?? '')
  const isAttack = (card) => Array.isArray(card.targets) && card.targets.length > 0

  if (incoming > 0 && hp < incoming + 8) {
    const block = playable.find(isBlock)
    if (block) return block
  }
  // 能斩杀先打：伤害牌优先，其次防御，最后其它。
  const attack = playable.find(isAttack)
  if (attack) return attack
  const block = playable.find(isBlock)
  if (block) return block
  return playable[0]
}

async function act(args) {
  const result = await call('act', args)
  const label = `${args.action}${args.card_ref ? `(${args.card_ref}` : ''}${args.target_index != null ? `->${args.target_index}` : ''}${args.card_ref ? ')' : ''}`
  console.log(`  ${label} => ${result.ok === false ? `FAIL ${result.code}: ${result.message}` : result.status ?? 'ok'}`)
  return result
}

async function fight(state) {
  for (let turn = 0; turn < 8; turn += 1) {
    if (state.screen !== 'COMBAT') return
    // 每场战斗第一次轮到自己时，先喝一瓶药，把 use_potion 这条路径也跑到。
    const potion = (state.run?.potions ?? []).find((item) => item.usable)
    if (turn === 0 && potion && state.combat?.my_turn) {
      await act({ action: 'use_potion', slot: potion.slot })
      await sleep(500)
      state = await view()
    }
    for (let play = 0; play < 6; play += 1) {
      state = await view()
      if (state.screen !== 'COMBAT' || !state.combat?.my_turn) break
      const card = pickCard(state)
      if (!card) break
      const args = { action: 'play_card', card_ref: card.ref }
      if (Array.isArray(card.targets) && card.targets.length) args.target_index = card.targets[0]
      const result = await act(args)
      if (result.ok === false) break
      if (result.state?.screen === 'CARD_SELECTION') {
        await handleSelection(result.state)
        state = await view()
        continue
      }
      await sleep(150)
    }
    state = await view()
    if (state.screen !== 'COMBAT') return
    // 只在真的轮到我、且动作列表允许时结束回合；否则等着（重复入队会卡死游戏）。
    if (!state.combat?.my_turn || !(state.actions ?? []).includes('end_turn')) {
      await call('wait', { seconds: 8 })
      state = await view()
      if (state.screen !== 'COMBAT') return
      continue
    }
    await act({ action: 'end_turn' })
    await sleep(1200)
    state = await view()
    if (state.screen !== 'COMBAT') return
  }
}

async function handleSelection(state) {
  const selection = state.selection ?? (await view()).selection
  const cards = selection?.cards ?? []
  for (let i = 0; i < cards.length; i += 1) {
    await act({ action: 'select_card', card_ref: cards[i].ref })
    await sleep(150)
    const now = await view()
    if (now.screen !== 'CARD_SELECTION') return
    if (now.selection?.confirm_available) break
  }
  if ((await view()).screen === 'CARD_SELECTION') {
    await act({ action: 'confirm_selection' })
    await sleep(400)
  }
}

async function handleReward(state) {
  const reward = state.reward
  if (reward?.card_choice) {
    const card = reward.cards?.[0]
    if (card) await act({ action: 'choose_reward_card', index: card.i ?? 0 })
    else await act({ action: 'skip_reward_cards' })
    await sleep(400)
    return
  }
  const items = reward?.rewards ?? []
  if (items.length) {
    await act({ action: 'claim_reward', index: items[0].i })
    await sleep(500)
    return
  }
  if (reward?.can_proceed) await act({ action: 'proceed' })
  await sleep(300)
}

async function handleMap(state) {
  const options = state.map?.options ?? []
  if (!options.length) {
    await sleep(600)
    return
  }
  // 优先去能测到新房间的节点。
  const rank = { Treasure: 0, RestSite: 1, Shop: 2, Event: 3, Ancient: 3, Elite: 5, Monster: 4, Boss: 6 }
  const sorted = [...options].sort((a, b) => (rank[a.type] ?? 9) - (rank[b.type] ?? 9))
  await act({ action: 'choose_map_node', index: sorted[0].i })
  await sleep(1500)
}

async function handleEvent(state) {
  const event = state.event
  if (event?.finished) {
    await act({ action: 'proceed' })
    await sleep(500)
    return
  }
  const option = (event?.options ?? []).find((item) => !item.locked)
  if (!option) {
    await act({ action: 'proceed' })
    await sleep(500)
    return
  }
  await act({ action: 'choose_event_option', index: option.i })
  await sleep(700)
}

async function handleChest(state) {
  const chest = state.chest
  if (!chest?.opened) {
    await act({ action: 'open_chest' })
    await sleep(700)
    return
  }
  const relic = (chest.relics ?? [])[0]
  if (relic) {
    await act({ action: 'choose_treasure_relic', index: relic.i })
    await sleep(700)
    return
  }
  if (chest.can_proceed) await act({ action: 'proceed' })
  await sleep(400)
}

async function handleRest(state) {
  const options = state.rest?.options ?? []
  const option = options.find((item) => item.enabled && item.requires_target === false) ?? options.find((item) => item.enabled)
  if (option) {
    await act({ action: 'choose_rest_option', index: option.i })
    await sleep(900)
    return
  }
  if (state.rest?.can_proceed) await act({ action: 'proceed' })
  await sleep(400)
}

async function handleShop(state) {
  const shop = state.shop
  if (!shop?.open) {
    if (shop?.can_proceed) {
      await act({ action: 'proceed' })
      await sleep(500)
      return
    }
    await act({ action: 'open_shop_inventory' })
    await sleep(600)
    return
  }
  await act({ action: 'close_shop_inventory' })
  await sleep(400)
}

await instance.start(host)
try {
  let state = await view()
  for (let step = 0; step < maxSteps; step += 1) {
    state = await view()
    console.log(`[${step}] screen=${state.screen} turn=${state.turn} floor=${state.run?.floor} actions=${(state.actions ?? []).join(',')}`)
    if (state.screen === 'COMBAT') await fight(state)
    else if (state.screen === 'MAIN_MENU') {
      const actions = state.actions ?? []
      if (actions.includes('continue_run')) {
        const result = await act({ action: 'continue_run' })
        if (result.ok === false) await act({ action: 'open_singleplayer' })
      } else {
        await act({ action: 'open_singleplayer' })
      }
      await sleep(1200)
    } else if (state.screen === 'SINGLEPLAYER_MENU') {
      await act({ action: 'start_standard_run' })
      await sleep(1200)
    } else if (state.screen === 'CHARACTER_SELECT') {
      await act({ action: 'embark' })
      await sleep(2500)
    } else if (state.screen === 'CARD_SELECTION') await handleSelection(state)
    else if (state.screen === 'REWARD') await handleReward(state)
    else if (state.screen === 'MAP') await handleMap(state)
    else if (state.screen === 'EVENT') await handleEvent(state)
    else if (state.screen === 'CHEST') await handleChest(state)
    else if (state.screen === 'REST') await handleRest(state)
    else if (state.screen === 'SHOP') await handleShop(state)
    else if (state.screen === 'MODAL') await act({ action: 'confirm_modal' })
    else if (state.screen === 'GAME_OVER') {
      // 先 Continue 放完总结，再 MainMenu 回主菜单；多按几次直到离开结算页。
      for (let i = 0; i < 3; i += 1) {
        const now = await view()
        if (now.screen !== 'GAME_OVER') break
        await act({ action: 'continue_game_over' })
        await sleep(1200)
      }
    } else {
      await sleep(800)
    }
  }
  const final = await view()
  console.log('FINAL', JSON.stringify({ screen: final.screen, floor: final.run?.floor, actions: final.actions }))
} finally {
  await instance.stop()
}
