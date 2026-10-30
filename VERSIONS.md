# 版本记录

每一版都要能回退：源码由 git 提交 + 标签记录，安装到游戏里的 DLL 由
`tools\backup-version.ps1` 一起打进 `backups\<标签>-<时间>\`。

| 版本 | 时间 | 内容 | git 标签 |
|---|---|---|---|
| **world-memory 1.0.0** | 2026-10-30 | **新 World `cortico-world-memory`（记忆检索）**：工作区 markdown → 本地 SQLite FTS5(trigram) 索引 + 本地向量；每次重建前缀自动注入 top-5（硬上限 2000 token、连续两轮不重复）；`memory_search` / `memory_index` / `memory_status`；frontmatter `status`/`supersedes` 作废旧事实；引用计数 + 时间衰减；索引不可用时静默退回 grep。对应 `memory-retrieval-spec-2026-10-30.md`；90 项端到端测试（含真实工作区 99 篇 / 6537 段） | `v1.1.0` |
| **1.0.0** | 2026-10-30 | **首个发布版**：单人全流程 + 本机双窗口联机（broker 传输）可用；`release/` 提供 `PwABridge-1.0.0.zip` 与 `cortico-world-pwa-1.0.0.zip`，附《使用教程》《发布说明》 | `v1.0.0` |
| 0.1.0 | 2026-10-30 | Mod 骨架：MCP + REST、状态快照、战斗/地图/事件/宝箱/营火/奖励/商店/选牌动作 | `v0.1.0` |
| 0.1.1 | 2026-10-30 | 主菜单/单人子菜单导航、状态分区容错、卡牌文本代入实际数值、事件结束可 proceed、点击方式按控件类型区分 | `v0.1.1` |
| 0.2.0 | 2026-10-30 | 新增 Coopanion World（`cortico-world-pwa`）：自动发现窗口、view/act/plan/wait/diag、delta 视图、事件提醒；本地联调脚本 | `v0.2.0` |
| 0.2.1 | 2026-10-30 | 稳定性：HTTP 线程只读脉冲缓存（之前 /health 轮询会冻游戏）；结束回合改走按钮路径；商店可 proceed；结算页 Continue→MainMenu；主菜单按钮等加载完成 | `v0.2.1` |
| 0.8.2 | 2026-10-30 | 联机验证工具：共享状态一致性采样（coop-verify）+ 共享字段监视（coop-monitor）；实测双窗口从第 1 层打到第 2 幕第 20 层（含 boss、换幕），共享状态（层/幕/回合/阵营/敌人血量）收敛，瞬时差异 ≤1.3–2.3s（演出同步），中继 2.5 万条转发 0 丢弃 | `v0.8.2` |
| 0.8.1 | 2026-10-30 | 选牌超过 max 时拒绝 select_card（避免卡在超选）；新增双窗口 soak 脚本（逐步对比 host/client）；soak 稳定态验证：回合/阵营/敌人血量一致，差异只在两边各自出牌的瞬间 | `v0.8.1` |
| 0.8.0 | 2026-10-30 | 联机（broker 传输）跑通：host 窗口内置中继、client 连它；NetHost/NetClient 传输替换 ENet/Steam；补主线程抽包、握手补发、身份 NetId 对齐、IsConnected 语义修正；角色/端口按联机配置（host 8091 / client 8092），双窗口实测到同房间、同敌人、同血量、动作互相同步 | `v0.8.0` |
| 0.7.2 | 2026-10-30 | 奖励屏判据改为「可领奖励 / 继续按钮 enabled / 卡牌可选」三者之一；地图屏可见就构建 map 区，只要有点可选就给 choose_map_node（不再被空奖励屏盖住） | `v0.7.2` |
| 0.7.1 | 2026-10-30 | 奖励覆盖层只在「还有奖励/跳过按钮」或没有其它房间屏时才优先；领完奖励后自动让位给地图/商店/营火/事件，不再卡在 REWARD | `v0.7.1` |
| 0.7.0 | 2026-10-30 | 奖励覆盖层优先于战斗房间判定（修 Boss「搜刮！」面板被认成 COMBAT）；BuildReward 不再依赖 ActiveScreenContext；奖励屏常驻 proceed，无继续按钮时点「跳过」箭头 | `v0.7.0` |
| 0.6.6 | 2026-10-30 | 重复动作判据改为「局面数值指纹」（画面/回合/层数/金币/血/手牌/药水/遗物/敌人/事件选项），奖励界面连领两项不再误伤；窗口缩到 1.2s | `v0.6.6` |
| 0.6.5 | 2026-10-30 | 选牌候选按 CardModel 实例去重（同一张牌被两个 holder 显示时不再重复占行/重复 ref/多算 selected_count）；选中区也按实例去重；auto 日志带 screen | `v0.6.5` |
| 0.6.4 | 2026-10-30 | 可重复事件选项（真理石板）再选必须带 again:true，state.event 增加 repeatable/was_chosen；同动作同参数在局面未变时 1.5s 内拒绝重复；自动推进同一动作只做一次、局面不变即停；Mod 日志记录每个动作与参数 | `v0.6.4` |
| 0.6.3 | 2026-10-30 | in_combat = 战斗进行中且场上还有敌人（打完最后一只/死亡立即变 false，不再靠 screen 兜底）；选中数改为 _selectedCards + NSelectedHandCardHolder + 候选标记三源合并，selected_count 始终存在 | `v0.6.3` |
| 0.6.2 | 2026-10-30 | World 增加 WORLD_BUILD 标记（sts2_diag.build、启动日志），用于确认 Coopanion 实际加载的是哪一版；新增 plan 不结束回合的回归测试 | `v0.6.2` |
| 0.6.1 | 2026-10-30 | turn_phase 改用游戏权威的 PlayerCombatState.Phase（Start/AutoPrePlay 不再算可出牌）；play_card/end_turn 只在 Play 阶段给出；in_combat 以 screen 兜底；自动结束回合只在 view 生效、有可用药水时不翻页、可配置 autoEndTurn | `v0.6.1` |
| 0.6.0 | 2026-10-30 | 结算页退出修好（优先 MainMenu、点两次策略、6s 等待、phase 字段）；end_turn 回执必须等「新回合可出牌」（turn+1 且 turn_phase=player_turn）；敌人加 combat_id，动作支持 target_id（跨步稳定）；piles 加 hand/total、run 加 deck_size | `v0.6.0` |
| 0.5.1 | 2026-10-30 | pending 回执带 do_not_repeat；plan 遇 pending/选牌即停并把后续步骤标 skipped；自动结束回合先复核（turn_phase=player_turn 且确实无牌可出）；重复提交防护；商店无 exit 按钮时自动开关一次货架 | `v0.5.1` |
| 0.5.0 | 2026-10-30 | 选择界面按官方 AutoSlay 注册表统一识别：卡牌（网格/三选一）、卡包、遗物、水晶球；新增 choose_relic / skip_selection；未支持界面显式标 unsupported | `v0.5.0` |
| 0.4.1 | 2026-10-30 | 只在「游戏真的在等你选牌」时放行（ActionExecutor/ActionQueueIsPaused），debuff 仅作信息；画面识别按可见节点兜底（修涅奥事件 UNKNOWN）；转场 UNKNOWN 等到画面确定；新增卡牌包选择界面与 choose_bundle（涅奥卷轴箱）；回合开始缺手牌时等抽牌 | `v0.4.1` |
| 0.4.0 | 2026-10-30 | 回合开始结算带 turn_phase/player_debuffs/note，有 debuff 或选牌时照常回执不再硬等；结束回合回执等回到自己回合；World 自动推进（收金币/药水、事件继续、开箱、唯一地图、无牌可出自动结束回合、主菜单继续）；超长回执改为合法 JSON 瘦身 | `v0.4.0` |
| 0.3.1 | 2026-10-30 | 回合开始前的遗物/状态结算不再当稳定局面（最多等 2.5s 到真正开局）；药水回执等伤害/格挡/能力落地；卡面 res:// 图标路径换成「能量/辉星/金币/格挡」；in_combat 以画面为准 | `v0.3.1` |
| 0.3.0 | 2026-10-30 | 动作回执改为「等局面稳定」后再读（stale 快照修复）；选牌覆盖已选区 NSelectedHandCardHolder、三选一 NChooseACardSelectionScreen（普通 NCardHolder）、确认改点 SelectModeConfirmButton；`selection` 增加 screen_type/min/max/unbounded/selected_refs；营火/宝箱/商店不再写死等 10s；客户端超时返回 may_have_executed | `v0.3.0` |
| 0.2.2 | 2026-10-30 | 营火选完选项后 proceed 按钮不亮（补 ShowProceedButton + 等启用）；地图从房间回来时 IsTravelEnabled 卡 false（补 SetTravelEnabled）；主菜单按钮改用 Released 信号；结算页按钮未出现时报 blocked 可重试 | `v0.2.2` |

## 接口与端口

- Mod 本地服务：单人 `8090`、host `8091`、client `8092`（被占则顺延）。
- 端点名片：`%LOCALAPPDATA%\PwA\endpoints\<pid>.json`。
- MCP：`POST /mcp`；REST：`/health`、`/state`、`/actions`、`/act`、`/plan`、`/events`。

## 坑

- 安装目录 `mods\PwABridge\` 里**只能有一份清单**（`PwABridge.json`）。多放一个 `mod_id.json`
  会被游戏当成两个同 id 的 mod（已踩过）。
- 游戏按 manifest 的 `id` 找 `<id>.dll`，DLL 名必须和 id 一致。
- 换 DLL 必须重启游戏；联调期间每次装完都用 `backup-version.ps1` 留档。
- 旧 world `cortico-world-sts2` 的工具名（`sts2_act` 等）会和新 world 撞车，启用新 world 前先停用旧的。
