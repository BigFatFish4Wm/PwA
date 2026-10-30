/**
 * 校验自动推进：结束回合后回执应带新回合手牌；奖励屏自动收金币/药水；无牌可出自动结束回合。
 * 用法：node test/auto-check.mjs
 */
import world from '../index.js'

const host = { pushEvent: async () => ({ cursor: 0 }), store: {}, modelFacts: {}, blob: () => null, log: () => {} }
const ctx = {
  id: world.id,
  cfg: { ...world.defaults(), enabled: true, watch: false },
  timezone: 'Asia/Shanghai', botName: '汐', botDir: process.cwd(), packageDir: process.cwd(),
  dataDir: process.cwd(), repoRoot: process.cwd(), secret: () => '', storeSecret: () => {}, persist: () => {}, restart: async () => {},
}
const instance = world.create(ctx)
const tools = new Map(instance.tools().map((tool) => [tool.name, tool]))
const call = async (name, args = {}) => JSON.parse(await tools.get(`sts2_${name}`).handler(args, { role: 'auto' }))
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

await instance.start(host)
try {
  let state = await call('view', { full: true })
  for (let step = 0; step < 14; step += 1) {
    const auto = state.auto?.map((a) => a.action).join(',') ?? ''
    console.log(`[${step}] ${state.screen} hand=${state.combat?.hand?.length ?? '-'} energy=${state.combat?.me?.energy ?? '-'} auto=${auto || '-'}`)
    if (state.screen === 'COMBAT') {
      const card = (state.combat.hand ?? []).find((c) => c.playable)
      if (!card) {
        console.log('  no playable card → view should auto end turn')
        await sleep(1200)
        state = await call('view', { full: true })
        continue
      }
      const args = { action: 'play_card', card_ref: card.ref }
      if (card.targets?.length) args.target_index = card.targets[0]
      const result = await call('act', args)
      state = result.state ?? (await call('view', { full: true }))
      console.log(`  play ${card.name} → ${result.status} hand=${state?.combat?.hand?.length} auto=${result.auto?.map((a) => a.action).join(',') || '-'}`)
      continue
    }
    if (state.screen === 'REWARD') {
      console.log(`  rewards=${(state.reward.rewards ?? []).map((r) => r.kind).join(',')} card_choice=${state.reward.card_choice}`)
      if (state.reward.card_choice) { await call('act', { action: 'choose_reward_card', index: 0 }); await sleep(600); state = await call('view', { full: true }); continue }
      if (!(state.reward.rewards ?? []).length) { await call('act', { action: 'proceed' }); await sleep(800); state = await call('view', { full: true }); continue }
    }
    if (state.screen === 'MAP') { await call('act', { action: 'choose_map_node', index: state.map.options[0].i }); await sleep(1200); state = await call('view', { full: true }); continue }
    if (state.screen === 'EVENT') { await call('act', { action: state.event.finished ? 'proceed' : 'choose_event_option', index: 0 }); await sleep(800); state = await call('view', { full: true }); continue }
    if (state.screen === 'CHEST') { await call('act', { action: state.chest.opened ? 'proceed' : 'open_chest' }); await sleep(800); state = await call('view', { full: true }); continue }
    if (state.screen === 'CARD_SELECTION') {
      const card = state.selection?.cards?.[0]
      if (card) await call('act', { action: 'select_card', card_ref: card.ref })
      await call('act', { action: 'confirm_selection' })
      await sleep(600)
      state = await call('view', { full: true })
      continue
    }
    await sleep(800)
    state = await call('view', { full: true })
  }
} finally {
  await instance.stop()
}
