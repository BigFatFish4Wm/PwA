using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Runs;
using PwA.Server;

namespace PwA.Game;

/// <summary>一个动作的执行结果。completed = 局面已经落到新状态；pending = 已接受但还在过场。</summary>
internal sealed record ActionResult(string Status, JsonObject? Extra = null);

/// <summary>所有原子动作。只在游戏线程上调用（由 GameApi 包一层 GameThread）。</summary>
internal static class ActionService
{
    private static readonly TimeSpan PlayTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RoomTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PotionTimeout = TimeSpan.FromSeconds(5);

    /// <summary>同一回合只允许入队一次结束回合；重复入队会把游戏的回合状态机卡死（已踩过）。</summary>
    private static int _lastEndTurnRound = -1;
    private static int _lastEndTurnTurn = -1;
    private static DateTime _lastEndTurnAt = DateTime.MinValue;

    // 同一个动作 + 同样的参数，在局面没有变化的情况下重复到达（客户端重试/自动推进重复），
    // 一律拒绝：事件效果、出牌都是「提交一次就结算一次」。
    private static string _lastActionKey = string.Empty;
    private static DateTime _lastActionAt = DateTime.MinValue;
    private static string _lastActionSignature = string.Empty;

    public static async Task<ActionResult> ExecuteAsync(string action, JsonElement args)
    {
        var key = action + "|" + (args.ValueKind == JsonValueKind.Undefined ? "{}" : args.GetRawText());
        var signature = Signature();
        if (string.Equals(key, _lastActionKey, StringComparison.Ordinal)
            && string.Equals(signature, _lastActionSignature, StringComparison.Ordinal)
            && (DateTime.UtcNow - _lastActionAt).TotalMilliseconds < 1200)
        {
            throw ApiException.Conflict("duplicate", "同一个动作刚刚提交过、而且局面数值完全没有变化，已拒绝重复执行。", new
            {
                action,
                hint = "先 sts2_view 确认结果；如果确实是连续操作（例如奖励界面连领两项），局面会先变化再重试",
            }, retryable: true);
        }

        _lastActionKey = key;
        _lastActionAt = DateTime.UtcNow;
        Log.Info($"[PwA.Act] {action} {Truncate(key, 160)}");

        try
        {
            var executed = await DispatchAsync(action, args);
            _lastActionSignature = Signature();
            return executed;
        }
        catch
        {
            _lastActionSignature = Signature();
            throw;
        }
    }

    private static async Task<ActionResult> DispatchAsync(string action, JsonElement args)
    {
        return action switch
        {
            "play_card" => await PlayCardAsync(args),
            "end_turn" => await EndTurnAsync(),
            "use_potion" => await UsePotionAsync(args),
            "discard_potion" => await DiscardPotionAsync(args),
            "choose_map_node" => await ChooseMapNodeAsync(args),
            "choose_event_option" => await ChooseEventOptionAsync(args),
            "open_chest" => await OpenChestAsync(),
            "choose_treasure_relic" => await ChooseTreasureRelicAsync(args),
            "choose_rest_option" => await ChooseRestOptionAsync(args),
            "claim_reward" => await ClaimRewardAsync(args),
            "choose_reward_card" => await ChooseRewardCardAsync(args),
            "skip_reward_cards" => await SkipRewardCardsAsync(),
            "buy_card" => await BuyAsync(args, ShopSlot.Card),
            "buy_relic" => await BuyAsync(args, ShopSlot.Relic),
            "buy_potion" => await BuyAsync(args, ShopSlot.Potion),
            "remove_card_at_shop" => await RemoveCardAtShopAsync(),
            "open_shop_inventory" => await OpenShopAsync(),
            "close_shop_inventory" => await CloseShopAsync(),
            "proceed" => await ProceedAsync(),
            "select_card" => await SelectCardAsync(args),
            "choose_bundle" => await ChooseBundleAsync(args),
            "choose_relic" => await ChooseRelicAsync(args),
            "skip_selection" => await SkipSelectionAsync(),
            "confirm_selection" => await ConfirmSelectionAsync(),
            "choose_character" => await ChooseCharacterAsync(args),
            "embark" => await EmbarkAsync(),
            "continue_run" => await ContinueRunAsync(),
            "open_singleplayer" => await OpenSingleplayerAsync(),
            "start_standard_run" => await StartStandardRunAsync(),
            "back" => await BackAsync(),
            "confirm_modal" => await ModalAsync(confirm: true),
            "dismiss_modal" => await ModalAsync(confirm: false),
            "continue_game_over" => await ContinueGameOverAsync(),
            "inspect_tree" => InspectTree(args),
            "click_node" => await ClickNodeAsync(args),
            _ => throw ApiException.BadRequest("unknown_action", $"不认识的动作 {action}。"),
        };
    }

    // ------------------------------------------------------------- 战斗

    private static async Task<ActionResult> PlayCardAsync(JsonElement args)
    {
        var combat = CombatManager.Instance;
        var state = combat.DebugOnlyGetState() ?? throw ApiException.Unavailable("state_unavailable", "不在战斗里。");
        var me = LocalContext.GetMe(state) ?? throw ApiException.Unavailable("state_unavailable", "找不到本地玩家。");

        if (!combat.IsPartOfPlayerTurn(me))
        {
            throw ApiException.Conflict("blocked", "还没轮到你。", new { waiting_for = "turn", retry_after_ms = 800 }, retryable: true);
        }

        if (GameNodes.OpenModal() != null)
        {
            throw ApiException.Conflict("modal_open", "有个弹窗挡着，先处理它。", retryable: true);
        }

        var hand = me.PlayerCombatState?.Hand.Cards.ToList()
            ?? throw ApiException.Unavailable("state_unavailable", "手牌不可用。");

        var card = ResolveHandCard(args, hand);
        var target = ResolveCardTarget(args, card, state, me);

        if (!card.TryManualPlay(target))
        {
            throw ApiException.Conflict("unplayable", "这张牌现在打不出去。", new { card = card.Id.Entry, playable = card.CanPlay() });
        }

        var outcome = await WaitForPlayAsync(card);
        var extra = new JsonObject
        {
            ["card"] = card.Id.Entry,
            ["ref"] = CardRefs.RefOf(card),
        };

        if (outcome == "selection")
        {
            extra["needs_selection"] = true;
        }

        return new ActionResult(outcome == "timeout" ? "pending" : "completed", extra);
    }

    private static CardModel ResolveHandCard(JsonElement args, List<CardModel> hand)
    {
        var reference = Args.Str(args, "card_ref") ?? Args.Str(args, "ref");
        if (!string.IsNullOrWhiteSpace(reference))
        {
            var card = CardRefs.Resolve(reference);
            if (card != null && hand.Contains(card))
            {
                return card;
            }

            throw ApiException.Conflict("stale_ref", "这个 ref 不在当前手牌里（换回合后 ref 会重编号）。先 sts2_view 拿新 ref。", new { card_ref = reference });
        }

        var index = Args.Int(args, "card_index") ?? Args.Int(args, "index");
        if (index != null)
        {
            if (index < 0 || index >= hand.Count)
            {
                throw ApiException.Conflict("invalid_target", "card_index 越界。", new { hand_count = hand.Count });
            }

            return hand[index.Value];
        }

        var id = Args.Str(args, "card_id");
        if (!string.IsNullOrWhiteSpace(id))
        {
            var card = hand.FirstOrDefault(c => string.Equals(c.Id.Entry, id, StringComparison.OrdinalIgnoreCase));
            if (card != null)
            {
                return card;
            }

            throw ApiException.Conflict("invalid_target", "手牌里没有这张牌。", new { card_id = id });
        }

        throw ApiException.BadRequest("invalid_request", "play_card 需要 card_ref 或 card_index。");
    }

    private static Creature? ResolveCardTarget(JsonElement args, CardModel card, CombatState state, Player me)
    {
        var targetType = card.TargetType;
        if (targetType is not (TargetType.AnyEnemy or TargetType.AnyAlly))
        {
            return null;
        }

        if (targetType == TargetType.AnyEnemy)
        {
            var byId = Args.Int(args, "target_id");
            if (byId is { } wantedId)
            {
                var matched = state.Enemies.FirstOrDefault(enemy => enemy.CombatId == (uint)wantedId);
                if (matched == null)
                {
                    throw ApiException.Conflict("target_gone", "target_id 对应的敌人已经不在了。", new { target_id = wantedId }, retryable: true);
                }

                if (!matched.IsAlive || !matched.IsHittable)
                {
                    throw ApiException.Conflict("target_gone", "这个目标已经不能打了。", new { target_id = wantedId }, retryable: true);
                }

                return matched;
            }

            var index = Args.Int(args, "target_index") ?? Args.Int(args, "target");
            if (index == null)
            {
                var enemies = state.Enemies;
                var alive = Enumerable.Range(0, enemies.Count).Where(i => enemies[i].IsAlive && enemies[i].IsHittable).ToArray();
                if (alive.Length == 1)
                {
                    return enemies[alive[0]];
                }

                throw ApiException.Conflict("needs_target", "这张牌需要 target_index。", new { targets = alive });
            }

            if (index < 0 || index >= state.Enemies.Count)
            {
                throw ApiException.Conflict("invalid_target", "target_index 越界。", new { enemy_count = state.Enemies.Count });
            }

            var enemy = state.Enemies[index.Value];
            if (!enemy.IsAlive || !enemy.IsHittable)
            {
                throw ApiException.Conflict("target_gone", "这个目标已经不能打了。", new { target_index = index }, retryable: true);
            }

            return enemy;
        }

        var runState = RunManager.Instance.DebugOnlyGetState()
            ?? throw ApiException.Unavailable("state_unavailable", "没有进行中的局。");
        var index2 = Args.Int(args, "target_index") ?? Args.Int(args, "target");
        if (index2 == null)
        {
            throw ApiException.Conflict("needs_target", "这张牌需要 target_index（指队友）。");
        }

        var ordered = runState.Players.OrderBy(player => SafeSlot(runState, player)).ToList();
        if (index2 < 0 || index2 >= ordered.Count)
        {
            throw ApiException.Conflict("invalid_target", "target_index 越界。", new { player_count = ordered.Count });
        }

        var targetPlayer = ordered[index2.Value];
        if (targetPlayer.Creature is null || !targetPlayer.Creature.IsAlive || LocalContext.IsMe(targetPlayer))
        {
            throw ApiException.Conflict("invalid_target", "这个队友现在不能被指定。", new { target_index = index2 });
        }

        return targetPlayer.Creature;
    }

    private static async Task<string> WaitForPlayAsync(CardModel card)
    {
        var deadline = DateTime.UtcNow + PlayTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await GameThread.WaitForNextFrameAsync();

            if (NCombatRoom.Instance?.Ui?.Hand is { } hand && hand.IsInCardSelection)
            {
                return "selection";
            }

            var combat = CombatManager.Instance;
            if (combat.EndingPlayerTurnPhaseOne || combat.EndingPlayerTurnPhaseTwo)
            {
                return "settled";
            }

            if (!combat.IsInProgress)
            {
                return "settled";
            }

            if (card.Pile?.Type != PileType.Hand)
            {
                return "settled";
            }
        }

        return "timeout";
    }

    private static async Task<ActionResult> EndTurnAsync()
    {
        var combat = CombatManager.Instance;
        var state = combat.DebugOnlyGetState() ?? throw ApiException.Unavailable("state_unavailable", "不在战斗里。");
        var me = LocalContext.GetMe(state) ?? throw ApiException.Unavailable("state_unavailable", "找不到本地玩家。");

        if (combat.IsPaused)
        {
            combat.Unpause();
        }

        if (combat.EndingPlayerTurnPhaseOne || combat.EndingPlayerTurnPhaseTwo)
        {
            return new ActionResult("completed", new JsonObject { ["already"] = true, ["ending"] = true });
        }

        if (!combat.IsPartOfPlayerTurn(me))
        {
            throw ApiException.Conflict("blocked", "还没轮到你（或回合正在结束）。", new { retry_after_ms = 600, waiting_for = "turn" }, retryable: true);
        }

        // 已经按过就别再按：再按一次会入队 UndoEndPlayerTurnAction，把结束回合撤销掉。
        if (combat.IsPlayerReadyToEndTurn(me))
        {
            return new ActionResult("completed", new JsonObject { ["already"] = true });
        }

        // 手动 new EndPlayerTurnAction 直接入队在单人局会把回合状态机卡死（已踩过两次），
        // 所以走和玩家点击完全相同的按钮路径：DebugPress → 等长按 → CallReleaseLogic → DebugRelease。
        var room = GameNodes.FindActiveCombatRoom()
            ?? throw ApiException.Unavailable("state_unavailable", "找不到战斗界面。");
        var button = room.Ui?.EndTurnButton;
        if (button == null || !GodotObject.IsInstanceValid(button))
        {
            throw ApiException.Unavailable("ui_missing", "找不到结束回合按钮。");
        }

        if (!button.IsEnabled)
        {
            throw ApiException.Conflict("blocked", "结束回合按钮现在不可用。", new { retry_after_ms = 500 }, retryable: true);
        }

        var roundBefore = state.RoundNumber;
        button.DebugPress();
        await WaitForEndTurnLongPressAsync(button);
        button.CallReleaseLogic();
        button.DebugRelease();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);
        while (DateTime.UtcNow < deadline)
        {
            await GameThread.WaitForNextFrameAsync();
            var now = combat.DebugOnlyGetState();
            if (!combat.IsInProgress || now == null)
            {
                return new ActionResult("completed", new JsonObject { ["combat_over"] = true });
            }

            if (now.RoundNumber != roundBefore)
            {
                return new ActionResult("completed", new JsonObject { ["turn"] = now.RoundNumber });
            }

            if (now.CurrentSide != CombatSide.Player)
            {
                return new ActionResult("completed", new JsonObject { ["side"] = now.CurrentSide.ToString() });
            }

            if (!combat.IsPartOfPlayerTurn(me) && combat.IsPlayerReadyToEndTurn(me))
            {
                return new ActionResult("completed", new JsonObject { ["waiting_for_teammate"] = GameNodes.IsMultiplayer() });
            }
        }

        return new ActionResult("pending");
    }

    /// <summary>按钮可能要求长按；按游戏配置的时长按住再放。</summary>
    private static async Task WaitForEndTurnLongPressAsync(NEndTurnButton button)
    {
        try
        {
            var barField = typeof(NEndTurnButton).GetField("_longPressBar", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (barField?.GetValue(button) is not NEndTurnLongPressBar bar)
            {
                return;
            }

            var enabledField = typeof(NEndTurnLongPressBar).GetField("_enabled", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (enabledField?.GetValue(bar) is not true)
            {
                return;
            }

            var durationField = typeof(NEndTurnLongPressBar).GetField("_longPressDuration", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            var duration = durationField?.GetValue(null) is double seconds ? seconds : 0.5;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Max(0.05, duration + 0.1));
            while (DateTime.UtcNow < deadline)
            {
                await GameThread.WaitForNextFrameAsync();
            }
        }
        catch
        {
            // 反射不到就当一个普通点击。
        }
    }

    private static async Task<ActionResult> UsePotionAsync(JsonElement args)
    {
        var runState = RunManager.Instance.DebugOnlyGetState() ?? throw ApiException.Unavailable("state_unavailable", "没有进行中的局。");
        var me = LocalContext.GetMe(runState) ?? throw ApiException.Unavailable("state_unavailable", "找不到本地玩家。");
        var slot = Args.Int(args, "slot") ?? Args.Int(args, "option_index")
            ?? throw ApiException.BadRequest("invalid_request", "use_potion 需要 slot。");

        if (slot < 0 || slot >= me.PotionSlots.Count)
        {
            throw ApiException.Conflict("invalid_target", "slot 越界。", new { slots = me.PotionSlots.Count });
        }

        var potion = me.PotionSlots[slot] ?? throw ApiException.Conflict("invalid_target", "这个槽是空的。", new { slot });
        if (potion.IsQueued)
        {
            throw ApiException.Conflict("busy", "这瓶药水已经在队列里了。", retryable: true);
        }

        var combat = CombatManager.Instance.DebugOnlyGetState();
        var target = ResolvePotionTarget(args, potion, me, combat);
        var beforeFingerprint = CombatFingerprint();
        potion.EnqueueManualUse(target);

        var deadline = DateTime.UtcNow + PotionTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await GameThread.WaitForNextFrameAsync();
            if (HasSelectionScreen())
            {
                return new ActionResult("completed", new JsonObject { ["needs_selection"] = true });
            }

            var slots = me.PotionSlots;
            if (potion.HasBeenRemovedFromState || slot >= slots.Count || !ReferenceEquals(slots[slot], potion))
            {
                // 药水效果可能延后一拍才弹出选牌界面（如 PowerPotion），再等几帧确认。
                for (var i = 0; i < 8; i++)
                {
                    await GameThread.WaitForNextFrameAsync();
                    if (HasSelectionScreen())
                    {
                        return new ActionResult("completed", new JsonObject { ["slot"] = slot, ["needs_selection"] = true });
                    }
                }

                // 伤害/格挡/能力也是异步结算的：等战斗数值真的变了再回执，最多 2s。
                var damageDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
                while (DateTime.UtcNow < damageDeadline)
                {
                    if (HasSelectionScreen())
                    {
                        return new ActionResult("completed", new JsonObject { ["slot"] = slot, ["needs_selection"] = true });
                    }

                    if (!string.Equals(CombatFingerprint(), beforeFingerprint, StringComparison.Ordinal))
                    {
                        break;
                    }

                    await Task.Delay(50);
                }

                return new ActionResult("completed", new JsonObject { ["slot"] = slot });
            }
        }

        return new ActionResult("pending");
    }

    /// <summary>战斗数值指纹：血量/格挡/能力数量。用来判断药水这类异步结算有没有真正落地。</summary>
    private static string CombatFingerprint()
    {
        try
        {
            var state = CombatManager.Instance.DebugOnlyGetState();
            if (state == null)
            {
                return "none";
            }

            var sb = new System.Text.StringBuilder(64);
            foreach (var player in state.Players)
            {
                sb.Append(player.Creature.CurrentHp).Append('/')
                  .Append(player.Creature.Block).Append(':')
                  .Append(player.Creature.Powers.Count).Append(';');
            }

            foreach (var enemy in state.Enemies)
            {
                sb.Append(enemy.CurrentHp).Append('/')
                  .Append(enemy.Block).Append(':')
                  .Append(enemy.Powers.Count).Append(',');
            }

            return sb.ToString();
        }
        catch
        {
            return "err";
        }
    }

    private static Creature? ResolvePotionTarget(JsonElement args, PotionModel potion, Player me, CombatState? combat)
    {
        var targetType = potion.TargetType;
        var index = Args.Int(args, "target_index") ?? Args.Int(args, "target");

        if (targetType == TargetType.AnyEnemy)
        {
            if (combat == null)
            {
                throw ApiException.Unavailable("state_unavailable", "不在战斗里，药水没有目标。");
            }

            var byId = Args.Int(args, "target_id");
            if (byId is { } wantedId)
            {
                var matched = combat.Enemies.FirstOrDefault(enemy => enemy.CombatId == (uint)wantedId && enemy.IsHittable);
                if (matched == null)
                {
                    throw ApiException.Conflict("target_gone", "target_id 对应的敌人已经不在了。", new { target_id = wantedId }, retryable: true);
                }

                return matched;
            }

            if (index == null)
            {
                var alive = Enumerable.Range(0, combat.Enemies.Count).Where(i => combat.Enemies[i].IsAlive && combat.Enemies[i].IsHittable).ToArray();
                if (alive.Length == 1)
                {
                    return combat.Enemies[alive[0]];
                }

                throw ApiException.Conflict("needs_target", "这瓶药需要 target_index。", new { targets = alive });
            }

            if (index < 0 || index >= combat.Enemies.Count || !combat.Enemies[index.Value].IsHittable)
            {
                throw ApiException.Conflict("invalid_target", "target_index 越界。", new { enemy_count = combat.Enemies.Count });
            }

            return combat.Enemies[index.Value];
        }

        if (targetType is TargetType.AnyPlayer or TargetType.AnyAlly)
        {
            var runState = RunManager.Instance.DebugOnlyGetState();
            if (runState == null || runState.Players.Count <= 1 || index == null)
            {
                return me.Creature;
            }

            var ordered = runState.Players.OrderBy(player => SafeSlot(runState, player)).ToList();
            if (index < 0 || index >= ordered.Count)
            {
                throw ApiException.Conflict("invalid_target", "target_index 越界。", new { player_count = ordered.Count });
            }

            return ordered[index.Value].Creature;
        }

        if (targetType is TargetType.AllEnemies or TargetType.AllAllies or TargetType.RandomEnemy or TargetType.TargetedNoCreature)
        {
            return null;
        }

        return me.Creature;
    }

    private static async Task<ActionResult> DiscardPotionAsync(JsonElement args)
    {
        var runState = RunManager.Instance.DebugOnlyGetState() ?? throw ApiException.Unavailable("state_unavailable", "没有进行中的局。");
        var me = LocalContext.GetMe(runState) ?? throw ApiException.Unavailable("state_unavailable", "找不到本地玩家。");
        var slot = Args.Int(args, "slot") ?? Args.Int(args, "option_index")
            ?? throw ApiException.BadRequest("invalid_request", "discard_potion 需要 slot。");

        if (slot < 0 || slot >= me.PotionSlots.Count || me.PotionSlots[slot] == null)
        {
            throw ApiException.Conflict("invalid_target", "这个槽没有药水。", new { slot, slots = me.PotionSlots.Count });
        }

        try
        {
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                new DiscardPotionGameAction(me, (uint)slot, CombatManager.Instance.IsInProgress));
        }
        catch (Exception ex)
        {
            throw ApiException.Conflict("discard_rejected", $"弃药水没有被游戏接受：{ex.Message}", retryable: true);
        }

        var deadline = DateTime.UtcNow + PotionTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await GameThread.WaitForNextFrameAsync();
            if (slot >= me.PotionSlots.Count || me.PotionSlots[slot] == null)
            {
                return new ActionResult("completed", new JsonObject { ["slot"] = slot });
            }
        }

        return new ActionResult("pending");
    }

    // ------------------------------------------------------------- 地图

    private static async Task<ActionResult> ChooseMapNodeAsync(JsonElement args)
    {
        var screen = NMapScreen.Instance;
        if (screen == null || !GodotObject.IsInstanceValid(screen) || !screen.IsOpen)
        {
            throw ApiException.Conflict("invalid_action", "地图没有打开。");
        }

        if (screen.IsTraveling)
        {
            throw ApiException.Conflict("blocked", "地图正在移动。", new { retry_after_ms = 500 }, retryable: true);
        }

        StateBuilder.EnsureMapTravelEnabled(screen, RunManager.Instance.DebugOnlyGetState());

        var nodes = GameNodes.FindAll<NMapPoint>(screen)
            .Where(node => GodotObject.IsInstanceValid(node) && node.IsEnabled && node.IsVisibleInTree())
            .OrderBy(node => node.Point.coord.row)
            .ThenBy(node => node.Point.coord.col)
            .ToList();

        if (nodes.Count == 0)
        {
            throw ApiException.Conflict("invalid_action", "现在没有可选的地图节点。");
        }

        var index = Args.Int(args, "index") ?? Args.Int(args, "option_index")
            ?? throw ApiException.BadRequest("invalid_request", "choose_map_node 需要 index。");
        if (index < 0 || index >= nodes.Count)
        {
            throw ApiException.Conflict("invalid_target", "index 越界。", new { options = nodes.Count });
        }

        var node = nodes[index];
        var coord = $"{node.Point.coord.row},{node.Point.coord.col}";

        if (GameNodes.IsMultiplayer())
        {
            screen.OnMapPointSelectedLocally(node);
        }
        else
        {
            node.ForceClick();
        }

        var deadline = DateTime.UtcNow + RoomTimeout;
        var runState = RunManager.Instance.DebugOnlyGetState();
        var me = runState != null ? LocalContext.GetMe(runState) : null;
        while (DateTime.UtcNow < deadline)
        {
            await GameThread.WaitForNextFrameAsync();
            if (GameNodes.ResolveScreen() != Screen.Map)
            {
                return new ActionResult("completed", new JsonObject { ["coord"] = coord });
            }

            if (GameNodes.IsMultiplayer() && me != null && runState != null)
            {
                try
                {
                    var vote = RunManager.Instance.MapSelectionSynchronizer?.GetVote(me);
                    if (vote?.coord is { } voteCoord && $"{voteCoord.row},{voteCoord.col}" == coord)
                    {
                        return new ActionResult("completed", new JsonObject { ["coord"] = coord, ["vote_submitted"] = true });
                    }
                }
                catch
                {
                }
            }
        }

        return new ActionResult("pending", new JsonObject { ["coord"] = coord });
    }

    // ------------------------------------------------------------- 事件

    private static async Task<ActionResult> ChooseEventOptionAsync(JsonElement args)
    {
        if (GameNodes.CurrentScreen() is not NEventRoom)
        {
            throw ApiException.Conflict("invalid_action", "现在不在事件里。");
        }

        var ev = StateBuilder.CurrentEvent() ?? throw ApiException.Unavailable("state_unavailable", "读不到当前事件。");
        var index = Args.Int(args, "index") ?? Args.Int(args, "option_index");

        if (ev.IsFinished)
        {
            await NEventRoom.Proceed();
            await WaitForScreenLeaveAsync(Screen.Event);
            return new ActionResult("completed", new JsonObject { ["proceeded"] = true });
        }

        if (index == null)
        {
            throw ApiException.BadRequest("invalid_request", "choose_event_option 需要 index。");
        }

        var options = ev.CurrentOptions;
        if (options == null || index < 0 || index >= options.Count)
        {
            throw ApiException.Conflict("invalid_target", "index 越界。", new { options = options?.Count ?? 0 });
        }

        if (options[index.Value].IsLocked)
        {
            throw ApiException.Conflict("invalid_action", "这个选项是锁着的。", new { index });
        }

        // 可重复选的选项（真理石板这类）再选一次会再次结算：必须显式 again:true，防止重试/误触把最大生命刷到 1。
        if (options[index.Value].WasChosen && Args.Bool(args, "again") != true)
        {
            throw ApiException.Conflict("repeat_confirm_required", "这个选项已经选过，而且可以重复选；再选一次会再次结算。确认要再选请带 again:true。", new
            {
                index,
                warning = "重复选会再次扣最大生命/继续升级，直到你停手或事件结束",
            }, retryable: false);
        }

        var before = EventSignature(ev);
        RunManager.Instance.EventSynchronizer.ChooseLocalOption(index.Value);

        var deadline = DateTime.UtcNow + RoomTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await GameThread.WaitForNextFrameAsync();
            if (GameNodes.ResolveScreen() != Screen.Event)
            {
                return new ActionResult("completed", new JsonObject { ["left_event"] = true });
            }

            var now = StateBuilder.CurrentEvent();
            if (now == null || EventSignature(now) != before)
            {
                return new ActionResult("completed", new JsonObject
                {
                    ["index"] = index,
                    ["finished"] = now?.IsFinished ?? true,
                    ["options"] = now?.CurrentOptions?.Count ?? 0,
                });
            }
        }

        return new ActionResult("pending", new JsonObject { ["index"] = index });
    }

    /// <summary>
    /// 局面数值指纹：画面、回合、层数、金币、血量、手牌数、敌人数、药水数、奖励项数。
    /// 只有这些都没变，才把「同动作同参数」当成重复提交（奖励界面连领两项会先变金币）。
    /// </summary>
    private static string Signature()
    {
        var sb = new System.Text.StringBuilder(64);
        sb.Append(GameNodes.ResolveScreen()).Append('|').Append(PulseCache.Turn).Append('|').Append(PulseCache.Floor);

        try
        {
            var run = RunManager.Instance.DebugOnlyGetState();
            if (run != null)
            {
                var player = LocalContext.GetMe(run);
                sb.Append('|').Append(player?.Gold.ToString() ?? "-");
                sb.Append('|').Append(player?.PotionSlots?.Count(slot => slot != null).ToString() ?? "-");
                sb.Append('|').Append(player?.Relics?.Count.ToString() ?? "-");
                sb.Append('|').Append(player?.Creature?.CurrentHp ?? 0);
            }
        }
        catch
        {
        }

        try
        {
            var combat = CombatManager.Instance.DebugOnlyGetState();
            if (combat != null)
            {
                sb.Append('|').Append(combat.RoundNumber).Append('|').Append(combat.Enemies.Count);
                var localMe = LocalContext.GetMe(combat);
                if (localMe != null)
                {
                    sb.Append('|').Append(localMe.Creature?.CurrentHp ?? 0);
                    sb.Append('|').Append(localMe.PlayerCombatState?.Hand.Cards.Count ?? 0);
                }
            }
        }
        catch
        {
        }

        try
        {
            var ev = StateBuilder.CurrentEvent();
            if (ev != null)
            {
                sb.Append("|ev:").Append(ev.CurrentOptions?.Count ?? 0).Append(':').Append(ev.IsFinished);
            }
        }
        catch
        {
        }

        return sb.ToString();
    }

    private static string Truncate(string value, int max)
    {
        return value.Length <= max ? value : value.Substring(0, max) + "…";
    }

    private static string EventSignature(EventModel ev)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(ev.Id.Entry).Append('|').Append(ev.IsFinished).Append('|');
        var options = ev.CurrentOptions;
        if (options != null)
        {
            foreach (var option in options)
            {
                builder.Append(option.TextKey).Append(':').Append(option.IsLocked).Append(';');
            }
        }

        return builder.ToString();
    }

    // ------------------------------------------------------------- 宝箱

    private static async Task<ActionResult> OpenChestAsync()
    {
        if (GameNodes.CurrentScreen() is NTreasureRoomRelicCollection)
        {
            return new ActionResult("completed", new JsonObject { ["already_open"] = true });
        }

        var room = FindTreasureRoom() ?? throw ApiException.Conflict("invalid_action", "现在不在宝箱房。");
        var chest = room.GetNodeOrNull<NClickableControl>("%Chest")
            ?? GameNodes.FindAll<NClickableControl>(room)
                .FirstOrDefault(control => GodotObject.IsInstanceValid(control) && control.IsVisibleInTree()
                    && GameNodes.Clean(control.Name).Contains("Chest", StringComparison.OrdinalIgnoreCase));

        if (chest == null)
        {
            throw ApiException.Unavailable("ui_missing", "找不到宝箱按钮。");
        }

        if (!chest.IsEnabled)
        {
            return new ActionResult("completed", new JsonObject { ["already_open"] = true });
        }

        chest.ForceClick();
        var deadline = DateTime.UtcNow + RoomTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await GameThread.WaitForNextFrameAsync();

            // 遗物集合有时是 NTreasureRoom 下面的节点而不是当前 screen，所以两种都看。
            if (GameNodes.CurrentScreen() is NTreasureRoomRelicCollection || RelicCollectionVisible())
            {
                return new ActionResult("completed", new JsonObject { ["opened"] = true });
            }

            if (GodotObject.IsInstanceValid(chest) && !chest.IsEnabled)
            {
                return new ActionResult("completed", new JsonObject { ["opened"] = true });
            }
        }

        return new ActionResult("pending");
    }

    private static bool RelicCollectionVisible()
    {
        try
        {
            var game = MegaCrit.Sts2.Core.Nodes.NGame.Instance;
            if (game == null || !GodotObject.IsInstanceValid(game))
            {
                return false;
            }

            if (GameNodes.FindAll<NTreasureRoomRelicCollection>(game)
                .Any(node => GodotObject.IsInstanceValid(node) && node.IsVisibleInTree()))
            {
                return true;
            }

            var relics = RunManager.Instance.TreasureRoomRelicSynchronizer?.CurrentRelics;
            return relics is { Count: > 0 };
        }
        catch
        {
            return false;
        }
    }

    private static NTreasureRoom? FindTreasureRoom()
    {
        var game = MegaCrit.Sts2.Core.Nodes.NGame.Instance;
        if (game == null || !GodotObject.IsInstanceValid(game))
        {
            return null;
        }

        return GameNodes.FindAll<NTreasureRoom>(game).FirstOrDefault(GodotObject.IsInstanceValid);
    }

    private static async Task<ActionResult> ChooseTreasureRelicAsync(JsonElement args)
    {
        IReadOnlyList<RelicModel>? relics;
        try
        {
            relics = RunManager.Instance.TreasureRoomRelicSynchronizer?.CurrentRelics;
        }
        catch
        {
            relics = null;
        }

        if (relics == null || relics.Count == 0)
        {
            throw ApiException.Conflict("invalid_action", "现在没有宝箱遗物可选。");
        }

        var index = Args.Int(args, "index") ?? Args.Int(args, "option_index")
            ?? throw ApiException.BadRequest("invalid_request", "choose_treasure_relic 需要 index。");
        if (index < 0 || index >= relics.Count)
        {
            throw ApiException.Conflict("invalid_target", "index 越界。", new { options = relics.Count });
        }

        RunManager.Instance.TreasureRoomRelicSynchronizer.PickRelicLocally(index);

        var deadline = DateTime.UtcNow + RoomTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await GameThread.WaitForNextFrameAsync();
            if (GameNodes.ProceedButton() != null || GameNodes.ResolveScreen() != Screen.Chest)
            {
                return new ActionResult("completed", new JsonObject { ["relic"] = relics[index].Id.Entry });
            }
        }

        return new ActionResult("pending");
    }

    // ------------------------------------------------------------- 营火

    private static async Task<ActionResult> ChooseRestOptionAsync(JsonElement args)
    {
        var options = RunManager.Instance.RestSiteSynchronizer?.GetLocalOptions();
        if (options == null || options.Count == 0)
        {
            throw ApiException.Conflict("invalid_action", "现在不在营火，或者没有选项。");
        }

        var index = Args.Int(args, "index") ?? Args.Int(args, "option_index")
            ?? throw ApiException.BadRequest("invalid_request", "choose_rest_option 需要 index。");
        if (index < 0 || index >= options.Count)
        {
            throw ApiException.Conflict("invalid_target", "index 越界。", new { options = options.Count });
        }

        if (!options[index].IsEnabled)
        {
            throw ApiException.Conflict("invalid_action", "这个选项不可用。", new { index });
        }

        var option = options[index];
        var needsTarget = string.Equals(option.OptionId, "MEND", StringComparison.OrdinalIgnoreCase);
        var task = RunManager.Instance.RestSiteSynchronizer.ChooseLocalOption(index);

        if (needsTarget)
        {
            await TryFinishRestTargetAsync(Args.Int(args, "target_index"));
        }

        var completed = await Task.WhenAny(task, Task.Delay(RoomTimeout)) == task;

        // 营火的 HEAL/SMITH 选完以后屏幕仍然停在营火（出现 proceed 按钮），所以不能等屏幕离开；
        // 等「选项消失 / 出现 proceed / 屏幕变了」任一成立就返回。
        var restDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < restDeadline)
        {
            await GameThread.WaitForNextFrameAsync();
            var optionsNow = RunManager.Instance.RestSiteSynchronizer?.GetLocalOptions();
            if (GameNodes.ResolveScreen() != Screen.Rest
                || optionsNow == null || optionsNow.Count == 0
                || GameNodes.ProceedButtonAny() != null)
            {
                break;
            }
        }

        return new ActionResult(completed ? "completed" : "pending", new JsonObject { ["index"] = index, ["id"] = option.OptionId });
    }

    /// <summary>MEND 要选一个队友：等目标选择模式，把焦点放到角色上，再结束选择。</summary>
    private static async Task TryFinishRestTargetAsync(int? targetIndex)
    {
        var room = NRestSiteRoom.Instance;
        var runState = RunManager.Instance.DebugOnlyGetState();
        if (room == null || runState == null)
        {
            return;
        }

        var ordered = runState.Players.OrderBy(player => SafeSlot(runState, player)).ToList();
        var target = targetIndex != null && targetIndex >= 0 && targetIndex < ordered.Count
            ? ordered[targetIndex.Value]
            : ordered.FirstOrDefault(player => player.Creature.IsAlive && !LocalContext.IsMe(player));

        if (target == null)
        {
            return;
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (NTargetManager.Instance?.IsInSelection == true)
            {
                break;
            }

            await GameThread.WaitForNextFrameAsync();
        }

        try
        {
            var node = room.GetCharacterForPlayer(target);
            var manager = NTargetManager.Instance;
            if (node != null && manager != null)
            {
                manager.OnNodeHovered(node);
                if (manager.HasMethod("FinishTargeting"))
                {
                    manager.Call("FinishTargeting", false);
                }
            }
        }
        catch
        {
        }
    }

    // ------------------------------------------------------------- 奖励

    private static async Task<ActionResult> ClaimRewardAsync(JsonElement args)
    {
        var buttons = RewardButtons();
        if (buttons.Count == 0)
        {
            throw ApiException.Conflict("invalid_action", "现在没有可领取的奖励。");
        }

        var index = Args.Int(args, "index") ?? Args.Int(args, "option_index")
            ?? throw ApiException.BadRequest("invalid_request", "claim_reward 需要 index。");
        if (index < 0 || index >= buttons.Count)
        {
            throw ApiException.Conflict("invalid_target", "index 越界。", new { options = buttons.Count });
        }

        var button = buttons[index];
        if (!button.IsEnabled)
        {
            throw ApiException.Conflict("invalid_action", "这个奖励现在领不了。", new { index });
        }

        var before = buttons.Count;
        GameNodes.Press(button);

        var deadline = DateTime.UtcNow + RoomTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await GameThread.WaitForNextFrameAsync();
            var now = RewardButtons();
            if (now.Count != before || GameNodes.ResolveScreen() != Screen.Reward || IsCardRewardScreen())
            {
                return new ActionResult("completed", new JsonObject { ["index"] = index, ["needs_choice"] = IsCardRewardScreen() });
            }
        }

        return new ActionResult("pending", new JsonObject { ["index"] = index });
    }

    private static List<NRewardButton> RewardButtons()
    {
        if (GameNodes.CurrentScreen() is not NRewardsScreen screen)
        {
            return new List<NRewardButton>();
        }

        return GameNodes.FindAll<NRewardButton>(screen)
            .Where(button => GodotObject.IsInstanceValid(button))
            .OrderBy(button => button.GlobalPosition.Y)
            .ThenBy(button => button.GlobalPosition.X)
            .ToList();
    }

    private static bool IsCardRewardScreen() => GameNodes.CurrentScreen() is NCardRewardSelectionScreen;

    private static async Task<ActionResult> ChooseRewardCardAsync(JsonElement args)
    {
        if (GameNodes.CurrentScreen() is not NCardRewardSelectionScreen screen)
        {
            throw ApiException.Conflict("invalid_action", "现在不在选牌奖励界面。");
        }

        var holders = GameNodes.FindAll<NCardHolder>(screen)
            .Where(holder => GodotObject.IsInstanceValid(holder) && holder.CardModel != null && holder.IsVisibleInTree())
            .OrderBy(holder => holder.GlobalPosition.Y)
            .ThenBy(holder => holder.GlobalPosition.X)
            .ToList();

        if (holders.Count == 0)
        {
            throw ApiException.Conflict("invalid_action", "奖励里没有可选卡。");
        }

        var index = Args.Int(args, "index") ?? Args.Int(args, "option_index")
            ?? throw ApiException.BadRequest("invalid_request", "choose_reward_card 需要 index。");
        if (index < 0 || index >= holders.Count)
        {
            throw ApiException.Conflict("invalid_target", "index 越界。", new { options = holders.Count });
        }

        var holder = holders[index];
        var cardId = holder.CardModel?.Id.Entry;
        GameNodes.PressCardHolder(holder);

        var deadline = DateTime.UtcNow + RoomTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await GameThread.WaitForNextFrameAsync();
            if (GameNodes.ResolveScreen() != Screen.Reward || !IsCardRewardScreen())
            {
                return new ActionResult("completed", new JsonObject { ["card"] = cardId });
            }
        }

        return new ActionResult("pending", new JsonObject { ["card"] = cardId });
    }

    private static async Task<ActionResult> SkipRewardCardsAsync()
    {
        if (GameNodes.CurrentScreen() is not NCardRewardSelectionScreen screen)
        {
            throw ApiException.Conflict("invalid_action", "现在不在选牌奖励界面。");
        }

        var alternative = GameNodes.FindAll<NCardRewardAlternativeButton>(screen)
            .FirstOrDefault(button => GodotObject.IsInstanceValid(button) && button.IsVisibleInTree() && button.IsEnabled);
        if (alternative == null)
        {
            throw ApiException.Conflict("invalid_action", "现在不能跳过。");
        }

        alternative.ForceClick();

        var deadline = DateTime.UtcNow + RoomTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await GameThread.WaitForNextFrameAsync();
            if (GameNodes.ResolveScreen() != Screen.Reward || !IsCardRewardScreen())
            {
                return new ActionResult("completed");
            }
        }

        return new ActionResult("pending");
    }

    // ------------------------------------------------------------- 商店

    private enum ShopSlot
    {
        Card,
        Relic,
        Potion,
    }

    private static async Task<ActionResult> BuyAsync(JsonElement args, ShopSlot kind)
    {
        var inventory = ShopInventory();
        var index = Args.Int(args, "index") ?? Args.Int(args, "option_index")
            ?? throw ApiException.BadRequest("invalid_request", "买卡/遗物/药水需要 index。");

        MegaCrit.Sts2.Core.Entities.Merchant.MerchantEntry? entry = kind switch
        {
            ShopSlot.Card => index >= 0 && index < inventory.CharacterCardEntries.Count + inventory.ColorlessCardEntries.Count
                ? (index < inventory.CharacterCardEntries.Count
                    ? inventory.CharacterCardEntries[index]
                    : inventory.ColorlessCardEntries[index - inventory.CharacterCardEntries.Count])
                : null,
            ShopSlot.Relic => index >= 0 && index < inventory.RelicEntries.Count ? inventory.RelicEntries[index] : null,
            ShopSlot.Potion => index >= 0 && index < inventory.PotionEntries.Count ? inventory.PotionEntries[index] : null,
            _ => null,
        };

        if (entry == null)
        {
            throw ApiException.Conflict("invalid_target", "index 越界。");
        }

        if (!entry.IsStocked)
        {
            throw ApiException.Conflict("invalid_action", "这个商品已经卖掉了。", new { index });
        }

        if (!entry.EnoughGold)
        {
            throw ApiException.Conflict("invalid_action", "金币不够。", new { price = entry.Cost });
        }

        var task = entry.OnTryPurchaseWrapper(inventory, false);
        var completed = await Task.WhenAny(task, Task.Delay(RoomTimeout));
        if (completed != task)
        {
            return new ActionResult("pending", new JsonObject { ["index"] = index });
        }

        bool ok;
        try
        {
            ok = await task;
        }
        catch (Exception ex)
        {
            throw ApiException.Conflict("purchase_failed", $"购买失败：{ex.Message}");
        }

        if (!ok)
        {
            throw ApiException.Conflict("purchase_failed", "游戏没有接受这次购买。", new { index });
        }

        return new ActionResult("completed", new JsonObject { ["index"] = index, ["needs_selection"] = HasSelectionScreen() });
    }

    private static MegaCrit.Sts2.Core.Entities.Merchant.MerchantInventory ShopInventory()
    {
        var inventory = NMerchantRoom.Instance?.Inventory?.Inventory;
        if (inventory == null)
        {
            throw ApiException.Conflict("invalid_action", "现在不在商店。");
        }

        return inventory;
    }

    private static async Task<ActionResult> RemoveCardAtShopAsync()
    {
        var inventory = ShopInventory();
        var entry = inventory.CardRemovalEntry;
        if (entry == null || !entry.IsStocked)
        {
            throw ApiException.Conflict("invalid_action", "这里的删牌服务已经用过了。");
        }

        if (!entry.EnoughGold)
        {
            throw ApiException.Conflict("invalid_action", "金币不够删牌。", new { price = entry.Cost });
        }

        var task = entry.OnTryPurchaseWrapper(inventory, false);
        var completed = await Task.WhenAny(task, Task.Delay(RoomTimeout));
        if (completed != task)
        {
            return new ActionResult("pending");
        }

        bool ok;
        try
        {
            ok = await task;
        }
        catch (Exception ex)
        {
            throw ApiException.Conflict("purchase_failed", $"删牌失败：{ex.Message}");
        }

        if (!ok)
        {
            throw ApiException.Conflict("purchase_failed", "游戏没有接受这次删牌。");
        }

        return new ActionResult("completed", new JsonObject { ["needs_selection"] = HasSelectionScreen() });
    }

    private static async Task<ActionResult> OpenShopAsync()
    {
        var room = NMerchantRoom.Instance ?? throw ApiException.Conflict("invalid_action", "现在不在商店。");
        if (room.Inventory?.IsOpen == true)
        {
            return new ActionResult("completed", new JsonObject { ["already_open"] = true });
        }

        room.OpenInventory();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            await GameThread.WaitForNextFrameAsync();
            if (room.Inventory?.IsOpen == true)
            {
                return new ActionResult("completed", new JsonObject { ["opened"] = true });
            }
        }

        return new ActionResult("pending");
    }

    private static async Task<ActionResult> CloseShopAsync()
    {
        var room = NMerchantRoom.Instance ?? throw ApiException.Conflict("invalid_action", "现在不在商店。");
        var inventory = room.Inventory;
        if (inventory?.IsOpen != true)
        {
            return new ActionResult("completed", new JsonObject { ["already_closed"] = true });
        }

        var back = GameNodes.FindAll<NBackButton>(inventory)
            .FirstOrDefault(button => GodotObject.IsInstanceValid(button) && button.IsVisibleInTree() && button.IsEnabled);
        if (back == null)
        {
            throw ApiException.Unavailable("ui_missing", "找不到关闭商店的按钮。");
        }

        back.ForceClick();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            await GameThread.WaitForNextFrameAsync();
            if (inventory.IsOpen != true)
            {
                return new ActionResult("completed");
            }
        }

        return new ActionResult("pending");
    }

    // ------------------------------------------------------------- 通用

    private static async Task<ActionResult> ProceedAsync()
    {
        var before = GameNodes.ResolveScreen();

        // 事件结束后没有 NProceedButton，入口是 NEventRoom.Proceed()。
        if (before == Screen.Event && GameNodes.CurrentScreen() is NEventRoom)
        {
            var ev = StateBuilder.CurrentEvent();
            if (ev?.IsFinished == true)
            {
                await NEventRoom.Proceed();
                await WaitForScreenLeaveAsync(Screen.Event);
                return new ActionResult("completed", new JsonObject { ["screen"] = GameNodes.ResolveScreen() });
            }
        }

        var button = GameNodes.ProceedButton() ?? GameNodes.ProceedButtonAny();

        // 商店：货架没开过时 exit 按钮可能还没建出来，开一次再关掉就会出来。
        if (button == null && before == Screen.Shop && NMerchantRoom.Instance is { } shopRoom)
        {
            try
            {
                if (shopRoom.Inventory?.IsOpen != true)
                {
                    shopRoom.OpenInventory();
                    for (var i = 0; i < 20 && shopRoom.Inventory?.IsOpen != true; i++)
                    {
                        await GameThread.WaitForNextFrameAsync();
                    }
                }

                var back = GameNodes.FindAll<NBackButton>(shopRoom.Inventory)
                    .FirstOrDefault(candidate => GodotObject.IsInstanceValid(candidate) && candidate.IsVisibleInTree() && candidate.IsEnabled);
                if (back != null)
                {
                    back.ForceClick();
                    for (var i = 0; i < 20 && shopRoom.Inventory?.IsOpen == true; i++)
                    {
                        await GameThread.WaitForNextFrameAsync();
                    }
                }
            }
            catch
            {
            }

            button = GameNodes.ProceedButton() ?? GameNodes.ProceedButtonAny();
        }

        // 奖励面板（含 Boss 的「搜刮！」）没有继续按钮时，右下角的「跳过」箭头就是出口。
        if (button == null && before == Screen.Reward && NGame.Instance is { } gameForSkip)
        {
            var skip = GameNodes.FindAll<NClickableControl>(gameForSkip)
                .Where(control => GodotObject.IsInstanceValid(control) && control.IsVisibleInTree() && control.IsEnabled)
                .FirstOrDefault(control => GameNodes.Clean(control.Name).Contains("Skip", StringComparison.OrdinalIgnoreCase));

            if (skip != null)
            {
                skip.ForceClick();
                await WaitForScreenLeaveAsync(Screen.Reward);
                return new ActionResult("completed", new JsonObject { ["skipped"] = true, ["screen"] = GameNodes.ResolveScreen() });
            }
        }

        if (button == null)
        {
            // 营火选完选项后，proceed 按钮由房间自己的 ShowProceedButton 放出来。
            if (before == Screen.Rest && NRestSiteRoom.Instance is { } restRoom && restRoom.HasMethod("ShowProceedButton"))
            {
                restRoom.Call("ShowProceedButton");
                try
                {
                    MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext.ActiveScreenContext.Instance?.Update();
                }
                catch
                {
                }

                for (var i = 0; i < 6 && button == null; i++)
                {
                    await GameThread.WaitForNextFrameAsync();
                    button = GameNodes.ProceedButton();
                }
            }
        }

        if (button == null)
        {
            throw ApiException.Conflict("invalid_action", "现在没有可以点的继续按钮。");
        }

        if (!button.IsEnabled)
        {
            var enableDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (DateTime.UtcNow < enableDeadline && !button.IsEnabled)
            {
                await GameThread.WaitForNextFrameAsync();
            }
        }

        GameNodes.Press(button);

        var deadline = DateTime.UtcNow + RoomTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await GameThread.WaitForNextFrameAsync();
            var now = GameNodes.ResolveScreen();
            if (now != before && now != Screen.Unknown)
            {
                return new ActionResult("completed", new JsonObject { ["screen"] = now });
            }

            if (GameNodes.ProceedButton() == null)
            {
                return new ActionResult("completed", new JsonObject { ["screen"] = now });
            }
        }

        return new ActionResult("pending");
    }

    private static async Task<ActionResult> SelectCardAsync(JsonElement args)
    {
        var holders = GameNodes.SelectionHolders();
        if (holders.Count == 0)
        {
            throw ApiException.Conflict("invalid_action", "现在没有选牌界面。");
        }

        var reference = Args.Str(args, "card_ref") ?? Args.Str(args, "ref");
        var index = Args.Int(args, "index") ?? Args.Int(args, "card_index");

        NCardHolder? holder;
        if (!string.IsNullOrWhiteSpace(reference))
        {
            var card = CardRefs.Resolve(reference);
            holder = holders.FirstOrDefault(candidate => candidate.CardModel != null && ReferenceEquals(candidate.CardModel, card));
            if (holder == null)
            {
                throw ApiException.Conflict("stale_ref", "这个 ref 不在候选里，重新 view 看 selection.cards。", new { card_ref = reference });
            }
        }
        else if (index != null)
        {
            if (index < 0 || index >= holders.Count)
            {
                throw ApiException.Conflict("invalid_target", "index 越界。", new { options = holders.Count });
            }

            holder = holders[index.Value];
        }
        else
        {
            throw ApiException.BadRequest("invalid_request", "select_card 需要 card_ref 或 index。");
        }

        var cardModel = holder.CardModel;

        // 选满就别再点了：网格选牌点多会卡在「超选」状态，confirm 也点不掉。
        try
        {
            var preview = StateBuilder.BuildSelection();
            var maxSelected = preview?["max"]?.GetValue<int?>();
            var selectedCount = preview?["selected_count"]?.GetValue<int>() ?? 0;
            var alreadySelected = preview?["cards"] is JsonArray currentCards
                && currentCards.Any(card => card?["ref"]?.GetValue<string>() == reference && card["selected"]?.GetValue<bool>() == true);
            if (maxSelected is { } max && selectedCount >= max && !alreadySelected)
            {
                throw ApiException.Conflict("selection_full", $"已经选满 {max} 张，先 confirm_selection 再选。", new
                {
                    selected_count = selectedCount,
                    max,
                }, retryable: true);
            }
        }
        catch (ApiException)
        {
            throw;
        }
        catch
        {
        }

        var hand = NCombatRoom.Instance?.Ui?.Hand;
        if (hand != null && hand.IsInCardSelection)
        {
            var method = hand.CurrentMode == NPlayerHand.Mode.UpgradeSelect
                ? NPlayerHand.MethodName.SelectCardInUpgradeMode
                : NPlayerHand.MethodName.SelectCardInSimpleMode;
            hand.Call(method, holder);
            hand.Call(NPlayerHand.MethodName.CheckIfSelectionComplete);
        }
        else
        {
            GameNodes.PressCardHolder(holder);
        }

        await GameThread.WaitForNextFrameAsync();
        await GameThread.WaitForNextFrameAsync();
        var stillSelecting = GameNodes.ResolveScreen() == Screen.CardSelection;
        return new ActionResult("completed", new JsonObject
        {
            ["card"] = cardModel?.Id.Entry,
            ["still_selecting"] = stillSelecting,
            ["selection"] = stillSelecting ? StateBuilder.BuildSelection() : null,
        });
    }

    /// <summary>「选一包卡」：先点卡包，出预览确认就点确认，等界面关闭。</summary>
    private static async Task<ActionResult> ChooseBundleAsync(JsonElement args)
    {
        var screen = GameNodes.BundleScreen()
            ?? throw ApiException.Conflict("invalid_action", "现在没有卡牌包选择界面。");
        var bundles = GameNodes.Bundles(screen);
        if (bundles.Count == 0)
        {
            throw ApiException.Unavailable("ui_missing", "读不到卡牌包。");
        }

        var index = Args.Int(args, "index") ?? Args.Int(args, "option_index")
            ?? throw ApiException.BadRequest("invalid_request", "choose_bundle 需要 index。");
        if (index < 0 || index >= bundles.Count)
        {
            throw ApiException.Conflict("invalid_target", "index 越界。", new { options = bundles.Count });
        }

        var bundle = bundles[index];
        var summary = string.Join(",", (bundle.Bundle ?? Array.Empty<CardModel>()).Select(card => card.Id.Entry));
        var hitbox = bundle.Hitbox;
        if (hitbox != null && GodotObject.IsInstanceValid(hitbox))
        {
            hitbox.ForceClick();
        }
        else
        {
            bundle.EmitSignal(Godot.Control.SignalName.GuiInput, new InputEventMouseButton { Pressed = true, ButtonIndex = MouseButton.Left });
        }

        var deadline = DateTime.UtcNow + RoomTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await GameThread.WaitForNextFrameAsync();

            if (GameNodes.BundleScreen() == null)
            {
                return new ActionResult("completed", new JsonObject { ["index"] = index, ["cards"] = summary });
            }

            // 点开卡包后通常要再确认一次
            var confirm = GameNodes.FindAll<NConfirmButton>(screen)
                .FirstOrDefault(button => GodotObject.IsInstanceValid(button) && button.IsVisibleInTree() && button.IsEnabled);
            if (confirm != null)
            {
                confirm.ForceClick();
                await GameThread.WaitForNextFrameAsync();
                await GameThread.WaitForNextFrameAsync();
                if (GameNodes.BundleScreen() == null)
                {
                    return new ActionResult("completed", new JsonObject { ["index"] = index, ["cards"] = summary, ["confirmed"] = true });
                }
            }
        }

        return new ActionResult("pending", new JsonObject { ["index"] = index, ["cards"] = summary });
    }

    /// <summary>选遗物界面：点第 index 个遗物。</summary>
    private static async Task<ActionResult> ChooseRelicAsync(JsonElement args)
    {
        var found = GameNodes.FindSelectionScreen()
            ?? throw ApiException.Conflict("invalid_action", "现在没有选择界面。");
        if (found.Kind != "relic")
        {
            throw ApiException.Conflict("invalid_action", $"当前选择界面不是选遗物（kind={found.Kind}）。");
        }

        var holders = GameNodes.RelicHolders(found.Screen);
        if (holders.Count == 0)
        {
            throw ApiException.Unavailable("ui_missing", "读不到遗物列表。");
        }

        var index = Args.Int(args, "index") ?? Args.Int(args, "option_index")
            ?? throw ApiException.BadRequest("invalid_request", "choose_relic 需要 index。");
        if (index < 0 || index >= holders.Count)
        {
            throw ApiException.Conflict("invalid_target", "index 越界。", new { options = holders.Count });
        }

        var model = holders[index].Relic?.Model;
        holders[index].ForceClick();

        var deadline = DateTime.UtcNow + RoomTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await GameThread.WaitForNextFrameAsync();
            if (GameNodes.FindSelectionScreen() == null)
            {
                return new ActionResult("completed", new JsonObject { ["relic"] = model?.Id.Entry, ["index"] = index });
            }

            var confirm = GameNodes.FindAll<NConfirmButton>(found.Screen)
                .FirstOrDefault(button => GodotObject.IsInstanceValid(button) && button.IsVisibleInTree() && button.IsEnabled);
            if (confirm != null)
            {
                confirm.ForceClick();
                await GameThread.WaitForNextFrameAsync();
                await GameThread.WaitForNextFrameAsync();
            }
        }

        return new ActionResult("pending", new JsonObject { ["relic"] = model?.Id.Entry, ["index"] = index });
    }

    /// <summary>能跳过的选择（NChoiceSelectionSkipButton）就跳过。</summary>
    private static async Task<ActionResult> SkipSelectionAsync()
    {
        var found = GameNodes.FindSelectionScreen()
            ?? throw ApiException.Conflict("invalid_action", "现在没有选择界面。");
        var skip = GameNodes.SkipButton(found.Screen)
            ?? throw ApiException.Conflict("invalid_action", "这个选择不能跳过。");

        skip.ForceClick();
        var deadline = DateTime.UtcNow + RoomTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await GameThread.WaitForNextFrameAsync();
            if (GameNodes.FindSelectionScreen() == null)
            {
                return new ActionResult("completed", new JsonObject { ["skipped"] = true });
            }
        }

        return new ActionResult("pending", new JsonObject { ["skipped"] = true });
    }

    private static async Task<ActionResult> ConfirmSelectionAsync()
    {
        if (!GameNodes.ConfirmSelection())
        {
            throw ApiException.Conflict("invalid_action", "找不到确认按钮。");
        }

        var deadline = DateTime.UtcNow + RoomTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await GameThread.WaitForNextFrameAsync();
            if (GameNodes.ResolveScreen() != Screen.CardSelection)
            {
                return new ActionResult("completed");
            }
        }

        return new ActionResult("pending", new JsonObject
        {
            ["still_selecting"] = true,
            ["selection"] = StateBuilder.BuildSelection(),
        });
    }

    private static async Task<ActionResult> ChooseCharacterAsync(JsonElement args)
    {
        if (GameNodes.CurrentScreen() is not NCharacterSelectScreen screen)
        {
            throw ApiException.Conflict("invalid_action", "现在不在选角色界面。");
        }

        var buttons = GameNodes.FindAll<NCharacterSelectButton>(screen)
            .Where(button => GodotObject.IsInstanceValid(button) && button.IsVisibleInTree())
            .ToList();
        if (buttons.Count == 0)
        {
            throw ApiException.Conflict("invalid_action", "没有可选角色。");
        }

        var index = Args.Int(args, "index") ?? Args.Int(args, "option_index")
            ?? throw ApiException.BadRequest("invalid_request", "choose_character 需要 index。");
        if (index < 0 || index >= buttons.Count)
        {
            throw ApiException.Conflict("invalid_target", "index 越界。", new { options = buttons.Count });
        }

        var button = buttons[index];
        if (button.IsLocked)
        {
            throw ApiException.Conflict("invalid_action", "这个角色还没解锁。", new { index });
        }

        button.ForceClick();
        await GameThread.WaitForNextFrameAsync();
        return new ActionResult("completed", new JsonObject { ["character"] = button.Character?.Id.Entry });
    }

    private static async Task<ActionResult> EmbarkAsync()
    {
        if (GameNodes.CurrentScreen() is not NCharacterSelectScreen screen)
        {
            throw ApiException.Conflict("invalid_action", "现在不在选角色界面。");
        }

        var confirm = GameNodes.FindAll<NConfirmButton>(screen)
            .FirstOrDefault(button => GodotObject.IsInstanceValid(button) && button.IsVisibleInTree() && button.IsEnabled);
        if (confirm == null)
        {
            throw ApiException.Conflict("invalid_action", "开始按钮现在不可用。");
        }

        confirm.ForceClick();
        var deadline = DateTime.UtcNow + RoomTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await GameThread.WaitForNextFrameAsync();
            var now = GameNodes.ResolveScreen();
            if (now != Screen.CharacterSelect && now != Screen.Unknown)
            {
                return new ActionResult("completed", new JsonObject { ["screen"] = now });
            }
        }

        return new ActionResult("pending");
    }

    /// <summary>主菜单 → 单人模式 → 选角色界面。启动后菜单按钮会先禁用一小会儿，这里等它变可用。</summary>
    private static async Task<ActionResult> OpenSingleplayerAsync()
    {
        var menu = MegaCrit.Sts2.Core.Nodes.NGame.Instance?.MainMenu;
        if (menu == null)
        {
            throw ApiException.Conflict("invalid_action", "现在不在主菜单。");
        }

        var button = menu.GetNodeOrNull<NClickableControl>("MainMenuTextButtons/SingleplayerButton")
            ?? GameNodes.FindAll<NClickableControl>(menu)
                .FirstOrDefault(control => GodotObject.IsInstanceValid(control)
                    && GameNodes.Clean(control.Name).Contains("Singleplayer", StringComparison.OrdinalIgnoreCase));

        if (button == null)
        {
            throw ApiException.Conflict("invalid_action", "找不到单人模式按钮。");
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);
        while (DateTime.UtcNow < deadline && !button.IsEnabled)
        {
            await GameThread.WaitForNextFrameAsync();
        }

        if (!button.IsEnabled)
        {
            throw ApiException.Conflict("blocked", "主菜单还在加载，单人模式按钮暂时不可用。", new { retry_after_ms = 800 }, retryable: true);
        }

        GameNodes.Press(button);
        var waitDeadline = DateTime.UtcNow + RoomTimeout;
        while (DateTime.UtcNow < waitDeadline)
        {
            await GameThread.WaitForNextFrameAsync();
            if (GameNodes.ResolveScreen() != Screen.MainMenu)
            {
                return new ActionResult("completed", new JsonObject { ["screen"] = GameNodes.ResolveScreen() });
            }
        }

        return new ActionResult("pending");
    }

    /// <summary>单人子菜单 → 标准模式（进入选角色）。</summary>
    private static async Task<ActionResult> StartStandardRunAsync()
    {
        var game = MegaCrit.Sts2.Core.Nodes.NGame.Instance
            ?? throw ApiException.Unavailable("state_unavailable", "游戏还没起来。");
        var submenu = GameNodes.FindAll<NSingleplayerSubmenu>(game)
            .FirstOrDefault(node => GodotObject.IsInstanceValid(node) && node.IsVisibleInTree())
            ?? throw ApiException.Conflict("invalid_action", "现在不在单人子菜单里。");

        var button = GameNodes.FindAll<NSubmenuButton>(submenu)
            .FirstOrDefault(node => GodotObject.IsInstanceValid(node) && node.IsVisibleInTree()
                && GameNodes.Clean(node.Name).Contains("Standard", StringComparison.OrdinalIgnoreCase))
            ?? throw ApiException.Conflict("invalid_action", "找不到标准模式按钮。");

        if (!button.IsEnabled)
        {
            throw ApiException.Conflict("invalid_action", "标准模式现在不可点。");
        }

        GameNodes.Press(button);
        var deadline = DateTime.UtcNow + RoomTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await GameThread.WaitForNextFrameAsync();
            if (GameNodes.ResolveScreen() == Screen.CharacterSelect)
            {
                return new ActionResult("completed", new JsonObject { ["screen"] = Screen.CharacterSelect });
            }
        }

        return new ActionResult("pending", new JsonObject { ["screen"] = GameNodes.ResolveScreen() });
    }

    /// <summary>返回上一层：找当前可见的返回按钮。</summary>
    private static async Task<ActionResult> BackAsync()
    {
        var game = MegaCrit.Sts2.Core.Nodes.NGame.Instance
            ?? throw ApiException.Unavailable("state_unavailable", "游戏还没起来。");

        var button = GameNodes.FindAll<NBackButton>(game)
            .Where(button => GameNodes.IsUsable(button))
            .OrderByDescending(button => button.GetIndex())
            .FirstOrDefault()
            ?? throw ApiException.Conflict("invalid_action", "现在没有可以点的返回按钮。");

        var before = GameNodes.ResolveScreen();
        GameNodes.Press(button);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(4);
        while (DateTime.UtcNow < deadline)
        {
            await GameThread.WaitForNextFrameAsync();
            if (GameNodes.ResolveScreen() != before)
            {
                return new ActionResult("completed", new JsonObject { ["screen"] = GameNodes.ResolveScreen() });
            }
        }

        return new ActionResult("pending", new JsonObject { ["screen"] = GameNodes.ResolveScreen() });
    }

    private static async Task<ActionResult> ContinueRunAsync()
    {        var menu = MegaCrit.Sts2.Core.Nodes.NGame.Instance?.MainMenu;
        if (menu == null)
        {
            throw ApiException.Conflict("invalid_action", "现在不在主菜单。");
        }

        var continueButton = menu.GetNodeOrNull<NClickableControl>("MainMenuTextButtons/ContinueButton")
            ?? GameNodes.FindAll<NClickableControl>(menu)
                .FirstOrDefault(control => GodotObject.IsInstanceValid(control) && control.IsVisibleInTree() && control.IsEnabled
                    && GameNodes.Clean(control.Name).Contains("Continue", StringComparison.OrdinalIgnoreCase));

        if (continueButton == null || !continueButton.IsEnabled)
        {
            throw ApiException.Conflict("invalid_action", "没有可以继续的存档。");
        }

        continueButton.ForceClick();
        await WaitForScreenLeaveAsync(Screen.MainMenu);
        return new ActionResult("completed", new JsonObject { ["screen"] = GameNodes.ResolveScreen() });
    }

    private static async Task<ActionResult> ModalAsync(bool confirm)
    {
        if (GameNodes.OpenModal() is not Node modal)
        {
            throw ApiException.Conflict("invalid_action", "现在没有弹窗。");
        }

        var tokens = confirm
            ? new[] { "Yes", "Confirm", "Ok", "Continue", "Accept", "Acknowledge" }
            : new[] { "No", "Cancel", "Back", "Close" };

        var button = GameNodes.FindAll<NClickableControl>(modal)
            .Where(control => GodotObject.IsInstanceValid(control) && control.IsVisibleInTree() && control.IsEnabled)
            .FirstOrDefault(control =>
            {
                var name = GameNodes.Clean(control.Name);
                return tokens.Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase));
            });

        if (button == null)
        {
            throw ApiException.Conflict("invalid_action", confirm ? "弹窗没有确认按钮。" : "弹窗没有取消按钮。");
        }

        button.ForceClick();
        await GameThread.WaitForNextFrameAsync();
        return new ActionResult("completed", new JsonObject { ["modal"] = modal.GetType().Name });
    }

    private static async Task<ActionResult> ContinueGameOverAsync()
    {
        if (GameNodes.CurrentScreen() is not Node screen)
        {
            throw ApiException.Conflict("invalid_action", "现在不在结算界面。");
        }

        // 结算页顺序是：总结动画 → Continue（放完/跳过）→ MainMenu。优先直接走 MainMenu；
        // 两个按钮的处理器都不吃 ForceClick，所以 ForceClick 后再补一次 Released 信号。
        var clicked = "none";
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buttons = GameNodes.FindAll<NClickableControl>(screen)
                .Where(control => GodotObject.IsInstanceValid(control) && control.IsVisibleInTree() && control.IsEnabled)
                .ToList();

            var mainMenu = buttons.FirstOrDefault(control => GameNodes.Clean(control.Name).Contains("MainMenu", StringComparison.OrdinalIgnoreCase)
                || GameNodes.Clean(control.Name).Contains("Return", StringComparison.OrdinalIgnoreCase));
            var continueButton = buttons.FirstOrDefault(control => GameNodes.Clean(control.Name).Contains("Continue", StringComparison.OrdinalIgnoreCase));

            var target = mainMenu ?? continueButton;
            if (target == null)
            {
                if (attempt == 0)
                {
                    throw ApiException.Conflict("blocked", "结算页还在放动画，暂时没有可点的按钮。", new { retry_after_ms = 800 }, retryable: true);
                }

                break;
            }

            clicked = GameNodes.Clean(target.Name);
            target.ForceClick();
            target.EmitSignal(NClickableControl.SignalName.Released, target);

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);
            while (DateTime.UtcNow < deadline)
            {
                await GameThread.WaitForNextFrameAsync();
                if (GameNodes.ResolveScreen() != Screen.GameOver)
                {
                    return new ActionResult("completed", new JsonObject { ["clicked"] = clicked, ["screen"] = GameNodes.ResolveScreen() });
                }

                // MainMenu 按钮出现就立刻点它，不用等动画。
                var nowButtons = GameNodes.FindAll<NClickableControl>(screen)
                    .Where(control => GodotObject.IsInstanceValid(control) && control.IsVisibleInTree() && control.IsEnabled)
                    .ToList();
                var nowMain = nowButtons.FirstOrDefault(control => GameNodes.Clean(control.Name).Contains("MainMenu", StringComparison.OrdinalIgnoreCase)
                    || GameNodes.Clean(control.Name).Contains("Return", StringComparison.OrdinalIgnoreCase));
                if (nowMain != null && !ReferenceEquals(nowMain, target))
                {
                    break;
                }
            }
        }

        return new ActionResult("pending", new JsonObject { ["clicked"] = clicked, ["screen"] = GameNodes.ResolveScreen() });
    }
    // ------------------------------------------------------------- 调试

    /// <summary>把界面节点树读出来（只读），用于排查按钮路径。</summary>
    private static ActionResult InspectTree(JsonElement args)
    {
        var path = Args.Str(args, "path");
        var depth = Math.Clamp(Args.Int(args, "depth") ?? 3, 1, 8);
        var root = MegaCrit.Sts2.Core.Nodes.NGame.Instance;
        Node? start = root;
        if (!string.IsNullOrWhiteSpace(path) && root != null)
        {
            start = root.GetNodeOrNull<Node>(path);
        }

        if (start == null)
        {
            throw ApiException.Conflict("invalid_target", "找不到这个节点。", new { path });
        }

        var json = new JsonObject
        {
            ["path"] = start.GetPath().ToString(),
            ["type"] = start.GetType().Name,
            ["children"] = DumpNode(start, depth),
        };
        return new ActionResult("completed", json);
    }

    private static JsonArray DumpNode(Node node, int depth)
    {
        var arr = new JsonArray();
        foreach (var child in node.GetChildren())
        {
            if (child is not Node childNode)
            {
                continue;
            }

            var entry = new JsonObject
            {
                ["name"] = childNode.Name.ToString(),
                ["type"] = childNode.GetType().Name,
            };

            if (childNode is CanvasItem canvas)
            {
                entry["visible"] = canvas.IsVisibleInTree();
            }

            if (childNode is NClickableControl clickable)
            {
                entry["enabled"] = clickable.IsEnabled;
            }

            if (depth > 1 && childNode.GetChildCount() > 0)
            {
                entry["children"] = DumpNode(childNode, depth - 1);
            }

            arr.Add(entry);
        }

        return arr;
    }

    /// <summary>按节点路径点一下。这是给 `inspect_tree` 配套的兜底手段。</summary>
    private static async Task<ActionResult> ClickNodeAsync(JsonElement args)
    {
        var path = Args.Str(args, "path") ?? throw ApiException.BadRequest("invalid_request", "click_node 需要 path。");
        var mode = Args.Str(args, "mode") ?? "force";
        var root = MegaCrit.Sts2.Core.Nodes.NGame.Instance
            ?? throw ApiException.Unavailable("state_unavailable", "游戏还没起来。");

        var node = root.GetNodeOrNull<Node>(path);
        if (node == null)
        {
            throw ApiException.Conflict("invalid_target", "找不到这个节点。", new { path });
        }

        if (node is not NClickableControl clickable)
        {
            throw ApiException.Conflict("invalid_target", "这个节点不是可以点的控件。", new { type = node.GetType().Name });
        }

        switch (mode)
        {
            case "debug":
                clickable.DebugPress();
                await GameThread.WaitForNextFrameAsync();
                clickable.DebugRelease();
                break;
            case "released":
                clickable.EmitSignal(NClickableControl.SignalName.Released, clickable);
                break;
            default:
                clickable.ForceClick();
                break;
        }

        await GameThread.WaitForNextFrameAsync();
        await GameThread.WaitForNextFrameAsync();
        return new ActionResult("completed", new JsonObject { ["screen"] = GameNodes.ResolveScreen(), ["path"] = path, ["mode"] = mode });
    }

    // ------------------------------------------------------------- 工具

    private static bool HasSelectionScreen() => GameNodes.ResolveScreen() == Screen.CardSelection;

    private static async Task WaitForScreenLeaveAsync(string screen, int extraFrames = 0)
    {
        var deadline = DateTime.UtcNow + RoomTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await GameThread.WaitForNextFrameAsync();
            if (GameNodes.ResolveScreen() != screen)
            {
                for (var i = 0; i < extraFrames; i++)
                {
                    await GameThread.WaitForNextFrameAsync();
                }

                return;
            }
        }
    }

    private static int SafeSlot(RunState runState, Player player)
    {
        try
        {
            return runState.GetPlayerSlotIndex(player);
        }
        catch
        {
            return 0;
        }
    }
}
