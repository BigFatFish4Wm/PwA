# PwA 接口（Mod ↔ World）

游戏：Slay the Spire 2 v0.111.0；Mod：PwA Bridge 0.1.x。
所有接口只监听 `127.0.0.1`，端口：单人 8090 / host 8091 / client 8092（被占顺延）。

## 端点

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/health` | 服务就绪、角色、端口、当前画面、是否在战斗/局内 |
| GET | `/state` | 完整局面（紧凑结构，见下） |
| GET | `/actions` | 只读可用动作 + 就绪原因 |
| GET | `/events?since=N` | 事件环增量（seq 单调递增） |
| POST | `/act` | `{ "action": "play_card", "card_ref": "c3", "target_index": 0 }` |
| POST | `/plan` | `{ "steps": [ { "action": ..., 参数 } ] }`，最多 16 步 |
| POST | `/mcp` | MCP Streamable HTTP（JSON-RPC：`initialize` / `tools/list` / `tools/call`） |

错误统一为 `{ "error": { "code", "message", "details", "retryable", "status_code" } }`。

## `/state` 结构（只列有内容的段）

```json
{
  "seq": 42, "screen": "COMBAT", "turn": 3, "in_combat": true,
  "actions": ["play_card", "end_turn", "use_potion", "discard_potion"],
  "combat": {
    "turn": 3, "side": "Player", "my_turn": true, "over_or_ending": false,
    "me": { "hp": 62, "max_hp": 80, "block": 5, "energy": 2, "stars": 0, "powers": [{"id":"STRENGTH_POWER","amount":2,"debuff":false}], "potions": [{"slot":0,"empty":false,"id":"...","name":"...","usable":true}] },
    "hand": [ { "i": 0, "ref": "c1", "id": "STRIKE_IRONCLAD", "name": "打击", "cost": 1, "playable": true, "why": null, "target_type": "AnyEnemy", "targets": [0], "text": "造成6点伤害。" } ],
    "enemies": [ { "i": 0, "name": "缩小甲虫", "hp": 38, "max_hp": 38, "block": 0, "alive": true, "intents": [{"kind":"Attack","damage":4,"hits":1,"total":4}], "powers": [] } ],
    "players": [ { "i": 0, "is_me": true, "hp": 62, "max_hp": 80, "block": 5, "energy": 2, "alive": true } ],
    "piles": { "draw": 6, "discard": 3, "exhaust": 0 }
  },
  "run": { "character": "铁甲战士", "floor": 3, "act": 1, "hp": 62, "max_hp": 80, "gold": 111, "deck": [...], "relics": [...], "potions": [...] },
  "map": { "open": true, "current": "1,0", "options": [ {"i":0,"coord":"2,0","node_type":"Monster"} ], "votes": [...] },
  "event": { "id": "NEOW", "title": "涅奥", "finished": false, "options": [ {"i":0,"title":"羽翼之靴","locked":false,"relic_id":"WINGED_BOOTS","description":"..."} ] },
  "reward": { "card_choice": false, "can_proceed": true, "rewards": [ {"i":0,"kind":"gold","description":"12金币","claimable":true} ] },
  "shop": { "open": false, "gold": 120, "cards": [...], "relics": [...], "potions": [...], "remove": {"price":75,"available":true,"affordable":true} },
  "rest": { "options": [ {"i":0,"id":"HEAL","enabled":true,"requires_target":false} ], "can_proceed": false },
  "chest": { "opened": true, "relics": [ {"i":0,"id":"...","name":"..."} ], "can_proceed": false },
  "selection": { "kind": "card", "prompt": "选择1张牌来变化。", "confirm_available": false, "cards": [ {...} ] },
  "modal": { "type": "NErrorPopup", "buttons": [ {"name":"YesButton","enabled":true} ] },
  "character_select": { "characters": [ {"i":0,"id":"IRONCLAD","locked":false,"selected":true} ], "can_embark": true },
  "singleplayer": { "options": [ {"i":0,"name":"StandardButton","enabled":true} ], "can_back": true },
  "game_over": { "can_continue": true }
}
```

## 动作

| action | 参数 | 说明 |
|---|---|---|
| `play_card` | `card_ref` 或 `card_index`，指向牌加 `target_index` | `blocked` = 没轮到你；`needs_target` = 缺目标；`target_gone` = 目标没了 |
| `end_turn` | 无 | |
| `use_potion` | `slot`，指向药水加 `target_index` | |
| `discard_potion` | `slot` | |
| `choose_map_node` | `index` | 多人时提交投票 |
| `choose_event_option` | `index`（事件已结束时可不传） | 事件翻页会返回新的 `event.options` |
| `open_chest` / `choose_treasure_relic` | 无 / `index` | |
| `choose_rest_option` | `index`，MEND 加 `target_index` | |
| `claim_reward` / `choose_reward_card` / `skip_reward_cards` | `index` / `index` / 无 | |
| `proceed` | 无 | 离开房间；事件结束时等价于 `NEventRoom.Proceed()` |
| `select_card` / `confirm_selection` | `card_ref` 或 `index` / 无 | 选牌界面（战斗内与牌库） |
| `buy_card` / `buy_relic` / `buy_potion` / `remove_card_at_shop` | `index` / 无 | |
| `open_shop_inventory` / `close_shop_inventory` | 无 | |
| `continue_run` / `open_singleplayer` / `start_standard_run` | 无 | 菜单导航 |
| `choose_character` / `embark` | `index` / 无 | 选角 |
| `confirm_modal` / `dismiss_modal` | 无 | |
| `continue_game_over` | 无 | |
| `inspect_tree` | `path`、`depth` | 只读调试：打印界面节点 |
| `click_node` | `path`、`mode` | 调试用兜底点击 |

## 就绪语义

- `blocked` + `retry_after_ms`：还没轮到你，等；不要当失败。
- `pending`：动作已被接受，但还在过场；接着看新状态。
- `needs_selection`：牌/药水触发了选牌界面，用 `select_card` 处理。

## MCP 工具

`get_state`、`get_actions`、`health`、`get_events`、`play_card`、`end_turn`、`use_potion`、
`discard_potion`、`choose_map_node`、`choose_event_option`、`open_chest`、`choose_treasure_relic`、
`choose_rest_option`、`claim_reward`、`choose_reward_card`、`skip_reward_cards`、`buy_card`、
`buy_relic`、`buy_potion`、`remove_card_at_shop`、`open_shop_inventory`、`close_shop_inventory`、
`proceed`、`select_card`、`confirm_selection`、`choose_character`、`embark`、`continue_run`、
`open_singleplayer`、`start_standard_run`、`back`、`confirm_modal`、`dismiss_modal`、
`continue_game_over`、`plan`、`inspect_tree`、`click_node`。

## 选择界面（`screen = CARD_SELECTION`）

游戏里「等你做选择」的界面不止选牌。清单取自游戏自己的自动处理注册表
`MegaCrit.Sts2.Core.AutoSlay.Handlers.Screens`（官方把所有需要自动处理的界面列全了），
Mod 用同一张表做识别（`GameNodes.SelectionKinds`），`state.selection.kind` 区分：

| kind | 界面类 | 状态字段 | 动作 |
|---|---|---|---|
| `card` | `NCardGridSelectionScreen` 及其子类（`NDeckCard/Upgrade/Transform/Enchant/Simple/CombatPile`）、`NChooseACardSelectionScreen` | `cards[]`、`min/max`、`selected_refs`、`prompt` | `select_card` + `confirm_selection` |
| `bundle` | `NChooseABundleSelectionScreen` | `bundles[]`（每包 `cards[]`） | `choose_bundle {index}` |
| `relic` | `NChooseARelicSelection` | `relics[]` | `choose_relic {index}` |
| `crystal_sphere` | `NCrystalSphereScreen` | `unsupported: true` | 暂不支持，先别提交动作 |

所有 kind 都可能带 `skip_available`（界面有 `NChoiceSelectionSkipButton` 时），对应动作 `skip_selection`。

另外两类「选遗物」不走这个界面：
- 事件选项里的遗物预览：`choose_event_option`（`state.event.options[i].relic_id`）
- 宝箱：`choose_treasure_relic`