/**
 * 本地联调脚本：不经 Coopanion，直接把 World 实例跑起来，连真实游戏窗口。
 * 用法：node test/run.mjs [view|diag|act|plan]
 */
import world from '../index.js'

const events = []
const host = {
  pushEvent: async (event) => {
    events.push(event)
    console.log(`[event] ${event.type} ${event.text ?? ''}`)
    return { cursor: events.length }
  },
  store: {},
  modelFacts: {},
  blob: () => null,
  log: (...args) => console.log('[host]', ...args),
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
  const tool = tools.get(`sts2_${name}`) ?? tools.get(name)
  if (!tool) throw new Error(`no tool ${name}`)
  const text = await tool.handler(args, { role: 'test', log: console.log })
  return text
}

const mode = process.argv[2] ?? 'view'

await instance.start(host)
try {
  if (mode === 'diag') {
    console.log(await call('diag'))
  } else if (mode === 'meta') {
    const ui = instance.webUi('zh')
    console.log(JSON.stringify({
      id: instance.id,
      tools: instance.tools().map((tool) => ({ name: tool.name, tags: tool.tags, barrierAfter: tool.barrierAfter })),
      defaults: world.defaults(),
      envPromptVars: instance.envPromptVars(),
      webUi: { label: ui.label, lamps: ui.lamps, badges: ui.badges, config: ui.config, promptDocs: ui.promptDocs },
    }, null, 2))
  } else if (mode === 'act') {
    const [action, ...rest] = process.argv.slice(3)
    const args = { action }
    for (const pair of rest) {
      const [key, value] = pair.split('=')
      args[key] = Number.isNaN(Number(value)) ? value : Number(value)
    }
    console.log(await call('act', args))
  } else if (mode === 'plan') {
    const steps = JSON.parse(process.argv[3])
    console.log(await call('plan', { steps }))
  } else if (mode === 'combat') {
    const view = JSON.parse(await call('view', { full: true }))
    const hand = view.combat?.hand ?? []
    const card = hand.find((item) => item.playable && Array.isArray(item.targets) && item.targets.length)
      ?? hand.find((item) => item.playable)
    if (!card) {
      console.log('no playable card:', JSON.stringify(view.actions))
    } else {
      const step = { action: 'play_card', card_ref: card.ref }
      if (Array.isArray(card.targets) && card.targets.length) step.target_index = card.targets[0]
      const steps = [step]
      if (process.argv.includes('--end-turn')) steps.push({ action: 'end_turn' })
      console.log(await call('plan', { steps }))
    }
  } else if (mode === 'wait') {
    console.log(await call('wait', { seconds: Number(process.argv[3] ?? 8) }))
  } else {
    console.log(await call('view', { full: process.argv.includes('--full') }))
  }
} finally {
  await instance.stop()
}
