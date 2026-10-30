/**
 * 联机同步监视（只读，不发动作）：每 2 秒对比 host/client 的共享状态，漂移写日志。
 * 用法：node tools/coop-monitor.mjs [--minutes 30] [--host ...] [--client ...]
 */
const args = process.argv.slice(2)
const arg = (name, fallback) => {
  const i = args.indexOf(name)
  return i >= 0 && args[i + 1] ? args[i + 1] : fallback
}
const HOST = arg('--host', 'http://127.0.0.1:8091')
const CLIENT = arg('--client', 'http://127.0.0.1:8092')
const MINUTES = Number(arg('--minutes', '30'))
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

async function getState(base) {
  try {
    const response = await fetch(`${base}/state`, { signal: AbortSignal.timeout(4000) })
    return response.ok ? await response.json() : null
  } catch {
    return null
  }
}

function shared(state) {
  if (!state) return null
  const combat = state.combat ?? {}
  return {
    screen: state.screen,
    floor: state.run?.floor ?? null,
    act: state.run?.act ?? null,
    turn: combat.turn ?? null,
    side: combat.side ?? null,
    enemies: (combat.enemies ?? [])
      .map((enemy) => `${enemy.combat_id}:${enemy.name}:${enemy.hp}:${enemy.block ?? 0}`)
      .sort()
      .join('|'),
    map: (state.map?.options ?? []).map((option) => option.coord).sort().join(','),
    event: state.event?.id ?? null,
    chest: state.chest ? `${state.chest.opened}:${(state.chest.relics ?? []).length}` : null,
    shop: state.shop ? `${(state.shop.cards ?? []).length}` : null,
    players: state.run?.players?.length ?? null,
  }
}

const deadline = Date.now() + MINUTES * 60 * 1000
let lastSignature = ''
let driftCount = 0

while (Date.now() < deadline) {
  const [host, client] = await Promise.all([getState(HOST), getState(CLIENT)])
  if (!host || !client) {
    console.log(`${new Date().toISOString()} 窗口不可达 host=${Boolean(host)} client=${Boolean(client)}`)
    await sleep(3000)
    continue
  }

  const hostShared = shared(host)
  const clientShared = shared(client)
  const diffs = Object.keys(hostShared).filter((key) => String(hostShared[key]) !== String(clientShared[key]))
  // 只有这些是真正的「共享世界状态」；event/chest/shop/screen/map 是各玩家自己的推进节奏。
  const sharedKeys = new Set(['floor', 'act', 'turn', 'side', 'enemies', 'players'])
  const sharedDiff = diffs.filter((key) => sharedKeys.has(key))
  const signature = diffs.map((key) => `${key}:${hostShared[key]}|${clientShared[key]}`).join(';')

  if (diffs.length > 0) {
    // 单次快照不同步常见于「两边各自结算的瞬间」，等 3 秒再确认一次；仍然不同才记成持久漂移。
    const [hostAgain, clientAgain] = await Promise.all([getState(HOST), getState(CLIENT)])
    const hostLater = shared(hostAgain)
    const clientLater = shared(clientAgain)
    const laterDiffs = hostLater && clientLater
      ? Object.keys(hostLater).filter((key) => sharedKeys.has(key) && String(hostLater[key]) !== String(clientLater[key]))
      : sharedDiff
    if (laterDiffs.length > 0 && signature !== lastSignature) {
      driftCount += 1
      lastSignature = signature
      console.log(`${new Date().toISOString()} 持续不同步 #${driftCount}: ${laterDiffs.map((key) => `${key} host=${hostLater[key]} client=${clientLater[key]}`).join(' | ')}`)
    } else if (laterDiffs.length === 0) {
      lastSignature = signature
    }
  }
  await sleep(2000)
}

console.log(`${new Date().toISOString()} 监视结束：漂移次数=${driftCount}`)
