const PHASE = {
  stopped: '未启动',
  starting: '启动中…',
  partial: '只起来一个',
  running: '两个窗口就绪',
  error: '启动失败',
}

const coopPanel = {
  mount(ctx) {
    const { root, ui } = ctx
    const card = ui.sheet({
      title: '联机房间',
      en: 'coop',
      desc: '一键拉起 host（8091）和 client（8092）两个窗口：host 自己开 broker 中继，汐连 client 窗口。已经开着的窗口会复用。',
    })
    const stateBox = ui.h('div')
    const message = ui.msgline('')
    const stop = ui.button('关闭联机', { variant: 'danger' })
    const start = ui.button('开始联机', { variant: 'primary' })
    const actions = ui.actions()
    actions.append(ui.h('span', 'grow'), stop, start)
    card.body.append(stateBox, message, actions)
    root.appendChild(card.el)

    let snapshot = null
    let busy = false

    const render = (next) => {
      if (ctx.signal.aborted) return
      snapshot = next
      const running = next.phase === 'running'
      const pending = next.phase === 'starting'
      const ports = next.ports ?? {}
      stateBox.replaceChildren(ui.kv([
        { k: '状态', v: ui.pill(PHASE[next.phase] ?? next.phase, running ? 'on' : 'off') },
        { k: '你玩', v: ports.host ? `host · ${ports.host}` : '—' },
        { k: '汐连', v: ports.client ? `client · ${ports.client}` : '—' },
        { k: '进程', v: [next.hostPid ? `host ${next.hostPid}` : null, next.clientPid ? `client ${next.clientPid}` : null].filter(Boolean).join(' / ') || '—' },
        { k: '说明', v: next.detail || '—' },
      ]))
      start.disabled = busy || running || pending
      start.textContent = next.phase === 'error' ? '重试' : '开始联机'
      stop.disabled = busy || !next.stoppable
    }

    const refresh = async () => {
      try {
        render(await ctx.invoke('state'))
      } catch (error) {
        if (!ctx.signal.aborted) message.textContent = `状态读取失败：${errorText(error)}`
      }
    }

    const run = (method, working, done) => {
      busy = true
      start.disabled = true
      stop.disabled = true
      message.textContent = working
      void ctx.invoke(method).then(
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

    start.addEventListener('click', () => {
      if (snapshot?.phase === 'running') return
      run('start', '正在拉起两个窗口…（首次约 20–40 秒）', '两个窗口已就绪')
    }, { signal: ctx.signal })

    stop.addEventListener('click', () => {
      run('stop', '正在关闭…', '已关闭')
    }, { signal: ctx.signal })

    void refresh()
    ctx.interval(() => { if (!busy) void refresh() }, 2000)
  },
}

function errorText(error) {
  return error instanceof Error ? error.message : String(error)
}

export default { panels: { coop: coopPanel } }
