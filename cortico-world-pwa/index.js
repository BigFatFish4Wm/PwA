/**
 * 杀戮尖塔 2 的 Coopanion World：把 PwA 桥接 Mod 的本机 HTTP 接口翻成汐的工具。
 *
 * Mod 在每个游戏窗口里起一个 127.0.0.1 服务（单人 8090 / host 8091 / client 8092），
 * 并在 %LOCALAPPDATA%\PwA\endpoints\ 留端点文件。World 只当 HTTP 客户端：
 * 不加载 DLL、不碰游戏内存，只读状态、发原子动作。
 */
import { readdir, readFile } from 'node:fs/promises'
import { spawn } from 'node:child_process'
import { join } from 'node:path'
import { homedir, tmpdir } from 'node:os'
import { fileURLToPath } from 'node:url'

const WORLD_ID = 'pwa'
const ENV_PROMPT_FILE = fileURLToPath(new URL('./ENV_PROMPT.md', import.meta.url))
const WORLD_BUILD = '1.1.0'
const TOOL_PREFIX = 'sts2_'

/** 这些动作不受 state.actions 限制（菜单/诊断类）。 */
const ALWAYS_ALLOWED = new Set([
  'back', 'continue_run', 'open_singleplayer', 'start_standard_run', 'inspect_tree', 'click_node',
])

const SCENE_KEYS = ['map', 'event', 'reward', 'shop', 'rest', 'chest', 'selection', 'modal', 'singleplayer', 'character_select', 'game_over']

function endpointDir() {
  const base = process.env.LOCALAPPDATA || join(homedir(), 'AppData', 'Local') || tmpdir()
  return join(base, 'PwA', 'endpoints')
}

function parseMaybeJson(text) {
  if (typeof text !== 'string') return text
  const trimmed = text.trim()
  if (!trimmed.startsWith('{') && !trimmed.startsWith('[')) return text
  try {
    return JSON.parse(trimmed)
  } catch {
    return text
  }
}

async function httpJson(base, path, options = {}) {
  const timeoutMs = options.timeoutMs ?? 10000
  const controller = new AbortController()
  const timer = setTimeout(() => controller.abort(), timeoutMs)
  try {
    const response = await fetch(base + path, {
      method: options.method ?? 'GET',
      headers: { 'content-type': 'application/json', accept: 'application/json' },
      body: options.body === undefined ? undefined : JSON.stringify(options.body),
      signal: controller.signal,
    })
    const text = await response.text()
    let json = null
    try {
      json = text ? JSON.parse(text) : null
    } catch {
      json = null
    }
    return { ok: response.ok, status: response.status, json, text }
  } catch (error) {
    return { ok: false, status: 0, json: null, text: '', error: error?.message ?? String(error) }
  } finally {
    clearTimeout(timer)
  }
}

/** 从端点文件 + 端口扫描找到活着的游戏窗口；优先 client（双开时是新开的那个）。 */
async function discoverWindows(config) {
  const found = []
  const seen = new Set()
  let entries = []
  try {
    entries = await readdir(endpointDir())
  } catch {
    entries = []
  }

  for (const name of entries) {
    if (!name.endsWith('.json')) continue
    try {
      const raw = await readFile(join(endpointDir(), name), 'utf8')
      const meta = JSON.parse(raw)
      if (!meta?.port) continue
      seen.add(Number(meta.port))
      found.push({ port: Number(meta.port), fileRole: meta.role, pid: meta.pid, startedAt: meta.process_started_at })
    } catch {
      // 半截文件/坏 JSON：跳过，靠端口扫描兜底。
    }
  }

  for (let port = 8090; port <= 8099; port += 1) {
    if (!seen.has(port)) found.push({ port, fileRole: undefined })
  }

  const alive = []
  for (const candidate of found) {
    const base = `http://127.0.0.1:${candidate.port}`
    const health = await httpJson(base, '/health', { timeoutMs: Math.min(config.timeoutMs, 1500) })
    if (!health.ok || !health.json || health.json.service !== 'pwa') continue
    alive.push({
      base,
      port: candidate.port,
      role: health.json.role ?? candidate.fileRole ?? 'unknown',
      pid: health.json.pid ?? candidate.pid,
      screen: health.json.screen ?? 'UNKNOWN',
      canAct: health.json.can_act === true,
      startedAt: health.json.process_started_at ?? candidate.startedAt ?? '',
    })
  }

  return alive
}

function pickWindow(windows, preferred) {
  if (!windows.length) return null
  if (preferred) {
    const trimmed = preferred.replace(/\/+$/, '')
    const exact = windows.find((w) => w.base === trimmed)
    if (exact) return exact
  }
  const rank = { client: 0, singleplayer: 1, host: 2, unknown: 3 }
  return [...windows].sort((a, b) => {
    const byRole = (rank[a.role] ?? 9) - (rank[b.role] ?? 9)
    if (byRole !== 0) return byRole
    return String(b.startedAt).localeCompare(String(a.startedAt))
  })[0]
}

// ---------------------------------------------------------------- 压缩

function compactPowers(powers) {
  if (!Array.isArray(powers)) return []
  return powers.slice(0, 12).map((power) => ({
    id: power.id,
    amount: power.amount,
    debuff: power.debuff === true,
  }))
}

function compactIntent(intent) {
  const out = { kind: intent.kind }
  if (intent.damage != null) out.damage = intent.damage
  if (intent.hits != null) out.hits = intent.hits
  if (intent.total != null) out.total = intent.total
  if (intent.cards != null) out.cards = intent.cards
  if (intent.label && !/^\d+$/.test(String(intent.label))) out.label = String(intent.label).slice(0, 30)
  return out
}

function compactCard(card) {
  const out = {
    i: card.i,
    ref: card.ref ?? card.reference,
    id: card.id ?? card.card_id,
    name: card.name,
    cost: card.cost,
  }
  if (card.upgraded) out.upgraded = true
  if (card.playable !== null && card.playable !== undefined) out.playable = card.playable
  if (card.why) out.why = card.why
  if (card.target_type && card.target_type !== 'Self' && card.target_type !== 'None') out.target_type = card.target_type
  if (Array.isArray(card.targets) && card.targets.length) out.targets = card.targets
  if (card.text) out.text = String(card.text).slice(0, 120)
  return out
}

function compactCombat(combat) {
  if (!combat) return null
  const me = combat.me ?? {}
  return {
    turn: combat.turn,
    side: combat.side,
    my_turn: combat.my_turn,
    turn_phase: combat.turn_phase,
    in_progress: combat.in_progress,
    player_debuffs: (combat.player_debuffs ?? []).map((debuff) => `${debuff.name}×${debuff.amount}`),
    ending_turn: combat.ending_turn,
    actions_disabled: combat.actions_disabled,
    me: {
      character: me.character,
      hp: me.hp,
      max_hp: me.max_hp,
      block: me.block,
      energy: me.energy,
      max_energy: me.max_energy,
      stars: me.stars,
      powers: compactPowers(me.powers),
    },
    hand: (combat.hand ?? []).map(compactCard),
    enemies: (combat.enemies ?? []).map((enemy) => ({
      i: enemy.i,
      id: enemy.id,
      combat_id: enemy.combat_id,
      name: enemy.name,
      hp: enemy.hp,
      max_hp: enemy.max_hp,
      block: enemy.block,
      alive: enemy.alive,
      intents: (enemy.intents ?? []).map(compactIntent),
      powers: compactPowers(enemy.powers),
    })),
    players: (combat.players ?? []).map((player) => ({
      i: player.i,
      is_me: player.is_me,
      character: player.character,
      hp: player.hp,
      max_hp: player.max_hp,
      block: player.block,
      energy: player.energy,
      alive: player.alive,
      powers: compactPowers(player.powers),
    })),
    piles: combat.piles,
  }
}

function deckSummary(cards) {
  if (!Array.isArray(cards)) return []
  const counts = new Map()
  for (const card of cards) {
    const key = `${card.id}${card.upgraded ? '+' : ''}`
    counts.set(key, (counts.get(key) ?? 0) + 1)
  }
  return [...counts.entries()].map(([id, count]) => (count > 1 ? `${id}×${count}` : id))
}

function compactRun(run) {
  if (!run) return null
  return {
    character: run.character,
    floor: run.floor,
    act: run.act,
    hp: run.hp,
    max_hp: run.max_hp,
    gold: run.gold,
    deck_size: Array.isArray(run.deck) ? run.deck.length : undefined,
    deck: deckSummary(run.deck),
    relics: (run.relics ?? []).map((relic) => (relic.count ? `${relic.name}×${relic.count}` : relic.name)),
    potions: (run.potions ?? []).filter((potion) => !potion.empty).map((potion) => ({ slot: potion.slot, name: potion.name, usable: potion.usable })),
    players: (run.players ?? []).map((player) => ({
      i: player.i,
      is_me: player.is_me,
      character: player.character,
      hp: player.hp,
      max_hp: player.max_hp,
      gold: player.gold,
      alive: player.alive,
    })),
  }
}

function compactScene(key, value) {
  if (!value) return null
  if (key === 'event' && Array.isArray(value.options)) {
    return {
      id: value.id,
      title: value.title,
      finished: value.finished,
      options: value.options.map((option) => ({
        i: option.i,
        title: option.title,
        locked: option.locked,
        relic: option.relic_name ?? undefined,
        text: String(option.description ?? '').slice(0, 100),
      })),
    }
  }
  if (key === 'reward') {
    if (value.card_choice) {
      return {
        card_choice: true,
        cards: (value.cards ?? []).map(compactCard),
        skip_options: (value.skip_options ?? []).map((option) => option.label),
      }
    }
    return {
      card_choice: false,
      can_proceed: value.can_proceed === true,
      rewards: (value.rewards ?? []).map((reward) => ({ i: reward.i, kind: reward.kind, text: reward.description, claimable: reward.claimable })),
    }
  }
  if (key === 'map') {
    return {
      current: value.current,
      open: value.open,
      options: (value.options ?? []).map((option) => ({ i: option.i, type: option.node_type, coord: option.coord })),
      votes: (value.votes ?? []).filter((vote) => vote.coord || vote.is_me),
    }
  }
  if (key === 'selection') {
    return {
      prompt: value.prompt,
      confirm_available: value.confirm_available,
      cards: (value.cards ?? []).map(compactCard),
    }
  }
  return value
}

function compactState(raw) {
  if (!raw || raw.error) return raw
  const state = {
    seq: raw.seq,
    screen: raw.screen,
    actions: raw.actions ?? [],
    in_combat: raw.combat ? ((raw.combat.enemies ?? []).length > 0 && raw.combat.in_progress === true) : raw.in_combat === true,
    turn: raw.turn ?? 0,
  }
  if (raw.combat) state.combat = compactCombat(raw.combat)
  if (raw.run) state.run = compactRun(raw.run)
  for (const key of SCENE_KEYS) {
    const scene = compactScene(key, raw[key])
    if (scene) state[key] = scene
  }
  return state
}

function stableKey(state) {
  const clone = { ...state }
  delete clone.seq
  return JSON.stringify(clone)
}

// ---------------------------------------------------------------- 工具描述

const noArgs = { type: 'object', properties: {}, additionalProperties: false }

const ACT_SCHEMA = {
  type: 'object',
  properties: {
    action: { type: 'string', description: '动作名，见 state.actions，例如 play_card / end_turn / choose_map_node' },
    card_ref: { type: 'string', description: '手牌 ref（如 c3），play_card / select_card 用' },
    card_index: { type: 'integer', description: '手牌下标；会随出牌重排，优先用 card_ref' },
    target_index: { type: 'integer', description: '目标下标：敌人是 combat.enemies[i]，队友是 combat.players[i]' },
    target_id: { type: 'integer', description: '稳定目标 id（combat.enemies[].combat_id），敌人重排后仍然有效；优先用它' },
    again: { type: 'boolean', description: '可重复选择的事件选项（was_chosen=true）再选一次时必须为 true' },
    index: { type: 'integer', description: '选项下标：地图/事件/奖励/宝箱/营火/商店/选角' },
    slot: { type: 'integer', description: '药水槽下标：use_potion / discard_potion' },
    path: { type: 'string', description: 'inspect_tree / click_node 的节点路径' },
    mode: { type: 'string', description: 'click_node 的点击方式：force / debug / released' },
    depth: { type: 'integer', description: 'inspect_tree 的深度' },
  },
  required: ['action'],
  additionalProperties: false,
}

// ---------------------------------------------------------------- World

function defaults() {
  return {
    enabled: false,
    baseUrl: '',
    pollSeconds: 4,
    watch: true,
    delta: true,
    timeoutMs: 20000,
    maxChars: 6000,
    auto: true,
    autoEndTurn: true,
    autoSteps: 6,
    waitTurnSeconds: 8,
    coopGameRoot: '',
    coopBrokerPort: 33772,
    coopSession: 'pwa',
    coopWithClient: true,
  }
}

const DEFAULT_GAME_ROOT = 'D:\\Programs\\Steam\\steamapps\\common\\Slay the Spire 2'
const COOP_HOST_PORT = 8091
const COOP_CLIENT_PORT = 8092

/** 一键联机：拉起 host 窗口（自己开中继）和 client 窗口，等两边 Mod 就绪。 */
function createCoopLauncher(cfg) {
  const state = { phase: 'stopped', hostPid: null, clientPid: null, detail: '', startedAt: null }

  const gameRoot = () => cfg().coopGameRoot || DEFAULT_GAME_ROOT
  const port = () => Number(cfg().coopBrokerPort) || 33772
  const session = () => cfg().coopSession || 'pwa'

  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms))

  async function health(portNumber) {
    try {
      const response = await fetch(`http://127.0.0.1:${portNumber}/health`, { signal: AbortSignal.timeout(2500) })
      return response.ok ? await response.json() : null
    } catch {
      return null
    }
  }

  async function waitFor(portNumber, role, timeoutMs) {
    const deadline = Date.now() + timeoutMs
    while (Date.now() < deadline) {
      const body = await health(portNumber)
      if (body && (!role || body.role === role)) return body
      await sleep(1000)
    }
    return null
  }

  function launch(args, extraEnv) {
    const child = spawn(join(gameRoot(), 'SlayTheSpire2.exe'), args, {
      cwd: gameRoot(),
      env: {
        ...process.env,
        PWA_COOP_ENDPOINT: `127.0.0.1:${port()}`,
        PWA_COOP_SESSION: session(),
        ...extraEnv,
      },
      detached: true,
      stdio: 'ignore',
    })
    child.unref()
    return child.pid ?? null
  }

  function kill(pid) {
    if (!pid) return
    try {
      spawn('taskkill', ['/PID', String(pid), '/T', '/F'], { stdio: 'ignore' }).unref()
    } catch {
      // 进程可能已经自己退了
    }
  }

  async function start(options = {}) {
    if (state.phase === 'running') return snapshot()
    const withClient = options.withClient ?? cfg().coopWithClient !== false
    state.phase = 'starting'
    state.detail = `拉起 host（中继 127.0.0.1:${port()}）…`

    const existingHost = await health(COOP_HOST_PORT)
    if (existingHost?.role === 'host') {
      state.detail = '已有一个 host 窗口在跑，直接复用'
    } else {
      state.hostPid = launch(
        ['--force-steam', 'off', '--clientId', '1', '-fastmp', 'host_standard'],
        { PWA_COOP: 'host', PWA_COOP_INDEX: '0' },
      )
      const host = await waitFor(COOP_HOST_PORT, 'host', 90000)
      if (!host) {
        state.phase = 'error'
        state.detail = 'host 窗口没起来（看 %LOCALAPPDATA%\\PwA\\coop-<pid>.log）'
        return snapshot()
      }
    }

    if (withClient) {
      state.detail = '拉起 client 窗口…'
      const existingClient = await health(COOP_CLIENT_PORT)
      if (existingClient?.role === 'client') {
        state.detail = 'client 窗口已在跑，直接复用'
      } else {
        state.clientPid = launch(
          ['--windowed', '--force-steam', 'off', '--clientId', '1001', '-fastmp', 'join'],
          { PWA_COOP: 'client', PWA_COOP_INDEX: '1' },
        )
        const client = await waitFor(COOP_CLIENT_PORT, 'client', 90000)
        if (!client) {
          state.phase = 'error'
          state.detail = 'client 窗口没起来（看 %LOCALAPPDATA%\\PwA\\coop-<pid>.log）'
          return snapshot()
        }
      }
    }

    state.phase = 'running'
    state.startedAt = Date.now()
    state.detail = withClient ? '两个窗口都就绪：你玩 8091，汐连 8092' : 'host 就绪（8091）'
    await sleep(600)
    return snapshot()
  }

  /** 当前活着的联机窗口 pid：8021/8022 两个角色端口上 /health 报的 pid 最可靠。 */
  async function liveCoopPids() {
    const pids = []
    for (const portNumber of [COOP_HOST_PORT, COOP_CLIENT_PORT]) {
      const body = await health(portNumber)
      if (body && (body.role === 'host' || body.role === 'client') && Number(body.pid) > 0) {
        pids.push(Number(body.pid))
      }
    }
    return pids
  }

  async function stop() {
    const pids = new Set()
    if (state.hostPid) pids.add(state.hostPid)
    if (state.clientPid) pids.add(state.clientPid)
    for (const pid of await liveCoopPids()) pids.add(pid)
    for (const pid of pids) kill(pid)
    state.hostPid = null
    state.clientPid = null
    state.phase = 'stopped'
    state.detail = pids.size > 0 ? `已关闭 ${pids.size} 个窗口` : '没有由这个按钮拉起的窗口'
    return snapshot()
  }

  function snapshot() {
    return {
      phase: state.phase,
      hostPid: state.hostPid,
      clientPid: state.clientPid,
      ports: { host: COOP_HOST_PORT, client: COOP_CLIENT_PORT, broker: port() },
      session: session(),
      detail: state.detail,
      stoppable: Boolean(state.hostPid || state.clientPid),
    }
  }

  async function status() {
    const host = await health(COOP_HOST_PORT)
    const client = await health(COOP_CLIENT_PORT)
    const discovered = await liveCoopPids()
    if (host || client) {
      state.phase = host && (!cfg().coopWithClient || client) ? 'running' : 'partial'
    }
    const snapshotValue = snapshot()
    return {
      ...snapshotValue,
      stoppable: snapshotValue.stoppable || discovered.length > 0,
      host: host ? { role: host.role, screen: host.screen } : null,
      client: client ? { role: client.role, screen: client.screen } : null,
    }
  }

  return { start, stop, status, snapshot }
}

function create(ctx) {
  const cfg = () => ctx.cfg
  const coop = createCoopLauncher(cfg)
  let host = null
  let timer = null
  let lastSeq = 0
  let lastAttentionKey = ''
  let lastAttentionAt = 0
  let lastState = null
  let lastKey = ''
  let lastBase = ''
  let lastHealth = null
  let lastOkAt = 0
  let inflight = false

  function clip(text) {
    const limit = Math.max(1000, Number(cfg().maxChars) || 6000)
    if (typeof text !== 'string') text = String(text)
    return text.length <= limit ? text : `${text.slice(0, limit)}\n…(truncated, ${text.length} chars)`
  }

  async function resolveBase(force = false) {
    const now = Date.now()
    if (!force && lastBase && now - lastOkAt < 5000) return lastBase
    const windows = await discoverWindows({ timeoutMs: cfg().timeoutMs })
    const picked = pickWindow(windows, cfg().baseUrl)
    lastBase = picked?.base ?? ''
    return lastBase
  }

  async function request(path, options = {}) {
    const base = await resolveBase()
    if (!base) {
      throw new Error('没有找到运行中的 PwA 游戏窗口：确认游戏已启动、Mod 已加载（/health 可访问）')
    }
    const timeoutMs = Number(options.timeoutMs) || Number(cfg().timeoutMs) || 20000
    const result = await httpJson(base, path, { ...options, timeoutMs })
    if (result.status === 0) {
      const aborted = /abort|timeout/i.test(String(result.error ?? ''))
      if (!aborted) lastBase = ''
      const failure = new Error(aborted ? `请求超时：${base}${path}` : `连不上 ${base}${path}：${result.error ?? 'unknown'}`)
      failure.status = 0
      failure.payload = {
        error: {
          code: aborted ? 'timeout' : 'unreachable',
          message: failure.message,
          retryable: true,
          details: { may_have_executed: path === '/act' && aborted },
        },
      }
      throw failure
    }
    lastOkAt = Date.now()
    if (!result.ok) {
      const error = result.json?.error
      const message = error ? `${error.code}: ${error.message}` : `HTTP ${result.status}`
      const failure = new Error(message)
      failure.payload = result.json
      failure.status = result.status
      throw failure
    }
    return result.json
  }

  async function fetchState() {
    const raw = await request('/state')
    const compact = compactState(raw)
    return compact
  }

  async function fetchHealth() {
    const base = await resolveBase(true)
    if (!base) return null
    const result = await httpJson(base, '/health', { timeoutMs: Math.min(cfg().timeoutMs, 3000) })
    lastHealth = result.ok ? result.json : null
    if (result.ok) lastOkAt = Date.now()
    const eventSeq = Number(lastHealth?.events_seq)
    if (Number.isFinite(eventSeq) && eventSeq >= 0) {
      // 游戏重启后事件序号归零：回退超过 5 就当新进程，重置游标。
      lastSeq = eventSeq < lastSeq - 5 ? eventSeq : Math.max(lastSeq, eventSeq)
    }
    return lastHealth
  }

  function viewPayload(state, forceFull) {
    const key = stableKey(state)
    const unchanged = key === lastKey
    lastKey = key
    lastState = state

    if (!forceFull && cfg().delta && unchanged) {
      // 没变化时也给「现在能做什么」和当前场景，否则模型会拿到一份空视图。
      const summary = {
        seq: state.seq,
        unchanged: true,
        screen: state.screen,
        turn: state.turn,
        actions: state.actions,
        hand_refs: (state.combat?.hand ?? []).map((card) => card.ref),
      }
      for (const sceneKey of SCENE_KEYS) {
        if (state[sceneKey]) summary[sceneKey] = state[sceneKey]
      }
      return summary
    }

    return state
  }

  async function actOnce(args, options = {}) {
    // 动作期间暂停事件轮询，少一份并发、也少一次「读到的还是旧画面」的机会。
    acting += 1
    try {
      return await actOnceInner(args, options)
    } finally {
      acting -= 1
    }
  }

  // 同一动作在「未落定」状态下的重复提交防护：pending / 需要选牌时记住 5 秒。
  let lastUnsettled = { key: '', at: 0 }

  /** 把敌人下标换成稳定 id：敌人死亡后列表会重排，id 不会。 */
  function withStableTarget(state, args) {
    if (args.target_id != null || args.target_index == null) return args
    const card = args.card_ref
      ? (state?.combat?.hand ?? []).find((item) => item.ref === args.card_ref)
      : null
    const enemyTarget = card ? card.target_type === 'AnyEnemy' : args.action === 'use_potion'
    if (!enemyTarget) return args
    const enemy = state?.combat?.enemies?.[args.target_index]
    if (!enemy || enemy.combat_id == null) return args
    return { ...args, target_id: enemy.combat_id }
  }

  async function actOnceInner(rawArgs, options = {}) {
    const args = withStableTarget(lastState ?? null, rawArgs)
    const dedupeKey = JSON.stringify([args.action, args.card_ref ?? args.card_index ?? null, args.target_index ?? null, args.target_id ?? null, args.index ?? null, args.slot ?? null])
    if (lastUnsettled.key === dedupeKey && Date.now() - lastUnsettled.at < 5000) {
      return {
        ok: false,
        action: args.action,
        code: 'duplicate',
        message: '上一次同名动作还没落定（pending/选牌中），不要重复提交；先 sts2_view 看结果',
        hint: 're-read state and decide again',
      }
    }
    const withState = options.withState !== false
    const action = String(args.action ?? '')
    if (!action) throw new Error('缺少 action')
    const state = await fetchState()
    const checks = preflight(state, action, args)
    if (checks.error) return checks

    const body = { action, ...pickActionArgs(args) }
    try {
      // 动作请求单独放宽到 25s：宁可多等，也别在动作已生效时被客户端 abort。
      const response = await request('/act', { method: 'POST', body, timeoutMs: 25000 })
      // Mod 在回执前已经等过局面稳定，直接用这份 state，不再多发一次 /state。
      let fresh = response.state ? compactState(response.state) : null
      if (fresh && withState && action === 'end_turn' && options.waitTurn !== false) {
        const nextTurn = await waitForMyTurn(state?.combat?.turn ?? null)
        if (nextTurn) fresh = nextTurn
      }
      if (fresh && withState) {
        lastKey = stableKey(fresh)
        lastState = fresh
      }
      const needsSelection = response.extra?.needs_selection === true || response.state?.screen === 'CARD_SELECTION'
      if (response.status === 'pending' || needsSelection) {
        lastUnsettled = { key: dedupeKey, at: Date.now() }
      } else {
        lastUnsettled = { key: '', at: 0 }
      }
      return { ok: true, action, status: response.status, extra: response.extra ?? undefined, state: withState ? fresh : undefined }
    } catch (error) {
      const payload = error.payload?.error
      if (payload) {
        const mayHaveExecuted = payload.details?.may_have_executed === true
        return {
          ok: false,
          action,
          code: payload.code,
          message: payload.message,
          retryable: payload.retryable === true,
          may_have_executed: mayHaveExecuted || undefined,
          hint: mayHaveExecuted
            ? '动作可能已经生效：先 sts2_view 复查，不要直接重复提交'
            : hintForCode(payload.code),
        }
      }
      throw error
    }
  }

  /** 能不做决策就往前走的一步；返回 null 表示该模型决定了。 */
  function pickAutoStep(state, allowEndTurn = false) {
    if (!state) return null
    const screen = state.screen
    if (screen === 'REWARD' && state.reward && state.reward.card_choice !== true) {
      const claimable = (state.reward.rewards ?? []).filter((item) => item.claimable)
      const free = claimable.find((item) => item.kind === 'gold' || item.kind === 'potion')
      if (free) return { action: 'claim_reward', index: free.i }
      return null
    }
    if (screen === 'EVENT' && state.event?.finished === true) return { action: 'proceed' }
    if (screen === 'CHEST' && state.chest && state.chest.opened !== true) return { action: 'open_chest' }
    if (screen === 'MAP' && (state.map?.options ?? []).length === 1) {
      return { action: 'choose_map_node', index: state.map.options[0].i }
    }
    // 弹窗默认不自动点：刚才自动确认一个「模组加载」弹窗直接把游戏退掉了。
    // 需要时把 worlds.pwa.autoModal 打开。
    if (screen === 'MODAL' && cfg().autoModal === true) {
      const enabled = (state.modal?.buttons ?? []).filter((button) => button.enabled !== false)
      if (enabled.length === 1) return { action: 'confirm_modal' }
      return null
    }
    if (screen === 'MAIN_MENU' && (state.actions ?? []).includes('continue_run')) return { action: 'continue_run' }
    if (allowEndTurn
      && cfg().autoEndTurn !== false
      && screen === 'COMBAT'
      && state.combat?.my_turn
      && state.combat?.turn_phase === 'player_turn'
      && (state.actions ?? []).includes('end_turn')) {
      const hand = state.combat.hand ?? []
      const usablePotion = (state.combat.me?.potions ?? []).some((potion) => potion.usable && !potion.empty)
      // 还有药水能用就先别替模型翻页：0 能量时喝药是合法决策。
      if (!usablePotion && hand.length > 0 && !hand.some((card) => card.playable)) {
        return { action: 'end_turn', reason: `no playable card (energy=${state.combat.me?.energy}, hand=${hand.length})` }
      }
    }
    return null
  }

  /** 连续做几件不需要决策的事（收钱/收药水/继续/开箱/唯一地图/唯一弹窗/无牌可出结束回合）。 */
  async function autoAdvance(state, options = {}) {
    const log = []
    const steps = Math.max(0, Math.min(8, Number(cfg().autoSteps ?? 6)))
    if (cfg().auto === false || steps === 0) return { state, log }
    const deadline = Date.now() + Math.min(6000, steps * 1500)
    const done = new Set()
    for (let i = 0; i < steps && Date.now() < deadline; i += 1) {
      const step = pickAutoStep(state, options.allowEndTurn === true)
      if (!step) break

      // 同一个动作在一次自动推进里只做一次；做完局面没动就停，避免重复提交。
      const stepKey = `${step.action}:${step.index ?? ''}`
      if (done.has(stepKey)) break
      done.add(stepKey)
      const beforeSeq = state?.seq
      const beforeScreen = state?.screen

      // 结束回合是不可逆动作：执行前重新读一次，确认仍然「轮到我 + 没有可打出的牌」。
      if (step.action === 'end_turn') {
        const fresh = await fetchState()
        const hand = fresh.combat?.hand ?? []
        const stillSafe = fresh.screen === 'COMBAT'
          && fresh.combat?.my_turn === true
          && fresh.combat?.turn_phase === 'player_turn'
          && hand.length > 0
          && !hand.some((card) => card.playable)
        if (!stillSafe) break
        log.push({ checked: 'end_turn_precheck', energy: fresh.combat.me?.energy, hand: hand.length })
      }

      const result = await actOnceInner(step, { withState: false })
      log.push({ action: step.action, index: step.index ?? null, screen: beforeScreen, ok: result.ok !== false, code: result.code })
      if (result.ok === false) break
      try {
        // 自动结束回合后要等回到自己的回合，否则回执又停在「敌方回合 / 手牌 0」。
        const nextTurn = step.action === 'end_turn' ? await waitForMyTurn(state?.combat?.turn ?? null) : null
        state = nextTurn ?? (await fetchState())
        if (state?.seq === beforeSeq && state?.screen === beforeScreen) break
      } catch {
        break
      }
    }
    return { state, log }
  }

  /**
   * 回合开始时游戏可能先回能量、后抽牌（能量 3 但手牌 0）。这种半截状态不返回给模型，
   * 最多等 1.2s 到抽牌出现；不是每个回合都等，只在「轮到我但手牌为空」时才走这条路。
   */
  async function handReady(state) {
    if (!state || state.screen !== 'COMBAT') return state
    if (state.combat?.my_turn !== true) return state
    if ((state.combat.hand ?? []).length > 0) return state
    const draw = state.combat.piles?.draw ?? 0
    if (draw <= 0) return state
    const deadline = Date.now() + 1200
    while (Date.now() < deadline) {
      await sleep(120)
      let next
      try { next = await fetchState() } catch { return state }
      if (next.screen !== 'COMBAT') return next
      if ((next.combat?.hand ?? []).length > 0) return next
    }
    return state
  }

  /** 结束回合后等回到自己的回合（最多 waitTurnSeconds 秒），否则回执里又是空的敌方回合。 */
  async function waitForMyTurn(minTurn = null) {
    const seconds = Math.max(0, Math.min(20, Number(cfg().waitTurnSeconds ?? 8)))
    if (!seconds) return null
    const deadline = Date.now() + seconds * 1000
    while (Date.now() < deadline) {
      await sleep(400)
      try {
        const state = await fetchState()
        if (state.screen !== 'COMBAT') return state
        const turn = state.combat?.turn ?? 0
        const newTurn = minTurn == null || turn > minTurn
        // 只看「轮到我」不够：换回合的过渡里 hand 还是上一回合的旧牌、能量还是 0。
        if (state.combat?.turn_phase === 'player_turn'
          && newTurn
          && (state.combat.hand ?? []).length > 0) {
          return state
        }
      } catch {
        return null
      }
    }
    return null
  }

  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms))

  /** 保证回执永远是合法 JSON：超长就按 牌组 → run → 敌人能力 → 只留摘要 的顺序瘦身。 */
  function fit(payload) {
    const limit = Math.max(1000, Number(cfg().maxChars) || 6000)
    const stringify = () => JSON.stringify(payload)
    let text = stringify()
    if (text.length <= limit) return text

    const state = payload?.state
    if (state?.run?.deck) {
      delete state.run.deck
      text = stringify()
      if (text.length <= limit) return text
    }
    if (state?.run) {
      delete state.run
      text = stringify()
      if (text.length <= limit) return text
    }
    for (const enemy of state?.combat?.enemies ?? []) {
      delete enemy.powers
    }
    text = stringify()
    if (text.length <= limit) return text

    // 保留动作结果本身（ok/status/extra/auto），只把 state 压成摘要。
    const head = { ...payload }
    delete head.state
    const summary = {
      ...head,
      truncated: true,
      note: `回执超过 ${limit} 字符：state 已压成摘要（动作结果完整保留）；需要完整局面请 sts2_view full=true`,
      screen: state?.screen ?? payload?.screen,
      actions: state?.actions ?? payload?.actions,
      turn: state?.turn ?? payload?.turn,
      hand_refs: (state?.combat?.hand ?? []).map((card) => card.ref),
    }
    return JSON.stringify(summary)
  }

  function pickActionArgs(args) {
    const out = {}
    for (const key of ['card_ref', 'card_index', 'target_index', 'target_id', 'index', 'slot', 'path', 'mode', 'depth', 'again']) {
      if (args[key] !== undefined && args[key] !== null) out[key] = args[key]
    }
    return out
  }

  function preflight(state, action, args) {
    if (!state) return { error: '没有状态' }
    const actions = state.actions ?? []
    if (!ALWAYS_ALLOWED.has(action) && !actions.includes(action)) {
      return {
        ok: false,
        blocked: true,
        action,
        code: 'action_not_available',
        message: `现在不能做 ${action}`,
        state: { screen: state.screen, turn: state.turn, actions },
      }
    }

    if (action === 'play_card') {
      const hand = state.combat?.hand ?? []
      let card = null
      if (args.card_ref) card = hand.find((item) => item.ref === args.card_ref)
      else if (args.card_index != null) card = hand[args.card_index]
      if (!card) {
        return { ok: false, action, code: 'stale_ref', message: '这张牌不在手牌里，重新 view 拿新 ref', hint: 're-read state' }
      }
      if (card.playable === false) {
        return { ok: false, action, code: 'unplayable', message: `${card.name} 现在打不出去（${card.why ?? 'unknown'}）`, state: { hand } }
      }
      const needsTarget = card.target_type === 'AnyEnemy' || card.target_type === 'AnyAlly'
      const targets = card.targets ?? []
      if (needsTarget && args.target_index == null) {
        if (targets.length === 1) return { autoTarget: targets[0], card }
        return { ok: false, action, code: 'needs_target', message: `${card.name} 需要 target_index`, state: { targets } }
      }
      if (needsTarget && args.target_index != null && !targets.includes(args.target_index)) {
        return { ok: false, action, code: 'target_gone', message: '这个目标已经不能选了', state: { targets } }
      }
      return { card }
    }

    if (['choose_map_node', 'choose_event_option', 'claim_reward', 'choose_reward_card', 'choose_treasure_relic', 'choose_rest_option', 'choose_character', 'buy_card', 'buy_relic', 'buy_potion'].includes(action)) {
      if (args.index == null) return { ok: false, action, code: 'invalid_request', message: `${action} 需要 index` }
    }

    return {}
  }

  function hintForCode(code) {
    switch (code) {
      case 'blocked': return '还没轮到你；等一会儿或 sts2_wait'
      case 'needs_target': return '从 state 里的 targets 选一个 target_index'
      case 'stale_ref': return '重新 sts2_view 拿新 ref'
      case 'target_gone': return '目标已死/重排，重新 sts2_view'
      case 'busy': return '上一个动作还没跑完，稍后重试'
      case 'modal_open': return '先处理弹窗（confirm_modal / dismiss_modal）'
      default: return undefined
    }
  }

  async function waitForChange(args) {
    const seconds = Math.min(Math.max(Number(args.seconds) || 8, 1), 20)
    const until = String(args.until ?? 'actionable')
    const deadline = Date.now() + seconds * 1000
    const start = lastState ?? (await fetchState())
    let state = start

    while (Date.now() < deadline) {
      await new Promise((resolve) => setTimeout(resolve, 500))
      try {
        state = await fetchState()
      } catch {
        continue
      }
      const changed = state.seq !== start.seq || state.screen !== start.screen || state.turn !== start.turn
      const actionable = (state.actions ?? []).length > 0
      if (until === 'any' && changed) return { waited: true, state: viewPayload(state, true) }
      if (until === 'choice' && ['CARD_SELECTION', 'REWARD', 'EVENT', 'CHEST', 'REST', 'SHOP'].includes(state.screen) && actionable) {
        return { waited: true, state: viewPayload(state, true) }
      }
      if (until === 'actionable' && changed && actionable) return { waited: true, state: viewPayload(state, true) }
      if (until === 'actionable' && actionable && state.combat?.my_turn === true && state.turn !== start.turn) {
        return { waited: true, state: viewPayload(state, true) }
      }
    }

    return { waited: false, timeout: seconds, state: viewPayload(state, true) }
  }

  let acting = 0

  async function pollEvents() {
    if (acting > 0) return
    if (!host || inflight) return
    inflight = true
    try {
      const base = await resolveBase()
      if (!base) return
      const result = await httpJson(base, `/events?since=${lastSeq}`, { timeoutMs: Math.min(cfg().timeoutMs, 4000) })
      if (!result.ok || !result.json) return
      const events = result.json.events ?? []
      if (process.env.PWA_DEBUG_POLL === '1') console.log(`[poll] base=${base} since=${lastSeq} events=${events.length} types=${events.map((event) => event.type).join(',')}`)
      const reported = Number(result.json.last_seq) ?? 0
      if (reported < lastSeq) {
        // 游戏重启：事件序号归零，重置游标，避免永远收不到新事件。
        if (process.env.PWA_DEBUG_POLL === '1') console.log(`[poll] seq 回退 ${lastSeq} -> ${reported}，按新进程处理`)
        lastSeq = reported
        return
      }
      const hasAttention = events.some((event) => event.type === 'attention')
      lastSeq = Math.max(lastSeq, reported)
      for (const event of events) {
        if (hasAttention && (event.type === 'turn_started' || event.type === 'player_choice_required')) {
          continue
        }
        if (event.type === 'attention') {
          const data = event.data ?? {}
          const key = `${data.kind}|${data.screen}|${data.turn}|${data.detail}`
          if (key === lastAttentionKey && Date.now() - lastAttentionAt < 6000) continue
          lastAttentionKey = key
          lastAttentionAt = Date.now()
        }
        const text = describeEvent(event)
        if (!text) continue
        await host.pushEvent(
          {
            type: `pwa.${event.type}`,
            text,
            source: 'pwa',
            meta: { seq: event.seq, screen: event.data?.screen, turn: event.data?.turn },
          },
          { trigger: 'debounce' },
        )
      }
    } catch (error) {
      if (process.env.PWA_DEBUG_POLL === '1') console.log(`[poll] 失败: ${error?.message ?? error}`)
      // 游戏没开、切场景、端口变了：下一轮再试。
    } finally {
      inflight = false
    }
  }

  const ATTENTION_TEXT = {
    my_turn: (data) => `轮到你了（${data.detail ?? '你的回合'}）：用 ${TOOL_PREFIX}view 看局面，${TOOL_PREFIX}act 出牌。`,
    selection: (data) => `需要你选牌：${data.detail ?? '选牌界面'}。用 ${TOOL_PREFIX}view 看候选，${TOOL_PREFIX}act select_card。`,
    reward_card: () => '战斗奖励：等你挑一张牌（或跳过）。',
    reward: () => '战斗结束，有奖励可以领：金币/药水/卡牌。',
    reward_proceed: () => '奖励领完了，可以继续。',
    event_option: (data) => `事件等你选选项${data.detail ? '：' + data.detail : ''}。`,
    event_proceed: () => '事件结束了，等你按继续（proceed）。',
    map: () => '地图等你选路/投票。',
    map_travel: () => '地图已经出发。',
    chest_open: () => '宝箱等你打开。',
    chest_relic: () => '宝箱等你选遗物。',
    chest_proceed: () => '宝箱拿完了，可以继续。',
    rest_option: () => '营火等你选（休息/升级/移除…）。',
    rest_proceed: () => '营火选完了，可以继续。',
    modal: (data) => `有个弹窗等你处理${data.detail ? '：' + data.detail : ''}。`,
    character_select: () => '等你选角色。',
    game_over: () => '结算页等你操作。',
    singleplayer_menu: () => null,
  }

  function describeEvent(event) {
    const data = event.data ?? {}
    if (event.type === 'attention') {
      const build = ATTENTION_TEXT[data.kind]
      return build ? build(data) : null
    }
    if (event.type === 'turn_started') {
      return `轮到你了：第 ${data.turn ?? '?'} 回合（${data.screen ?? '?'}）。用 ${TOOL_PREFIX}view 看局面，${TOOL_PREFIX}act 出牌。`
    }
    if (event.type === 'player_choice_required') {
      return `需要你做选择：${data.kind ?? '选择'}（${data.screen ?? '?'}）。用 ${TOOL_PREFIX}view 看候选。`
    }
    return null
  }

  function startPolling() {
    if (timer) clearInterval(timer)
    const seconds = Math.max(2, Math.min(30, Number(cfg().pollSeconds) || 4))
    timer = setInterval(() => { void pollEvents() }, seconds * 1000)
    void pollEvents()
  }

  const tools = [
    {
      name: `${TOOL_PREFIX}view`,
      description: '读当前局面：画面、可用动作、手牌（带 ref）、敌人意图、地图/事件/奖励等。默认只回变化部分；要全量传 full=true。',
      parameters: { type: 'object', properties: { full: { type: 'boolean', description: '强制返回完整局面' } }, additionalProperties: false },
      tags: ['read', 'snapshot'],
      barrierAfter: true,
      handler: async (args) => {
        let state = await fetchState()
        const auto = await autoAdvance(state, { allowEndTurn: true })
        state = await handReady(auto.state)
        const payload = viewPayload(state, args.full === true)
        if (auto.log.length) payload.auto = auto.log
        return fit(payload)
      },
    },
    {
      name: `${TOOL_PREFIX}act`,
      description: '执行一个动作。参数照 state.actions / state 里的结构填；返回执行后的新状态（含后续可用动作）。',
      parameters: ACT_SCHEMA,
      tags: ['act', 'snapshot'],
      barrierAfter: true,
      handler: async (args) => {
        const result = await actOnce(args)
        if (result.ok !== false && result.state) {
          const auto = await autoAdvance(result.state, { allowEndTurn: false })
          result.state = await handReady(auto.state)
          if (auto.log.length) result.auto = auto.log
        }
        lastKey = ''
        return fit(result)
      },
    },
    {
      name: `${TOOL_PREFIX}plan`,
      description: '一次提交多步动作（最多 8 步），按顺序执行，遇到不可用的步骤就停下并报出是哪一步。',
      parameters: {
        type: 'object',
        properties: {
          steps: {
            type: 'array',
            description: '每步一个对象，字段同 sts2_act',
            items: { type: 'object', properties: ACT_SCHEMA.properties, required: ['action'] },
          },
        },
        required: ['steps'],
        additionalProperties: false,
      },
      tags: ['act', 'snapshot'],
      barrierAfter: true,
      handler: async (args) => {
        const steps = Array.isArray(args.steps) ? args.steps.slice(0, 8) : []
        if (!steps.length) return fit({ ok: false, message: 'steps 为空' })
        const initial = await fetchState()
        const resolved = steps.map((step) => withStableTarget(initial, step))
        const results = []
        for (const [index, step] of resolved.entries()) {
          // 每步只回状态摘要；完整局面在最后统一给一次，避免一步一份全量状态。
          const result = await actOnce(step, { withState: false })
          results.push({ i: index + 1, action: step.action, ...result })

          const needsSelection = result.extra?.needs_selection === true
            || result.state?.screen === 'CARD_SELECTION'
            || result.code === 'needs_selection'
          const pending = result.status === 'pending'
          if (result.ok === false || needsSelection || pending) {
            const reason = needsSelection ? 'needs_selection' : (pending ? 'pending' : 'failed')
            const skipped = resolved.slice(index + 1).map((later, offset) => ({
              i: index + 2 + offset,
              action: later.action,
              skipped: true,
              reason: needsSelection
                ? '前一步弹出了选牌界面，这些步骤没有提交'
                : '前一步还没落定，这些步骤没有提交',
            }))
            const state = await fetchState()
            return fit({
              ok: false,
              stopped_at: index + 1,
              reason,
              note: needsSelection
                ? '先处理 state.selection（select_card/confirm_selection），再重新提交剩下的步骤'
                : '前一步状态未定：先 sts2_view 确认，不要重复提交',
              steps: results.concat(skipped),
              state: viewPayload(state, true),
            })
          }
        }
        let state = await fetchState()
        const auto = await autoAdvance(state, { allowEndTurn: false })
        state = await handReady(auto.state)
        const payload = { ok: true, steps: results, state: viewPayload(state, true) }
        if (auto.log.length) payload.auto = auto.log
        return fit(payload)
      },
    },
    {
      name: `${TOOL_PREFIX}wait`,
      description: '等到局面变化/轮到你/出现选择点（最多 20 秒）。不要用它代替决策，只在真的没轮到或过场时用。',
      parameters: {
        type: 'object',
        properties: {
          seconds: { type: 'integer', description: '最多等几秒（1–20，默认 8）' },
          until: { type: 'string', description: 'actionable（默认）/ any（任何变化）/ choice（出现选择）' },
        },
        additionalProperties: false,
      },
      tags: ['read', 'snapshot'],
      barrierAfter: true,
      handler: async (args) => fit(await waitForChange(args)),
    },
    {
      name: `${TOOL_PREFIX}diag`,
      description: '连接诊断：找到哪些游戏窗口、各自角色/端口/画面、这个 World 连的是哪一个。',
      parameters: noArgs,
      tags: ['read'],
      barrierAfter: true,
      handler: async () => {
        const windows = await discoverWindows({ timeoutMs: Math.min(cfg().timeoutMs, 3000) })
        const picked = pickWindow(windows, cfg().baseUrl)
        const health = await fetchHealth()
        return fit({
          build: WORLD_BUILD,
          connected: Boolean(picked),
          base: picked?.base ?? null,
          role: picked?.role ?? null,
          health,
          windows,
          config: { baseUrl: cfg().baseUrl, watch: cfg().watch, delta: cfg().delta, pollSeconds: cfg().pollSeconds },
        })
      },
    },
    {
      name: `${TOOL_PREFIX}coop_start`,
      description: '一键开启本机双窗口联机：host 窗口自己开 broker 中继，client 窗口连它。已经开着就复用。',
      parameters: {
        type: 'object',
        properties: {
          withClient: { type: 'boolean', description: '是否同时拉起 client 窗口（默认按配置，true）' },
        },
        additionalProperties: false,
      },
      tags: ['write', 'act'],
      barrierAfter: true,
      handler: async (args) => fit(await coop.start({ withClient: args.withClient })),
    },
    {
      name: `${TOOL_PREFIX}coop_stop`,
      description: '关闭由 coop_start 拉起的两个游戏窗口。',
      parameters: noArgs,
      tags: ['write', 'act'],
      barrierAfter: true,
      handler: async () => fit(await coop.stop()),
    },
    {
      name: `${TOOL_PREFIX}coop_state`,
      description: '联机房间状态：窗口是否就绪、端口、角色。',
      parameters: noArgs,
      tags: ['read'],
      barrierAfter: false,
      handler: async () => fit(await coop.status()),
    },
  ]

  return {
    id: WORLD_ID,

    envPromptVars() {
      let role = '未连接'
      let base = '未连接'
      if (lastBase) {
        base = lastBase
        role = lastHealth?.role ?? 'unknown'
      }
      return { 'pwa.role': role, 'pwa.base': base }
    },

    tools() {
      return tools
    },

    webUi(language = 'zh') {
      const zh = language !== 'en'
      const coopState = coop.snapshot()
      const connected = Boolean(lastBase) && Date.now() - lastOkAt < 15000
      const role = lastHealth?.role ?? 'unknown'
      return {
        label: zh ? '杀戮尖塔 2（PwA）' : 'Slay the Spire 2 (PwA)',
        lamps: [{
          label: zh ? '游戏连接' : 'Game link',
          state: connected ? 'online' : 'error',
          hint: connected
            ? `${lastBase} · ${role} · ${lastHealth?.screen ?? '?'}`
            : (zh ? '没找到 PwA 窗口：确认游戏已启动、Mod 已加载' : 'No PwA window: start the game with the mod installed'),
        }],
        badges: connected ? [
          { label: zh ? '角色' : 'Role', value: role, tone: role === 'client' ? 'on' : 'plain' },
          { label: zh ? '画面' : 'Screen', value: lastHealth?.screen ?? '?', tone: 'plain' },
          { label: zh ? '楼层' : 'Floor', value: lastHealth?.floor ?? 0, tone: 'plain' },
        ] : [],
        promptDocs: [{
          key: 'worlds.pwa.envPrompt',
          title: zh ? 'PwA 环境提示词' : 'PwA environment prompt',
          description: zh ? '告诉模型怎么用这些工具：先看再做、ref 规则、blocked 语义。' : 'How to use the tools: observe first, ref rules, blocked semantics.',
          path: ENV_PROMPT_FILE,
          role: 'envPrompt',
          vars: [
            { name: 'pwa.role', description: zh ? '本窗口角色（client/host/singleplayer）' : 'window role' },
            { name: 'pwa.base', description: zh ? 'World 连接的地址' : 'base URL the world talks to' },
          ],
        }],
        panels: [{
          id: 'coop',
          title: zh ? '联机房间' : 'Co-op room',
          description: zh ? '一键拉起 host + client 两个窗口；汐连 client（8092）。' : 'Start host + client windows; the agent joins the client (8092).',
        }],
        invoke: async (panel, method, args) => {
          if (panel !== 'coop') throw new Error(`unknown panel: ${panel}`)
          if (method === 'start') return coop.start(args?.[0] ?? {})
          if (method === 'stop') return coop.stop()
          if (method === 'state') return coop.status()
          throw new Error(`unknown method: ${method}`)
        },
        config: [{
          id: 'world:pwa',
          owner: 'world:pwa',
          schema: {
            type: 'object',
            title: zh ? '杀戮尖塔 2（PwA）' : 'Slay the Spire 2 (PwA)',
            description: zh ? '连接哪个游戏窗口、多久轮询一次、视图是否只回变化。' : 'Which game window, poll interval, delta views.',
            properties: {
              'worlds.pwa.baseUrl': {
                type: 'string',
                title: zh ? '指定地址（留空自动发现）' : 'Base URL (empty = auto)',
                description: zh ? '例如 http://127.0.0.1:8092；留空时读端点文件并优先选 client 窗口。' : 'e.g. http://127.0.0.1:8092; empty = discover endpoints, prefer client.',
              },
              'worlds.pwa.pollSeconds': {
                type: 'integer',
                title: zh ? '事件轮询间隔（秒）' : 'Poll seconds',
                description: zh ? '2–30，默认 4。' : '2–30, default 4.',
              },
              'worlds.pwa.watch': {
                type: 'boolean',
                title: zh ? '主动提醒' : 'Proactive pings',
                description: zh ? '轮到你了/需要选择时推一条事件。' : 'Push an event when it is your turn or a choice is needed.',
              },
              'worlds.pwa.delta': {
                type: 'boolean',
                title: zh ? '只回变化' : 'Delta views',
                description: zh ? '局面没变化时 sts2_view 只回摘要。' : 'sts2_view returns a summary when nothing changed.',
              },
              'worlds.pwa.timeoutMs': {
                type: 'integer',
                title: zh ? 'HTTP 超时（毫秒）' : 'HTTP timeout (ms)',
                description: zh ? '3000–30000，默认 10000。' : '3000–30000, default 10000.',
              },
              'worlds.pwa.maxChars': {
                type: 'integer',
                title: zh ? '单次回执上限（字符）' : 'Max chars',
                description: zh ? '默认 6000。' : 'Default 6000.',
              },
              'worlds.pwa.auto': {
                type: 'boolean',
                title: zh ? '自动推进（不决策）' : 'Auto-advance',
                description: zh ? '自动收金币/药水、继续、开箱、唯一地图、唯一弹窗、无牌可出时结束回合。' : 'Claim gold/potions, continue, open chest, single-option map/modal, end turn when no playable card.',
              },
              'worlds.pwa.autoEndTurn': {
                type: 'boolean',
                title: zh ? '无牌可出时自动结束回合' : 'Auto end turn',
                description: zh ? '只在 view 里生效；act/plan 不会替你翻页。默认开。' : 'Only in view; act/plan never end the turn for you. Default on.',
              },
              'worlds.pwa.autoSteps': {
                type: 'integer',
                title: zh ? '自动推进步数上限' : 'Auto step limit',
                description: zh ? '0–8，默认 6。' : '0–8, default 6.',
              },
              'worlds.pwa.waitTurnSeconds': {
                type: 'integer',
                title: zh ? '结束回合后等新回合（秒）' : 'Wait for next turn (s)',
                description: zh ? '0–20，默认 8；0 = 立即返回当前状态。' : '0–20, default 8; 0 = return immediately.',
              },
              'worlds.pwa.coopGameRoot': {
                type: 'string',
                title: zh ? '游戏安装目录' : 'Game install dir',
                description: zh ? '默认 D:\\Programs\\Steam\\steamapps\\common\\Slay the Spire 2。' : 'Default Steam path.',
              },
              'worlds.pwa.coopBrokerPort': {
                type: 'integer',
                title: zh ? 'broker 端口' : 'Broker port',
                description: zh ? '默认 33772，两个窗口必须一致。' : 'Default 33772.',
              },
              'worlds.pwa.coopSession': {
                type: 'string',
                title: zh ? '会话 id' : 'Session id',
                description: zh ? '默认 pwa。' : 'Default pwa.',
              },
              'worlds.pwa.coopWithClient': {
                type: 'boolean',
                title: zh ? '同时拉起 client 窗口' : 'Also start client window',
                description: zh ? '默认开；只开 host 时关掉。' : 'Default on.',
              },
            },
          },
        }],
      }
    },

    async start(worldHost) {
      host = worldHost
      lastSeq = 0
      lastKey = ''
      try { host?.log?.(`[pwa] world build ${WORLD_BUILD}`) } catch { }
      await fetchHealth()
      try {
        lastState = await fetchState()
      } catch {
        // 游戏没起来也能先挂载；工具调用时会再试。
      }
      if (cfg().watch) startPolling()
    },

    async stop() {
      if (timer) clearInterval(timer)
      timer = null
      host = null
    },
  }
}

export default {
  id: WORLD_ID,
  label: '杀戮尖塔 2（PwA）',
  defaults,
  create,
}
