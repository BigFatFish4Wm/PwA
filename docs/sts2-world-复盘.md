# Coopanion World 契约 · STS2 旧世界复盘

引用：`types.ts` `world.ts` `dry-mount.ts` `manifest.ts` `extensions.ts` `prefix.ts` `config-schema.ts` `loop.ts` `core.ts` `template.ts` `console/world.ts` `webui-protocol.ts` 均在 `…\cortico\src\`；`O`=`D:\BigFatWhale\_dev\cortico-world-sts2\index.js`；`N1`~`N4`=`…\notes\` 的问题清单/试玩反馈/联机不同步/地图投票。

## A. 契约要点

**包**：`type:"module"` 必填；`cortico.kind:"world"`；`cortico.api` 必须等于 `EXTENSION_API_VERSIONS.world=6`（上一版契约只 warning 并适配，`check:extension` 仍判失败）（manifest.ts:23-27,105-140）。`keywords:["cortico-world"]` 缺了只 warning（:167-171）；`webUiClient/webUiStyle` 须是包内相对 `.js`/`.css`（:142-159）。旧包 `api:6`、无 describe，合规。

**装载**：读 `extensions/package.json` 的 dependencies，从 `extensions/node_modules/<名>/` 按 `exports→module→main→index.js` import 默认导出；id 与内建/先前扩展冲突即拒装（extensions.ts:115-136,272-274,338-359）。ESM 缓存→装完必须重启（README:60-70）。

**默认导出**：硬校验只有 `id/label/defaults()/create()`（extensions.ts:138-145）；完整形状另含 `preflight?/configOptions?/describe?`（world.ts:51-71）。

**时机**：`create(ctx)` 装配期对每个定义都调一次（含未启用），零配置不得抛（world.ts:226-231；dry-mount.ts:150）；`start(host)` 挂载时调，抛错=移出挂载表并回滚 enabled（core.ts:618-641,662-676）；`stop()` 死线 20s（core.ts:49,687）；`webUi(language)` 每请求现算、不缓存、不影响模型文本（types.ts:913-917）；`envPromptVars()` 每次前缀重建/交接都调（:905-910）；`tools()` 随工具表反复调，须无副作用。

**tag / barrierAfter / endsTurn**：`ToolTag` 是封闭枚举 read|write|speak|act|flow|snapshot（types.ts:189-199）；空数组告警，且按标签选工具永远选不到它（dry-mount.ts:184-185；core.ts:243-244）——旧世界全用 `['game']`（O:1206 等），不在枚举内。`barrierAfter`：模型读到本工具结果后才可继续，同一输出里其后的调用直接记"未执行"（types.ts:264-268；loop.ts:1410-1413）。`endsTurn`：执行完结束本次唤醒、不再调下一轮模型，期间事件退回总线（types.ts:269-273；loop.ts:1446,1479）——动作类工具只该 barrierAfter。长阻塞工具加 `interruptible` 并查 `ctx.signal`（:274-278）。

**config**：`ConfigGroup{id,owner:"world:<id>",schema:{type:"object",title,properties}}`（config-schema.ts:50-64）；字段只有 integer/number/boolean/string(enum)/二元 array，另有 x-hot/x-scale/x-suffix/x-options/x-path/x-warning（:1-48）；提交里未声明的键整体拒绝（:134-153）。owner 必须 `world:<id>`，每个路径必须在 `worlds.<id>.` 段内且 `defaults()` 有同名项，否则 dry-mount 判 failure（dry-mount.ts:47-62,216）。

**envPrompt + promptDocs(vars)**：模板是 `webUi().promptDocs` 里 `role:'envPrompt'` 那条（`path` 用 `fileURLToPath`），占位符 `{{name}}` / `{{name | 缺省}}`，名字限 `[\w.]`（types.ts:782-818；template.ts:1-9）。`vars` 只是给 WebUI 的声明，值由 `envPromptVars()` 现给；返回 `null`=整段不出现，`{}`=保留无变量全文（types.ts:906-908；prefix.ts:82-93）。覆盖顺序 `包/worlds/<id>/ENV_PROMPT.md`→`部署/worlds/<id>/ENV_PROMPT.md`，整份替换（prefix.ts:26-59）。旧 world 没有 envPrompt（O:1521-1523），对前缀零贡献。

**pushEvent**：入参 `{type,ts,source,origin?,text,senderKey?,meta?,tags?,ephemeral?,blobs?}` + `{deliver?,trigger?}`（types.ts:684-687,30-67,622-627）；trigger 五档，外部默认 debounce、内部默认 flush（:598-620）。`origin:"internal"` 只许内部机制或已认证操作员通道（:681-687；console/world.ts:20-26）；`ephemeral` 只在整批都是内部项时才从 session 移除（:62-66）；`tags` 只有 snapshot/speak，决定交接保留（:18-24）。旧 world 把游戏提醒标成 internal+flush（O:1189-1200）。

**panels**：`WorldPanelDecl{id:[a-z0-9-],title,getMethods?}`（types.ts:773-780）+ `invoke(panel,method,args)`/`stream(panel,socket)`（:850-859）；HTTP `/api/webui/providers/<page>/panels/<panel>/<method>`、WS 同构（webui-protocol.ts:440-458）；声明 panels 却无 `cortico.webUiClient` 只是 warning（dry-mount.ts:209-211）；bundle URL 服务端分配（manifest.ts:190-213）。

**describe.json**：不必需。manifest 可选、运行时不读；只有 `check:extension` 要求有，市场/索引只读它（manifest.ts:161-162；README:27-37）。

**装本地包**：目录 `<repoRoot>/extensions`，可用 `CORTICO_EXTENSIONS_DIR` 覆盖（extensions.ts:98-103）；扩展页安装时会补一个空 `pnpm-workspace.yaml` 让它自成工作区根（:436-443；README:94-95），手动装也照做，别并进外层 workspace。做法：`extensions/package.json` 的 dependencies 写 `"cortico-world-x":"link:D:/…"`，在该目录 `pnpm install`/`pnpm add link:…`，让 `node_modules/` 出符号链接，重启生效。代码里 `import … from 'cortico/world.ts'` 拿到框架同一实例（manifest.ts:36-37；README:72-78）；浏览器侧只许 `import type`。

## B. 旧 world 清单

工具 7 个（O:1202-1519）：`sts2_state`（full/delta 局面）、`sts2_actions`、`sts2_act`（单动作+preflight+重试）、`sts2_plan_turn`（≤10 步）、`sts2_call`（任意 MCP 工具）、`sts2_companion`（/health 的 role/端口）、`sts2_health`；另有死代码 `wait()`（O:824-863），`wait_until_actionable` 没有工具入口。

config 10 项（O:1536-1610）：enabled/url/watch/watchSeconds/target/waitTeammate/promptDebounceSeconds/delta/maxChars/timeoutMs；`fullEveryTurns` 只在 defaults 与代码里（O:1629,843,937），UI 没这个键，MOD-SPEC:17 的"可 UI 配置"不成立；`defaults().enabled:true`（O:1619）违反"定义一律 false"（dry-mount.ts:141-143）。

watch：`setInterval` 3–15s（O:1003-1010），每 tick 拉 state+actions，`promptFor` 算 key 去重+debounce 后推 `sts2.prompt`（O:1012-1104,1134-1201），药水变化拼进正文（:1112-1122），`shouldWaitForTeammate` 用 mod 的 readiness 标志跳过（:1124-1132）；无 in-flight 锁，慢 tick 会叠加。

delta：`buildSnapshot` 只挑人要看字段（O:417-507）→`diffSnapshots` 深差分、数组按 `i` 出 changed/removed（:510-536）→`state()` 首轮或 `forceFull` 给 full，其余给 delta，无变化补 hand/actions（:905-982）。

可删的"补状态"补丁：
1. `cleanText` 去 `res://`、`[tag]`、`{}` 富文本（O:168-176）→ mod 应给纯文本。
2. `HEAVY_KEYS`+`prune`+`render` 减字与截断（O:17,683-709）→ mod 在回 playbook/guidance/原始日志。
3. `compactScene` 删地图节点、滤 `| 0g` 假条目、自算 `selected_indices`、`map.open===false` 就丢（O:286-311），`localizeLine` 把 proceed/skip 翻中文（:326-331）；`actionParam`/`OPTION_ACTIONS`/`optionsForAction` 靠动作名猜参数名与候选（:313-324,379-404）。
4. `assignRefs` 自造 `c1..cN`（O:864-903）——MOD-SPEC:26 也承认 ref 该搬进 mod。
5. `preflight`（O:721-823）：card_id↔index 互转、stale_index 重映射、target 按 target_id 重定位、单目标自动补、blocked/waiting_for 分类。
6. 重试环（O:1244-1279）、`lastFailed` 防重发（:1299,1441-1444）、`may_have_executed`（:1280-1322）、`waitSettled`（:984-1001）、脏快照重读 6 次（:912-924）。
7. `promptFor` 遇 EVENT 直接 return null（O:1013），为规避事件不同步关推送（N3:33）；自造 `waiting_for_map_sync`（O:431-438,723-733）——N3:12 确认非游戏字段，且成死门（N4:30-34）。
8. 沙坑 boss 机制硬编码（O:239-245,471-482）→ 该由 mod 出结构化 risk；`sts2_call` 万能口（O:1350-1366）绕过 schema、token 不可控；handler 用 try/catch 把失败变中文串（O:679,1345）→ 工具记录不会标 failed（types.ts:253-258）。

## C. 踩过的坑

**N1**：A1 `player_choice` 挡住却不给 prompt/候选（N1:7-12；N2:71-74,81-84）→ 重试 4 次后整条 plan 作废、只能人点，"回合开始卡住"的根因；A2 选牌层手牌与 state 的 hand 不是一套（N1:14-18；N2:75,85）→ 按下标出牌会打错；A3 没有"轮到我"通知（N1:20-22）→ 掐时间猜，早一步就 invalid_action；B1 队友回合出牌 `invalid_action`+retryable:false（N1:26-28；N2:58,69）→ 整条 plan 立即失败，与 blocked 语义分裂；B2 昏眩打完断回合、其余牌 `blocked_by_hook`（N1:30-32；N2:68）→ plan 白等 4 轮；B3 目标死：index 没了→`invalid_target` 中止，index 还在→静默打下一只（N1:34-37；N2:59-62）→ 实测伤害甩给血更厚的怪；C1 `choose_rest_option` 的 requires_target 标错（N1:41-42；N2:63）→ MEND 不传 target_index 就报错；C2 options 串场（N1:44-46；N2:64）→ discard_potion 显示地图/商店选项、buy_relic 显示卡牌。

**N2 其余**：plan_turn 第二步 stale_ref（N2:13-14）→ 退回单步，每回合 7–9 次调用（:9）；state 只回 seq+ts（:15）→ 每次多 2–3 次调用；act 回执整份 after（:16）→ token 重；回合翻页 1–2s 脏快照 hand=[]/energy=0/actions=[]（:17）→ 读到空局面；ref 时有时无（:18）；energy/弃牌堆疑似串队友（:19）→ 决策依据错；Mod UI 不显示队友端口（:36）→ 只能靠 /health 猜；end_turn 后 actions 只剩 discard_potion（:37）→ 以为没动作；单人局 end_turn 后 waiting_for 短暂报 teammate（:101）→ 误判；已进 REWARD 仍推"没有可打出的牌"（:102,115）→ 噪音唤醒；换 DLL 必重启游戏、run 回档（:103）→ 联调成本。

**N3**：多步事件两端选项不同→checksum 分岔、NErrorPopup/黑屏（N3:15-24），与 world 无关但污染观测；同 clientId=1 双开只当"同一玩家的第二视图"（:32）→ 永远开不了局；0.11.11 关掉 EVENT 推送（:33）→ 决策点缺一类。

**N4**：投前看不到 host 票、投完才出现（N4:6-12,17-19）→ 等于闭眼选；`waiting_for_map_sync` 是死门：不许投、不说等什么，available 里却列着 choose_map_node（:30-34）。

## D. 建议

1. 工具面 5 个：`view`（read+snapshot, barrierAfter，只给决策必需字段，含 actions）/`do`（act, barrierAfter，一次一动作，参数枚举由 mod 生成，目标一律 target_id）/`plan`（act, barrierAfter，多步+内置 preflight+`skipped` 软失败）/`wait`（read, interruptible，等 `turn_started` 事件而不是 sleep）/`diag`（read，合并 health/role/port/version）。删掉 `call` 万能口与独立 `actions`。
2. env prompt 写"用法"不写数据：哪个工具何时用、`blocked` 与 `error` 的区别、`waiting_for` 各值含义、别按 index 猜、plan 遇 skipped 继续、单人/双人差异、一次 view 一次 do。数值只经 `envPromptVars()` 给（端口/role/是否双人/是否已连），变量名带命名空间（照 console/world.ts:244-246）。
3. watch 要，但改事件驱动：mod 推 `turn_started/player_choice_required/map_ready/reward_available` 时 world 才 push，一次决策点一条、带 `seq`；trigger 用 `debounce`/`piggyback`，别用 `internal`（会并进 user 消息、掉线不补投，console/world.ts:20-26）；轮询只兜底并加 in-flight 锁（旧 setInterval 会叠加，O:1003-1010）。
4. 端口/双客户端：配置只留 `baseUrl`；启动读 `/health` 的 `instance_role/api_port/pid` 决定连谁，别再用 host/companion 这类角色名（实测已是 second_player，N2:79；N3:7）；双开必须不同 clientId（N3:32）；最好让 mod 写含 pid/port/role 的运行文件，读不到就明确报"哪个端口哪个 role 连不上"，别让 UI 与实际不符（N2:36）。
5. 防 token 膨胀靠结构：字段白名单（不是 `HEAVY_KEYS` 黑名单）、牌堆只给计数、hand 只给 i/ref/cost/playable/targets、回执只给 changed、绝不回整份 after（N2:16）；单次回执目标 <1200 字，`maxChars` 只当兜底，>8000 字 Core 会告警（loop.ts:160）。
6. 错误语义：失败就 throw 或 `{failed:true}`（types.ts:253-258），别用中文串伪装成功；"没轮到/选择中/目标没了"给结构化 `reason+retry_after_ms`，执行不确定时给 `may_have_executed`+当前局面。
