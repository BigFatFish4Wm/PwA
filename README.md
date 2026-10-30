# PwA（Play with Agent）· 杀戮尖塔 2 桥接

让 AI 助手（Coopanion 的「汐」）陪你打《杀戮尖塔 2》：**Mod** 把一个游戏窗口的状态和原子动作暴露成本机 MCP / HTTP，**World** 把它们翻译成助手能调用的工具。Mod 只提供动作、不做决策；单人、本机双窗口联机都支持。

- 仓库 / 下载：**<https://github.com/BigFatFish4Wm/PwA>**（release 里有现成 zip）
- 游戏版本：**v0.111.0**（Windows / Steam）
- Mod 版本：**1.0.0**（`PwABridge`）
- World 版本：**1.0.0**（`cortico-world-pwa`，Coopanion API 6）

## 能做什么

| 能力 | 说明 |
|---|---|
| 看局面 | 楼层、幕、玩家/敌人、手牌（含是否可打出及原因）、意图、牌堆、金币、遗物、药水、地图、事件、奖励、商店、营火、宝箱、选牌界面 |
| 出牌/战斗 | 打出任意牌（可用稳定的 `target_id` 指定敌人）、用药水、弃药水、结束回合 |
| 推进流程 | 地图选点、事件选项、宝箱、遗物、奖励（金币/药水/卡牌）、商店买卖、营火、继续/跳过、结算页退出 |
| 选择界面 | 网格选牌、三选一、卡牌包、选遗物、跳过；超选会被拒绝 |
| 联机 | 本机双窗口：host 自己跑 broker 中继，agent 连 client 窗口；共享状态（回合/阵营/敌人血量）同步 |
| 省调用 | World 侧自动收金币/药水、继续、开箱、无牌可出时自动结束回合（可关） |

## 安装

详细步骤（含截图式说明、排错）见 **[docs/使用教程.md](docs/使用教程.md)**。最短路径：

1. **装 Mod**：把 `PwABridge.dll` + `PwABridge.json` 放进
   `…\Slay the Spire 2\mods\PwABridge\`（目录名和 json 里的 id 都要是 `PwABridge`）；
2. **装 World**：Coopanion 扩展页安装 `cortico-world-pwa`（或把目录链接进
   `…\coopanion\data\extensions\node_modules\cortico-world-pwa`），启用后重启 Coopanion；
3. **启动游戏**：正常开游戏；Mod 会打印 `[PwA] 就绪 http://127.0.0.1:8090/mcp`。

端口按角色自动分配：**单人 8090 / host 8091 / client 8092**。

## 单人

1. 进游戏开一局；
2. 让助手用 `sts2_view` 看局面、`sts2_act` 出牌、`sts2_plan` 连做几步；
3. 想手打就在游戏窗口里直接操作，助手只在你让它动的时候动。

## 联机（本机双窗口）

两种一键方式：

- **面板**：Coopanion → World 面板「联机房间」→「开始联机」；
- **指令**：助手调用 `sts2_coop_start`（关闭用 `sts2_coop_stop`）；
- 或者脚本：`powershell -ExecutionPolicy Bypass -File tools\start-coop.ps1`。

拉起后：

| 窗口 | 谁玩 | Mod 端口 |
|---|---|---|
| host | 你 | 8091 |
| client | 助手（汐） | 8092 |

两个窗口都用键鼠，操作哪个窗口哪个窗口响应；World 自动连 client。机制和限制见 [docs/PwA-联机.md](docs/PwA-联机.md)。

## 工具一览（World 暴露）

`sts2_view` 看局面 · `sts2_act` 单动作 · `sts2_plan` 连做 · `sts2_wait` 等变化 · `sts2_diag` 连接诊断 · `sts2_coop_start` / `sts2_coop_stop` / `sts2_coop_state` 联机房间。

动作清单（34+）：`play_card`、`end_turn`、`use_potion`、`discard_potion`、`choose_map_node`、`choose_event_option`、`open_chest`、`choose_treasure_relic`、`choose_rest_option`、`claim_reward`、`choose_reward_card`、`skip_reward_cards`、`buy_card`/`buy_relic`/`buy_potion`、`remove_card_at_shop`、`open/close_shop_inventory`、`proceed`、`select_card`、`choose_bundle`、`choose_relic`、`skip_selection`、`confirm_selection`、`choose_character`、`embark`、`continue_run`、`open_singleplayer`、`start_standard_run`、`back`、`confirm_modal`/`dismiss_modal`、`continue_game_over`、`inspect_tree`、`click_node`。

接口细节：[docs/PwA-接口.md](docs/PwA-接口.md)。

## 配置

- Mod：环境变量 `PWA_ENABLED`、`PWA_PORT`、`PWA_PORT_FALLBACK`、`PWA_PUBLISH_ENDPOINT`；
- 联机：`PWA_COOP=host|client`、`PWA_COOP_INDEX`、`PWA_COOP_ENDPOINT`（默认 `127.0.0.1:33772`）、`PWA_COOP_SESSION`；
- World：`worlds.pwa.*`，包括 `auto`、`autoEndTurn`、`waitTurnSeconds`、`coopGameRoot`、`coopBrokerPort`、`coopWithClient`。

## 排错

| 现象 | 检查 |
|---|---|
| 助手连不上 | `http://127.0.0.1:8090/health` 是否 200；`%LOCALAPPDATA%\PwA\endpoints\` 是否有当前 pid 的 json |
| 画面是 UNKNOWN | 转场中；等 1–2 秒或 `sts2_view full=true` |
| 联机超时/连不上 | 两个窗口都要装同一个 Mod；host 先起；看 `%LOCALAPPDATA%\PwA\coop-<pid>.log` |
| 桌宠被工具调用刷屏 | 见下方「桌宠通知静音」 |

## 桌宠通知静音

DSH ↔ Coopanion 桌宠桥默认会把每次工具调用、回合开始/结束都推给桌宠。仓库提供补丁：

```bash
node tools/patch-coopanion-connector.mjs          # 只保留 回复 / 审批 / 提问 / 错误
node tools/patch-coopanion-connector.mjs --revert # 还原
```

改的是 profile 里的 `dist/index.cjs`，**插件升级/重装后要重新执行**，改完需重启桌面端。

## 目录

| 路径 | 内容 |
|---|---|
| `PwA/` | Mod 源码（C# / .NET 9 / Harmony） |
| `cortico-world-pwa/` | World 源码（Node ESM） |
| `docs/` | 使用教程、接口、联机、复盘 |
| `tools/` | 安装、打包、联机、验证脚本 |
| `release/` | 发布产物（zip） |
| `VERSIONS.md` | 版本与标签 |

## 构建与发布

```powershell
cd PwA; dotnet build -c Release          # 产出 bin\Release\net9.0\PwABridge.dll
powershell -ExecutionPolicy Bypass -File tools\install-mod.ps1   # 装进游戏
powershell -ExecutionPolicy Bypass -File tools\pack-release.ps1  # 生成 release/*.zip
```

## 许可

Mod 与 World 源码：MIT。`PwA/Coop/Protocol`、`PwA/Coop/Broker` 改编自
[Bahnerbd/STS2CouchCoop](https://github.com/Bahnerbd/STS2CouchCoop)（MIT），
详见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。不包含游戏资源或二进制。
