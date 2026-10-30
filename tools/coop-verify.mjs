/** 共享世界状态一致性验证：采样 N 秒，统计不同步次数与最长持续时间。 */
const args = process.argv.slice(2)
const arg = (name, fallback) => {
  const i = args.indexOf(name)
  return i >= 0 && args[i + 1] ? args[i + 1] : fallback
}
const HOST = arg('--host', 'http://127.0.0.1:8091')
const CLIENT = arg('--client', 'http://127.0.0.1:8092')
const SECONDS = Number(arg('--seconds', '90'))

const sleep = (ms) => new Promise((r) => setTimeout(r, ms))
const getState = async (base) => {
  try {
    const response = await fetch(`${base}/state`, { signal: AbortSignal.timeout(3000) })
    return response.ok ? await response.json() : null
  } catch {
    return null
  }
}

function shared(state) {
  if (!state) return null
  const combat = state.combat ?? {}
  return {
    floor: state.run?.floor ?? null,
    act: state.run?.act ?? null,
    turn: combat.turn ?? null,
    side: combat.side ?? null,
    enemies: (combat.enemies ?? [])
      .map((enemy) => `${enemy.combat_id}:${enemy.hp}/${enemy.max_hp}:${enemy.block ?? 0}`)
      .sort()
      .join('|'),
  }
}

let samples = 0
let mismatchedSamples = 0
let longestMs = 0
let runStart = null
const deadline = Date.now() + SECONDS * 1000

while (Date.now() < deadline) {
  const [host, client] = await Promise.all([getState(HOST), getState(CLIENT)])
  const hostShared = shared(host)
  const clientShared = shared(client)
  if (hostShared && clientShared) {
    samples += 1
    const diffs = Object.keys(hostShared).filter((key) => String(hostShared[key]) !== String(clientShared[key]))
    if (diffs.length > 0) {
      mismatchedSamples += 1
      runStart ??= Date.now()
      console.log(`不同步: ${diffs.map((key) => `${key} host=${hostShared[key]} client=${clientShared[key]}`).join(' | ')}`)
    } else if (runStart != null) {
      longestMs = Math.max(longestMs, Date.now() - runStart)
      runStart = null
    }
  }
  await sleep(700)
}
if (runStart != null) longestMs = Math.max(longestMs, Date.now() - runStart)

console.log(JSON.stringify({ seconds: SECONDS, samples, mismatchedSamples, longestMs }, null, 0))
