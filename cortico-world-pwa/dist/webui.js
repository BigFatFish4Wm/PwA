/**
 * 联机房间面板：
 *   本机固定 = 你（host，8091）+ 汐（client，8092）；
 *   远程位 0–2 只是给对方发地址和口令，不占本机进程/内存。
 */
const PHASE = {
  stopped: '未启动',
  starting: '启动中…',
  partial: '只起来一个窗口',
  running: '本机双窗口就绪',
  error: '启动失败',
}

const coopPanel = {
  mount(ctx) {
    const { root, ui } = ctx
    const card = ui.sheet({
      title: '联机房间',
      en: 'coop',
      desc: '本机固定两个窗口：你（host，8091）和汐（client，8092）。远程位只是给别人的连接信息，对方在自己电脑上开窗口，不占本机资源。',
    })

    const stateBox = ui.h('div')
    const message = ui.msgline('')

    const controls = ui.h('div', 'pwa-coop-controls')
    const slotsLabel = ui.h('span', 'pwa-coop-label')
    slotsLabel.textContent = '远程位：'
    const slots = document.createElement('select')
    for (const value of ['0', '1', '2']) {
      const option = document.createElement('option')
      option.value = value
      option.textContent = value
      slots.appendChild(option)
    }
    const tokenInput = document.createElement('input')
    tokenInput.type = 'text'
    tokenInput.placeholder = '口令（远程位>0 时自动生成）'
    tokenInput.autocomplete = 'off'
    const roll = ui.button('换一个口令')
    const copy = ui.button('复制远程命令')
    controls.append(slotsLabel, slots, tokenInput, roll, copy)

    const actions = ui.actions()
    const stop = ui.button('关闭联机', { variant: 'danger' })
    const start = ui.button('开始联机', { variant: 'primary' })
    actions.append(ui.h('span', 'grow'), stop, start)

    card.body.append(stateBox, controls, message, actions)
    root.appendChild(card.el)

    let snapshot = null
    let busy = false

    const render = (next) => {
      if (ctx.signal.aborted) return
      snapshot = next
      const running = next.phase === 'running'
      const pending = next.phase === 'starting'
      const remote = next.remote ?? { slots: 0, connected: 0, token: '', endpoints: [], command: '' }
      const ports = next.ports ?? {}
      stateBox.replaceChildren(ui.kv([
        { k: '状态', v: ui.pill(PHASE[next.phase] ?? next.phase, running ? 'on' : 'off') },
        { k: '你（host）', v: ports.host ? `8091 · pid ${next.host?.pid ?? next.hostPid ?? '—'}` : '—' },
        { k: '汐（本机 client）', v: ports.client ? `8092 · pid ${next.client?.pid ?? next.clientPid ?? '—'}` : '—' },
        { k: '远程位', v: remote.slots > 0 ? `已连 ${remote.connected}/${remote.slots}` : '未开启' },
        { k: '令牌', v: remote.slots > 0 ? (remote.token || '—') : '—' },
        { k: '地址', v: remote.slots > 0 ? (remote.endpoints?.[0] ?? '走隧道：填隧道地址') : '—' },
        { k: '说明', v: next.detail || '—' },
      ]))
      if (document.activeElement !== tokenInput) tokenInput.value = remote.slots > 0 ? remote.token ?? '' : ''
      if (slots.value !== String(remote.slots)) slots.value = String(remote.slots)
      start.disabled = busy || running || pending
      start.textContent = next.phase === 'error' ? '重试' : '开始联机'
      stop.disabled = busy || !next.stoppable
      copy.disabled = busy || !remote.command
      roll.disabled = busy || !(Number(slots.value) > 0)
    }

    const refresh = async () => {
      try {
        render(await ctx.invoke('state'))
      } catch (error) {
        if (!ctx.signal.aborted) message.textContent = `状态读取失败：${errorText(error)}`
      }
    }

    const run = (method, args, working, done) => {
      busy = true
      start.disabled = true
      stop.disabled = true
      message.textContent = working
      void ctx.invoke(method, args).then(
        async (next) => {
          busy = false
          render(next)
          message.textContent = done
          await ctx.refresh()
        },
        async (error) => {
          busy = false
          message.textContent = `${method === 'start' ? '启动' : '关闭'}失败：${errorText(error)}`
          await refresh()
          await ctx.refresh()
        },
      )
    }

    const readOptions = () => ({
      remoteSlots: Math.max(0, Math.min(2, Number(slots.value) || 0)),
      token: tokenInput.value.trim(),
    })

    start.addEventListener('click', () => {
      if (snapshot?.phase === 'running') return
      run('start', [readOptions()], '正在拉起本机两个窗口…（首次约 20–40 秒）', '本机双窗口已就绪')
    }, { signal: ctx.signal })

    stop.addEventListener('click', () => {
      run('stop', [], '正在关闭…', '已关闭')
    }, { signal: ctx.signal })

    roll.addEventListener('click', () => {
      const alphabet = 'abcdefghijkmnpqrstuvwxyz23456789'
      let value = ''
      for (let i = 0; i < 8; i += 1) value += alphabet[Math.floor(Math.random() * alphabet.length)]
      tokenInput.value = value
    }, { signal: ctx.signal })

    copy.addEventListener('click', async () => {
      const command = snapshot?.remote?.command
      if (!command) return
      try {
        await navigator.clipboard.writeText(command)
        message.textContent = '远程启动命令已复制，发给对方即可（他也要装 PwA Mod）'
      } catch {
        message.textContent = command
      }
    }, { signal: ctx.signal })

    void refresh()
    ctx.interval(() => { if (!busy) void refresh() }, 2000)
  },
}

function errorText(error) {
  return error instanceof Error ? error.message : String(error)
}

export default { panels: { coop: coopPanel } }
