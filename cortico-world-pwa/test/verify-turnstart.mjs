/**
 * 校验四个小问题：in_combat、回合开始空手牌、卡面 res:// 残留、药水结算。
 * 用法：node test/verify-turnstart.mjs
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

function findRes(value, path = '') {
  const hits = []
  if (typeof value === 'string') {
    if (value.includes('res://')) hits.push(`${path}=${value}`)
  } else if (Array.isArray(value)) {
    value.forEach((item, index) => hits.push(...findRes(item, `${path}[${index}]`)))
  } else if (value && typeof value === 'object') {
    for (const [key, item] of Object.entries(value)) hits.push(...findRes(item, `${path}.${key}`))
  }
  return hits
}

await instance.start(host)
try {
  let state = await call('view', { full: true })
  if (state.screen === 'MAIN_MENU') {
    await call('act', { action: state.actions.includes('continue_run') ? 'continue_run' : 'open_singleplayer' })
    await sleep(1500)
    state = await call('view', { full: true })
  }
  if (state.screen === 'SINGLEPLAYER_MENU') { await call('act', { action: 'start_standard_run' }); await sleep(1200); state = await call('view', { full: true }) }
  if (state.screen === 'CHARACTER_SELECT') { await call('act', { action: 'embark' }); await sleep(3000); state = await call('view', { full: true }) }
  for (let i = 0; i < 8 && state.screen !== 'COMBAT'; i += 1) {
    if (state.screen === 'EVENT') await call('act', { action: state.event?.finished ? 'proceed' : 'choose_event_option', index: 0 })
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

  if (state.screen !== 'COMBAT') {
    console.log('not in combat, stop:', state.screen)
  } else {
    console.log('in_combat =', state.in_combat)
    const resHits = findRes(state)
    console.log('res:// leftovers =', resHits.length, resHits.slice(0, 3))

    // 打完这一回合，再看下一回合开始时的回执
    let current = state
    for (let i = 0; i < 4 && current.combat?.my_turn; i += 1) {
      const card = (current.combat.hand ?? []).find((c) => c.playable && c.targets?.length)
      if (card) await call('act', { action: 'play_card', card_ref: card.ref, target_index: card.targets[0] })
      else break
      current = await call('view', { full: true })
    }
    const end = await call('act', { action: 'end_turn' })
    console.log('end_turn 回执: side=', end.state?.combat?.side, 'hand=', end.state?.combat?.hand?.length)

    const waited = await call('wait', { seconds: 12, until: 'actionable' })
    const next = waited.state ?? (await call('view', { full: true }))
    console.log('下一回合: my_turn=', next.combat?.my_turn, 'energy=', next.combat?.me?.energy, 'hand=', next.combat?.hand?.length)
    console.log('下一回合 in_combat=', next.in_combat, 'res leftovers=', findRes(next).length)
  }
} finally {
  await instance.stop()
}
