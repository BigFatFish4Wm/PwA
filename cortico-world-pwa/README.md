# cortico-world-pwa

## 安装（发布包）

1. 解压 `cortico-world-pwa-1.0.0.zip` 到一个固定目录；
2. 让 Coopanion 能加载它：把整个 `cortico-world-pwa` 目录放到
   `…\coopanion\data\extensions\node_modules\cortico-world-pwa`（或建 junction 链接）；
3. Coopanion 设置里启用 `worlds.pwa`（`enabled: true`），**完全退出再打开**（ESM 不热更）；
4. 游戏那边装好 `PwABridge` Mod 并启动，World 会自动发现窗口并优先连 client（8092）。

需要 Node ≥ 22、Coopanion（World API 6）。下面的仓库内脚本路径（`tools\...`）只在本仓库里有效，
单独安装时不需要。

Coopanion（Cortico）的杀戮尖塔 2 World：把 [PwA Bridge](../PwA) Mod 的本机 HTTP 接口
翻译成汐的工具。World 只做「读局面 + 翻译动作」，不做任何决策。

## 它连谁

Mod 在每个游戏窗口起一个 `127.0.0.1` 服务，端口按角色分配：

| 窗口 | 端口 |
|---|---|
| 单人 | 8090 |
| 主机（host） | 8091 |
| 第二窗口（client，双开时新开的那个） | 8092 |

端口被占时顺延；每个进程会在 `%LOCALAPPDATA%\PwA\endpoints\<pid>.json` 留名片。
World 自动发现并**优先选 client 窗口**——正好是「汐自己那一侧」；主机窗口不需要被接管。

也可以在配置里写死 `worlds.pwa.baseUrl`（如 `http://127.0.0.1:8092`）。

## 工具

| 工具 | 作用 |
|---|---|
| `sts2_view` | 读局面（画面、可用动作、手牌带 `ref`、敌人意图、地图/事件/奖励…）；默认只回变化，`full=true` 强制全量 |
| `sts2_act` | 执行一个动作，参数照 `state` 里的结构填 |
| `sts2_plan` | 一次提交 ≤8 步，遇不可用就停 |
| `sts2_wait` | 等到轮到/出现选择（≤20 秒），只在过场时用 |
| `sts2_diag` | 连接诊断：哪些窗口、角色、端口、连的是哪个 |

动作名与参数见仓库根目录 `docs/PwA-接口.md`，以及环境提示词 `ENV_PROMPT.md`。

## 配置

`worlds.pwa.*`：`baseUrl`（留空自动发现）、`pollSeconds`（2–30，默认 4）、`watch`（主动提醒）、
`delta`（只回变化）、`timeoutMs`、`maxChars`。`enabled` 由 Coopanion 的 World 页开关。

## 本地安装 / 联调

```powershell
# 1) 登记包 + 写配置（会停用旧的 sts2 world，工具名会撞）
node tools\enable-world.mjs

# 2) 建 node_modules 链接
powershell -ExecutionPolicy Bypass -File tools\link-world.ps1

# 3) 重启 Coopanion（ESM 不热更）

# 不经 Coopanion 直接联调：
node cortico-world-pwa\test\run.mjs diag
node cortico-world-pwa\test\run.mjs view
node cortico-world-pwa\test\run.mjs combat --end-turn
```

## 设计取舍

- **状态白名单**：只发决策需要的字段；牌堆只给计数，牌组给 `ID×n`，回执不带整份原始状态。
- **ref 稳定**：手牌 `c1..cN` 由 Mod 绑定在卡牌实例上，一回合内不随出牌重排；World 不自己编号。
- **动作前置校验**：`sts2_act` 会先对照 `state.actions` 和手牌 ref；`blocked`/`needs_target`/`stale_ref`
  会带 `hint` 返回，让模型重读而不是瞎重试。
- **事件提醒**：轮询 Mod 的事件环，只在「轮到你」「需要选择」时推一条 external 事件（debounce），不刷屏。


## 一键联机

- 面板：Coopanion 的 World 面板里「联机房间」→「开始联机」（拉起 host 8091 + client 8092，host 自己开 broker 中继）。
- 指令：sts2_coop_start / sts2_coop_stop / sts2_coop_state。
- 已经开着的窗口会复用；关闭只会关掉 8091/8092 上那两个联机窗口。
