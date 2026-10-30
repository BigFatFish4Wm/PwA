# STS2 mod 房间动作实现参考（v0.111.0，据 STS2-PlayAgent 实装）

## 0. 共同骨架

- 入口：`GameActionService.ExecuteAsync(ActionRequest)` → `DispatchAsync(actionName)` 的 switch（`"choose_map_node"`、`"choose_event_option"`、`"open_chest"`、`"choose_treasure_relic"`、`"choose_rest_option"`、`"proceed"` …）。变更动作串行：`ActionExecutionGate.Shared.Acquire(actionName)`，`finally` 释放。
- 屏幕：`ActiveScreenContext.Instance.GetCurrentScreen()` → `IScreenContext`；`GameStateService.ResolveScreen()` 归一（`NMapScreen`/`NMapRoom`→`"MAP"`、`NEventRoom`→`"EVENT"`、`NTreasureRoom`/`NTreasureRoomRelicCollection`→`"CHEST"`、`NRestSiteRoom`→`"REST"`、`NMerchantRoom`/`NMerchantInventory`→`"SHOP"`、无匹配→`"UNKNOWN"`、有 `NModalContainer.Instance.OpenModal`→`"MODAL"`）。
- 每个 handler 固定四段：`CanXxx(screen)` 否则 409 `invalid_action` → 参数缺失 400 `invalid_request` → 索引越界/锁定 409 `invalid_target` → 「点一下」+「带 deadline 轮询到稳定」。稳定判定统一走 `IsStableScreenState`（见 §7）。

## 1. choose_map_node

**读取**

- 屏幕/节点容器：`MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen.Instance`（= `NRun.Instance?.GlobalUi.MapScreen`），要求 `IsVisibleInTree() && IsOpen`（`GameStateService.TryGetMapScreen`，且 `currentScreen` 必须是 `NMapScreen` 或 `NMapRoom`）。
- 节点：`GameStateService.FindDescendants<NMapPoint>(mapScreen)`。`NMapPoint : NButton : NClickableControl : Godot.Control`，用到的成员：`Point`（`MegaCrit.Sts2.Core.Map.MapPoint`）、`State`（`MapPointState`）、`IsEnabled`、`GlobalPosition`、`ForceClick()`。
- 坐标类型：`MapPoint.coord` 是 `MapCoord` 结构体，**字段 `int col; int row`**（不是属性）；`MapPoint.parents`（`HashSet<MapPoint>`）、`MapPoint.Children`、`MapPoint.PointType`（`MapPointType`：`Shop/Treasure/RestSite/Monster/Elite/Boss/Ancient/Unknown`）。`MapPointState`：`None/Travelable/Traveled/Untravelable`。
- 合法下一节点（mod 自己的判据，不是读 `State`）：

```csharp
static bool IsMapNodeClickable(NMapScreen s, NMapPoint n) =>
    n.IsEnabled && !s.IsTraveling && (s.IsTravelEnabled || s.IsDebugTravelEnabled);
// 取回后 .OrderBy(n => n.Point.coord.row).ThenBy(n => n.Point.coord.col)
// option_index 就是该排序后的下标
```

`NMapPoint.IsEnabled` 是缓存，只在 state 变化时刷新；`TravelToMapCoord` 一进来就 `IsTravelEnabled=false` 而下一行节点仍 `IsEnabled`，所以必须同时读屏幕级旅行开关。游戏自身规则在 `NMapScreen.RecalculateTravelability` 里用 `MapTravel.GetTravelablePointsFrom(runState, currentPoint)` 把点置为 `Travelable`（可作替代判据）。

- 可导出/可用的地图数据（`GameStateService.Map.cs`）：`runState.Map`（`ActMap`：`StartingMapPoint/BossMapPoint/SecondBossMapPoint/GetAllMapPoints()/GetRowCount()/GetColumnCount()`）、`runState.CurrentMapCoord`、`runState.CurrentMapPoint`、`runState.VisitedMapCoords`、`runState.CurrentActIndex`、`RunManager.Instance.MapSelectionSynchronizer.MapGenerationCount`、`GetVote(Player)`、`mapScreen.IsTravelEnabled/IsTraveling/IsDebugTravelEnabled`。

**提交**

单人（非投票）：

```csharp
selectedNode.ForceClick();                                  // NClickableControl.ForceClick
await WaitForMapTransitionAsync(TimeSpan.FromSeconds(10), () => roomEntered);
```

`ForceClick` → `NMapPoint.OnRelease` → `NMapScreen.OnMapPointSelectedLocally(point)`。

多人（`isMultiplayerVote = RunManager.Instance.NetService.Type.IsMultiplayer() && runState.Players.Count > 1`，`IsMultiplayer` 是 `NetGameTypeExtensions` 扩展方法）：

```csharp
var mapScreen = NMapScreen.Instance ?? currentScreen as NMapScreen;
mapScreen.OnMapPointSelectedLocally(selectedNode);          // 直接调 public 方法，不点按钮
```

`OnMapPointSelectedLocally` 内部（反编译确认）：`LocalContext.GetMe(_runState)`；若 `PlayerVoteDictionary[me] == point.coord` 则不重复投票（`Players.Count>1` 时改发 `RunManager.Instance.FlavorSynchronizer.SendMapPing(coord)`）；否则 `new MapLocation(_runState.CurrentMapCoord, _runState.CurrentActIndex)` + `MapVote{ coord, mapGenerationCount = MapSelectionSynchronizer.MapGenerationCount }` → `new VoteForMapCoordAction(LocalContext.GetMe(_runState), source, vote)` → `RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(action)`。

完整链路（谁真正移动）：

1. `VoteForMapCoordAction.ExecuteAction()` → `RunManager.Instance.MapSelectionSynchronizer.PlayerVotedForMapCoord(player, source, destination)`（source 不匹配 / generation 过旧则丢弃）→ 写 `_votes[slot]`。
2. 票齐且 `_netService.Type != NetGameType.Client` → `MapSelectionSynchronizer.MoveToMapCoord()`：`_multiplayerMapPointSelection.NextItem(_votes)`（同票数随机）→ `new MoveToMapCoordAction(LocalContext.GetMe(_runState), coord)` → `RequestEnqueue`。
3. `MoveToMapCoordAction.ExecuteAction()` → `NMapScreen.Instance.TravelToMapCoord(coord)` → `IsTraveling=true`、`IsTravelEnabled=false`、动画、`await RunManager.Instance.EnterMapCoord(coord)`。
4. `RunManager.EnterMapCoord` 进入房间 → 触发 `RunManager.Instance.RoomEntered`。

**结论**：不自己 `new` 这两个 Action，也不存在地图移动用的 `MapCmd`（`MegaCrit.Sts2.Core.Commands.MapCmd` 只有 `SetBossEncounter(IRunState, EncounterModel)`）。`RunManager` 的移动入口只有 `EnterMapCoord(MapCoord)`。

**等待**

```csharp
RunManager.Instance.RoomEntered += OnRoomEntered;            // event Action，finally 里 -=
// 轮询 IsMapTransitionStable：
//   (roomEntered 已触发 || runState.CurrentRoom != null && CurrentRoom is not MapRoom)
//   && DoesScreenMatchCurrentRoom(screen, runState.CurrentRoom)
//   && IsStableScreenState(screen, allowMapScreen: false)
```

`DoesScreenMatchCurrentRoom`：`CombatRoom→COMBAT`、`EventRoom→EVENT`、`MerchantRoom→SHOP`、`RestSiteRoom→REST`、`TreasureRoom→CHEST`、`MapRoom→MAP`、其它→非 UNKNOWN 且非 MAP。多人分支换成 `WaitForMultiplayerMapVoteOrTransitionAsync`：房间进入**或** `IsLocalMapVoteRegistered(coord)`（`MapSelectionSynchronizer.GetVote(localPlayer)` 的 row/col 相等）即 `stable=true`，此时 message 为「Map vote submitted. Waiting for other players to finish choosing.」——client 不等房间，只等票落地。

**MP 专属**：只有 host 的 synchronizer 会 `MoveToMapCoord()`；client 永远只投票。host 侧另有补偿（`GameStateService.Map.cs`）：玩家数 >1 且 host、地图打开、`GetVote` 抛 `ArgumentOutOfRangeException`/`IndexOutOfRangeException`、且无人投票、且 key `{CurrentActIndex}:{CurrentMapCoord}` 未修过 → 调 `synchronizer.OnLocationChanged(runState.MapLocation)` 重建票槽（注释：host lobby 可能早于第二玩家创建，不重建会把 host 单票当全票）。

## 2. choose_event_option

- 屏幕：`currentScreen is NEventRoom`（`NEventRoom.Instance`）；`CanChooseEventOption` = 是 `NEventRoom` ∧ `RunManager.Instance.EventSynchronizer.GetLocalEvent() != null` ∧ (`IsFinished` 或 `CurrentOptions.Any(o => !o.IsLocked)`)。
- 模型：`RunManager.Instance.EventSynchronizer.GetLocalEvent()` → `EventSynchronizer.GetEventForPlayer(LocalPlayer)` → `_events[GetPlayerSlotIndex(player)]`。每人一份 mutable 副本，`EventModel.Owner` 是该玩家；`EventSynchronizer.Events` 是全体。
- 选项：`EventModel.CurrentOptions`（`IReadOnlyList<MegaCrit.Sts2.Core.Events.EventOption>`）。`EventOption`：`TextKey`、`Title`/`Description`（`LocString`，需 `eventModel.DynamicVars.AddTo(loc)` 后 `GetFormattedText()`）、`IsLocked`、`IsProceed`、`Relic`、`WasChosen`、`WillKillPlayer(Player)`、`Chosen()`（`Task`）。
- 执行（**void，不 await**）：`RunManager.Instance.EventSynchronizer.ChooseLocalOption(int index)`。内部：`IsShared` → 记本地票 + 发 `VotedForSharedEventOptionMessage{optionIndex, pageIndex}`；非 shared → `ChooseOptionForEvent(LocalPlayer, index)`（`CurrentOptions[index]`，`TaskHelper.RunSafely(option.Chosen())`，写 `CurrentMapPointHistoryEntry.EventChoices`）+ 广播 `OptionIndexChosenMessage{type=Event}`。option 的异步体是 fire-and-forget（`EventSynchronizer.AwaitPendingOptionTasks()` 存在但本 mod 不调用）。
- 完成判定（多步/嵌套页靠这个）：

```csharp
RunManager.Instance.EventSynchronizer.ChooseLocalOption(index);
stable = await WaitForEventOptionTransitionAsync(
    eventModel.Id?.Entry, BuildEventOptionSignature(eventModel), options.Count, TimeSpan.FromSeconds(10));
```

任一成立即完成：屏幕不再是 `NEventRoom`；`GetLocalEvent().Id?.Entry` 变了；`IsFinished`；`CurrentOptions.Count` 变了；`BuildEventOptionSignature` 变了（signature = 每个 option 的 `TextKey:IsLocked:IsProceed:Title:Description` 拼串）。10s 无变化 → `status="pending", stable=false`（不是异常）。

→ **没有"需要更多步"的特殊分支**：下一页只是新的 `CurrentOptions`，调用方再发一次 `choose_event_option`。若某选项打开的是一张独立界面（卡牌选择、bundle、水晶球），后续改用对应动作（`select_deck_card`/`confirm_selection`、`choose_bundle`/`confirm_bundle`、`crystal_*`）。

- 事件已结束（`IsFinished == true`）：只有合成 proceed（`index 0`，`is_proceed=true`）：

```csharp
if (NMapScreen.Instance == null) throw 503;   // NEventRoom.Proceed 同步解引用它
var t = NEventRoom.Proceed();                 // public Task
await WaitForGameTaskAsync(t, 10s);           // TimedOut → ObserveBackgroundTask + pending
stable = await WaitForEventScreenTransitionAsync(10s);   // 屏幕不再是 NEventRoom
```

## 3. Ancient / Neow

- 类型：`MegaCrit.Sts2.Core.Models.Events.Neow : AncientEventModel : EventModel`。**mod 没有古之民专用分支**：它就是事件房间（`EventLayoutType.Ancient`，地图点 `MapPointType.Ancient`），走同一个 `choose_event_option`（`GameDataExportService` 仅标 `type="Ancient"`）。
- 与普通事件的差别（影响实现）：
  1. 选项仍是 `CurrentOptions` 里的 `EventOption`；遗物选项由 `EventOption.FromRelic(relic, event, onChosen, textKey)` 生成，`option.Relic != null` → 报 `has_relic_preview`。**枚举遗物选项 = 遍历 `CurrentOptions` 取 `Relic`**（`relic.Id.Entry` / `relic.Title.GetFormattedText()` / `relic.Rarity`）。`Neow.AllPossibleOptions` 是静态全集（10 个 CurseOptions + 14 个 PositiveOptions + 6 个单独 relic），不是本局选项。
  2. 无 run modifier 时 `GenerateInitialOptions()` 给 3 个：`PositiveOptions` 去互斥后 `UnstableShuffle(Rng).Take(2)` + 1 个随机 CurseOption。
  3. 有 modifier 时只返回 `ModifierOptions[0]` 一个；选完 `OnModifierOptionSelected` 再 `SetEventState(..., ModifierOptions[index+1])` —— 一次一个，正好由 §2 的 signature 变化判定，需要连续调用。
  4. ancient 可"预完成"：`EventModel.SetInitialEventState(isPreFinished)` 只允许 ancient 传 `isPreFinished=true`（`AncientEventModel.StartPreFinished()`），读档时事件可能一开始就 `IsFinished`，只剩 proceed。
  5. 多人坑：`IsShared => false` 的事件消息只带 optionIndex，而 `EventModel.BeginEvent` 的 RNG 播种为 `run.Rng.Seed + (IsShared ? 0 : slotIndex) + hash(Id.Entry)` → 两机选项顺序不同，同 index 选到不同遗物。两个 Harmony prefix 修：`EventRngSync`（patch `EventModel.Rng` setter，`NetGameType.Host/Client` 时替换为 `new Rng(run.Rng.Seed, "coopanion_event")`）、`NeowOptionSync`（patch `Neow.GenerateInitialOptions`，多人时用反射的私有 setter 设为 `new Rng(run.Rng.Seed, "neow_shared")`）。
  6. `EventChoiceSync`：Harmony prefix 抓 `EventSynchronizer.HandleEventOptionChosenMessage` 的 `OptionIndexChosenMessage.optionIndex`，记录对端最新选择，供 host"采纳队友的选择而不是跑另一页"（记录型辅助，不在执行路径上）。

## 4. chest / TreasureRoom

- 判定：`currentScreen is NTreasureRoom`；`CanOpenChest` = `treasureRoom.GetNodeOrNull<NButton>("%Chest")` 有效且 `IsEnabled`。
- 开箱：`chestButton.ForceClick()`；等 `GameStateService.GetTreasureRelicCollection(screen) != null`（10s）。该函数：`currentScreen as NTreasureRoomRelicCollection`，或 `NTreasureRoom.GetNodeOrNull<NTreasureRoomRelicCollection>("%RelicCollection")` 且 `Visible`。
- 枚举遗物：`RunManager.Instance.TreasureRoomRelicSynchronizer.CurrentRelics`（`IReadOnlyList<RelicModel>`，可为 null）。UI 对应 `NTreasureRoomRelicCollection` / `NTreasureRoomRelicHolder{Index, Relic}`（holder 是 `NButton`，点击走 `PickRelic`），但 mod 不走 UI。
- 选择：`CanChooseTreasureRelic` = 集合非空 ∧ `CurrentRelics.Count > 0`；缺 `option_index` → 400；越界 → 409；然后

```csharp
RunManager.Instance.TreasureRoomRelicSynchronizer.PickRelicLocally(option_index);  // 同步 void
stable = await WaitForRelicPickTransitionAsync(TimeSpan.FromSeconds(10));
```

（跳过用 `SkipRelicLocally()`；`CurrentRelics == null`/空 = 空箱 `CompleteWithNoRelics`。）
- 等待（任一）：屏幕离开 `NTreasureRoomRelicCollection` 且回到 `NTreasureRoom` 且 `GetProceedButton(screen) != null`（再等一帧复核）；或 `IsStableScreenState(screen, allowMapScreen: true)`。
- 收尾：`proceed`（`NTreasureRoom.ProceedButton`，`IRoomWithProceedButton`）。

## 5. rest site

- 判定：`currentScreen is NRestSiteRoom`；`CanChooseRestOption` = `RunManager.Instance.RestSiteSynchronizer.GetLocalOptions()` 非空且有 `IsEnabled` 项。
- 枚举：`RestSiteSynchronizer.GetLocalOptions()` → `IReadOnlyList<MegaCrit.Sts2.Core.Entities.RestSite.RestSiteOption>`，字段/属性 `OptionId`（`"HEAL"`/`"SMITH"`/`"MEND"`…）、`IsEnabled`、`Title`/`Description`、`Icon`。UI 侧 `NRestSiteRoom.Options` 是同一列表。`ChooseLocalOption(int)` 返回 `Task<bool>`。
- 目标只在 `OptionId == "MEND"`（忽略大小写）且存在合法目标（其他存活玩家）时需要；`target_index` 空间 `"run.players"`，合法下标 = `runState.Players` 按 `GetPlayerSlotIndex` 排序、`Creature.IsAlive`、排除自己。

```csharp
var task = RunManager.Instance.RestSiteSynchronizer.ChooseLocalOption(index);   // Task<bool>
if (requiresTarget)      stable = await CompleteRestOptionTargetSelectionAsync(task, targetPlayer, 10s);
else if (id == "SMITH")  { ObserveBackgroundResult(task, "choose_rest_option"); stable = await WaitForRestOptionTransitionAsync(10s); }
else                     { /* WaitForGameTaskAsync(task,10s) + ClassifyGameTaskWait + 过渡等待 */ }
```

MEND 的目标选择：

```csharp
var tm = NTargetManager.Instance;                    // 轮询到 IsInSelection == true
var node = NRestSiteRoom.Instance.GetCharacterForPlayer(targetPlayer);
tm.OnNodeHovered(node);                              // public
tm.Call(NTargetManager.MethodName.FinishTargeting, false);   // 私有方法，Godot StringName 调用
await WaitForTaskResultAsync(task, timeout);
```

- 等待（`WaitForRestOptionTransitionAsync`）：屏幕不再是 `NRestSiteRoom`（SMITH → 卡牌选择）→ true；`restSiteRoom.ProceedButton` 可用（HEAL 后）→ true；`GetLocalOptions()` 空或全 `!IsEnabled` → `restSiteRoom.Call(NRestSiteRoom.MethodName.ShowProceedButton)`（私有，Godot Call）+ `ActiveScreenContext.Instance.Update()` + 等一帧，再看 proceed 是否可用。
- 收尾：`proceed`。

## 6. 通用 proceed / continue / 离开房间

```csharp
if (!GameStateService.CanProceed(screen)) throw 409;             // 排除 NRewardsScreen / NCardRewardSelectionScreen
var b = GameStateService.GetProceedButton(screen) ?? throw 503;  // NProceedButton
b.ForceClick();
stable = await WaitForProceedTransitionAsync(screen, TimeSpan.FromSeconds(10));
// IsProceedStable = 屏幕引用已变 && IsStableScreenState(current, allowMapScreen: true)
```

`GetProceedButton` 解析顺序：`NRewardsScreen` → `GetRewardProceedButton`；`IRoomWithProceedButton`（`NRestSiteRoom`、`NTreasureRoom` 等）→ `.ProceedButton`；否则 `FindDescendants<NProceedButton>().FirstOrDefault(usable)`。usable = 实例有效 ∧ `IsEnabled` ∧ `IsVisibleInTree()`。

其它房间的原生离开入口：事件 `NEventRoom.Proceed()`（Task）；奖励界面 proceed 按钮，卡死兜底才用 `RunManager.Instance.ProceedFromTerminalRewardsScreen()`；存档退出用 `NPauseMenu.CloseToMenu()`（反射私有）。

## 7. 等待机制

- 帧原语：`GameThread.WaitForNextFrameAsync()` = `await game.ToSignal(tree, SceneTree.SignalName.ProcessFrame)`，外包 `Task.WhenAny(frame, Task.Delay(50))`（窗口被遮挡不发帧时防死等）；`NGame`/`SceneTree` 无效时退化为 `Task.Delay(16)`；非游戏线程时用捕获的 `SynchronizationContext.Post` 回主线程。
- 统一形状：`deadline = DateTime.UtcNow + timeout` → `while (DateTime.UtcNow < deadline) { await WaitForNextFrameAsync(); if (pred) return true; }` → 循环外再判一次。
- 轮询对象：`GetCurrentScreen()` 的类型、`RunManager.Instance.DebugOnlyGetState()` 的 `CurrentRoom`/`CurrentMapCoord`、`EventSynchronizer.GetLocalEvent()` 的 `IsFinished`/`CurrentOptions`、`RestSiteSynchronizer.GetLocalOptions()`、`GetTreasureRelicCollection()` 是否出现、`NProceedButton.IsEnabled`、`MapSelectionSynchronizer.GetVote()`。
- 游戏 Task：`WaitForGameTaskAsync(task, timeout)` = `Task.WhenAny(task, Task.Delay(timeout))`，超时返回 null；`GameTaskWaitPolicy.Classify(taskCompleted, taskFaulted, deadlineReached)` → `Completed/TimedOut/Failed`；`TimedOut` → `ObserveBackgroundTask`（后台等它，异常只记日志）+ 响应 `pending/stable=false`；`Failed` → 409 并带上任务异常类型与消息。
- 超时值：地图/事件/宝箱/休息/proceed 均 10s；save&quit 20s；game over summary 60s；菜单类 5–15s。
- `IsStableScreenState`：`ResolveScreen != "UNKNOWN"`；`COMBAT` 需 `NCombatRoom.Mode == CombatRoomMode.ActiveCombat && CombatManager.Instance.IsInProgress && !IsOverOrEnding && IsPlayerActionPhase(DebugOnlyGetState()) && !PlayerActionsDisabled`；`MAP` 需 `allowMapScreen && currentScreen is NMapScreen && !IsTraveling`；其余屏直接 true。

## 8. 反射 / Harmony 清单（R = C# 反射私有成员，G = Godot `Call(StringName)` 打私有方法）

- R `typeof(NPauseMenu).GetMethod("CloseToMenu")`、`GetField("_saveAndQuitButton")`（save_and_quit）；`typeof(NDevConsole)` `_devConsole`；`typeof(NCrystalSphereScreen)` `_entity`；`typeof(NMultiplayerSubmenu)` `FastHost`/`StartLoad`；`typeof(EventModel).GetProperty("Rng").GetSetMethod(true)`（NeowOptionSync）。
- G `NRestSiteRoom.ShowProceedButton`、`NTargetManager.FinishTargeting`、`NChooseABundleSelectionScreen.OnBundleClicked`、`NRewardsScreen.TryEnableProceedButton`。
- Harmony 诊断（不改返回值，只写 godot.log）：`NMapScreen.OnMapPointSelectedLocally`、`NMapScreen.TravelToMapCoord`、`RunManager.EnterMapCoord`、`MoveToMapCoordAction.ExecuteAction`(protected)、`PlayerChoiceSynchronizer.ReserveChoiceId/SyncLocalChoice/WaitForRemoteChoice`、`EventSynchronizer.ChooseLocalOption/BeginEvent/HandleEventOptionChosenMessage`。
- Harmony 生效型：`EventModel.set_Rng`、`Neow.GenerateInitialOptions`（见 §3）。
