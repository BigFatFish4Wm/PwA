# cortico-world-pwa

让 Coo（Coopanion 桌宠）陪你玩 **《杀戮尖塔 2》**：读取局面、出牌、喝药水、选地图、做事件、开宝箱、营火、商店，还能开本机双人 / 三人联机房。

这是 **World 扩展**，依赖 [PwA Bridge](https://github.com/BigFatFish4Wm/PwA) 这个游戏内 Mod：Mod 在本地窗口起一个 `127.0.0.1` 的 HTTP / MCP 服务，本扩展把它翻译成 Coo 能调用的工具。

> A [Cortico](https://github.com/Pal-AI-Lab/Cortico) World for *Slay the Spire 2*: the bot reads the game state and plays through the PwA bridge mod. Local only — no data leaves your machine except the model calls you already configured.

## 需要先装什么

| 依赖 | 说明 |
|---|---|
| Windows + 《杀戮尖塔 2》 | 只测过 Steam 版 **v0.111.0** |
| [PwA Bridge Mod](https://github.com/BigFatFish4Wm/PwA/releases) | 解压 `PwABridge-*.zip`，把 `PwABridge` 目录放进 `…\Slay the Spire 2\mods\` |
| Coopanion | 在当前 World 页里启用本扩展，然后**完全重启** Coopanion（World 不热加载） |

安装：在 Coopanion 的扩展页搜索 `cortico-world-pwa` 安装并启用；或者 `npm i cortico-world-pwa` 后按扩展页的「从本机目录安装」装入。

## 它能让 Coo 做什么

- **看局面**：画面、回合、能量、手牌（带稳定 `ref`）、敌人意图与血量、遗物、药水、地图、事件、奖励、商店、营火、宝箱、结算页
- **做动作**：出牌（可指定目标）、喝/丢药水、结束回合、选地图节点、事件选项与继续、领奖励/选牌/跳过、商店买卖、营火选项、开箱/选遗物、选牌界面、继续/跳过、结算退出
- **联机**：本机 host + 本机 agent 窗口；可选 0–2 个远程位（远程玩家在自己电脑上装同一个 Mod 连进来）
- **叫醒**：轮到你、需要选牌、奖励、事件继续、地图、宝箱、营火、结算等「界面在等你」的时刻主动推送提醒

只提供工具，不做决策；出牌策略由模型决定。

## 工具

| 工具 | 作用 |
|---|---|
| `sts2_view` | 读局面（默认只回变化，`full=true` 全量） |
| `sts2_act` | 执行一个原子动作 |
| `sts2_plan` | 一次提交 ≤8 步 |
| `sts2_wait` | 等到轮到你 / 出现选择（≤20 秒） |
| `sts2_diag` | 连接诊断：找到哪些窗口、角色、端口 |
| `sts2_coop_start` / `sts2_coop_stop` / `sts2_coop_state` | 一键联机房间 |

动作名和参数见 [docs/PwA-接口.md](https://github.com/BigFatFish4Wm/PwA/blob/main/docs/PwA-接口.md)。

## 配置

| 键 | 默认 | 说明 |
|---|---|---|
| `worlds.pwa.baseUrl` | 空 | 留空自动发现，优先连 client（8092） |
| `worlds.pwa.pollSeconds` | 4 | 事件轮询间隔 2–30 |
| `worlds.pwa.watch` | true | 主动提醒 |
| `worlds.pwa.auto` | true | 自动推进（收金币/继续/开箱等无决策动作） |
| `worlds.pwa.coopBind` | 127.0.0.1 | 内网穿透保持默认；局域网直连填 0.0.0.0 |
| `worlds.pwa.coopRemoteSlots` | 0 | 远程位 0–2，只在对方机器开窗，不占本机资源 |

## 联机（可选）

- 本机：host（8091）+ agent client（8092），一键拉起；
- 远程：对方装同一个 Mod，填你给的地址和口令，`-fastmp join` 连进来；
- 详细步骤见 [联机文档](https://github.com/BigFatFish4Wm/PwA/blob/main/docs/PwA-联机.md)。

## 常见问题

- **找不到窗口**：先确认游戏已启动且 Mod 加载（`%LOCALAPPDATA%\PwA\endpoints\` 有文件）；再点 `sts2_diag`。
- **端口变了**：单人 8090 / host 8091 / client 8092（被占用会顺延），World 会自动发现。
- **改了 World 没生效**：World 不热加载，重启 Coopanion。
- **联机不同步**：只支持双方都装同一个 Mod 的 broker 房；原版 Steam 客户端进不来。

## 隐私

扩展只访问本机 `127.0.0.1` 上的 Mod 端口，以及你配置的联机 broker（默认本机回环）。它不上传游戏截图或账号信息；模型调用走你自己在 Coopanion 里配置的 provider。

## 许可

MIT。联机协议部分改编自 [Bahnerbd/STS2CouchCoop](https://github.com/Bahnerbd/STS2CouchCoop)（MIT），详见 `THIRD_PARTY_NOTICES.md`。本项目不包含《杀戮尖塔 2》的任何资源或二进制。
