using System.Text.Json.Nodes;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using PwA.Server;
using Environment = System.Environment;

namespace PwA.Game;

/// <summary>把游戏状态读成 JSON。只读；所有东西都在游戏线程上取。</summary>
internal static class StateBuilder
{
    private static int _seq;

    public static JsonObject? BuildLite()
    {
        var runState = TryRunState();
        var combat = CombatManager.Instance;
        var combatState = TryCombatState();
        var turn = combatState?.RoundNumber ?? 0;
        var screen = GameNodes.ResolveScreen();
        var me = combatState != null ? SafeMe(combatState) : null;
        var canAct = me != null && combat.IsInProgress && combat.IsPartOfPlayerTurn(me);

        return new JsonObject
        {
            ["screen"] = screen,
            ["turn"] = turn,
            ["in_combat"] = combat.IsInProgress && (combatState?.Enemies.Count ?? 0) > 0,
            ["choice"] = PromptKind(screen),
            ["turn_started"] = canAct && !combat.PlayerActionsDisabled,
            ["floor"] = runState?.TotalFloor ?? 0,
        };
    }

    private static string? PromptKind(string screen) => screen switch
    {
        Screen.CardSelection => "card",
        Screen.Reward => "reward",
        Screen.Event => "event",
        Screen.Map => "map",
        Screen.Rest => "rest",
        Screen.Shop => "shop",
        Screen.Chest => "chest",
        Screen.Modal => "modal",
        _ => null,
    };

    public static JsonObject Build()
    {
        var screen = GameNodes.ResolveScreen();
        var runState = TryRunState();
        var combatState = TryCombatState();
        var me = combatState != null ? SafeMe(combatState) : null;
        var runMe = runState != null ? SafeMe(runState) : null;

        var state = new JsonObject
        {
            ["service"] = "pwa",
            ["mod_version"] = ModEntry.Version,
            ["role"] = InstanceRole.Current.ToString().ToLowerInvariant(),
            ["port"] = BridgeServer.Instance?.Port ?? 0,
            ["pid"] = Environment.ProcessId,
            ["screen"] = screen,
            ["seq"] = Interlocked.Add(ref _seq, 1),
            ["ts"] = DateTimeOffset.Now.ToString("O"),
            ["in_combat"] = CombatManager.Instance.IsInProgress && (CombatManager.Instance.DebugOnlyGetState()?.Enemies?.Count ?? 0) > 0,
            ["turn"] = combatState?.RoundNumber ?? 0,
            ["combat"] = BuildCombat(combatState, me),
            ["run"] = BuildRun(runState, runMe),
            ["map"] = screen == Screen.Map || GameNodes.VisibleMapScreen() != null ? Safe(BuildMap, "map") : null,
            ["event"] = screen == Screen.Event ? Safe(BuildEvent, "event") : null,
            ["reward"] = screen == Screen.Reward ? Safe(BuildReward, "reward") : null,
            ["shop"] = screen == Screen.Shop ? Safe(BuildShop, "shop") : null,
            ["rest"] = screen == Screen.Rest ? Safe(BuildRest, "rest") : null,
            ["chest"] = screen == Screen.Chest ? Safe(BuildChest, "chest") : null,
            ["selection"] = screen == Screen.CardSelection ? Safe(BuildSelection, "selection") : null,
            ["modal"] = screen == Screen.Modal ? Safe(BuildModal, "modal") : null,
            ["character_select"] = screen == Screen.CharacterSelect ? Safe(BuildCharacterSelect, "character_select") : null,
            ["singleplayer"] = screen == Screen.SingleplayerMenu ? Safe(BuildSingleplayerMenu, "singleplayer") : null,
            ["game_over"] = screen == Screen.GameOver ? Safe(BuildGameOver, "game_over") : null,
        };

        state["actions"] = BuildActions(state);

        PulseCache.Screen = screen;
        PulseCache.InCombat = CombatManager.Instance.IsInProgress;
        PulseCache.InRun = runState != null;
        PulseCache.Turn = combatState?.RoundNumber ?? 0;
        PulseCache.Floor = runState?.TotalFloor ?? 0;
        PulseCache.Role = InstanceRole.Current.ToString().ToLowerInvariant();
        PulseCache.UpdatedAtTicks = DateTime.UtcNow.Ticks;
        return state;
    }

    /// <summary>给脉冲用：这一局在不在。</summary>
    public static bool HasRun() => TryRunState() != null;

    /// <summary>单个区块读失败不应该让整份状态失败。</summary>
    private static JsonNode? Safe(Func<JsonNode?> build, string name)
    {
        try
        {
            return build();
        }
        catch (Exception ex)
        {
            Log.Warn($"[PwA] 读 {name} 失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>本地化文本缺 key 是常态（事件翻页、未解锁内容），一律降级成空串。</summary>
    public static string SafeLoc(Func<string?> read)
    {
        try
        {
            return GameNodes.Clean(read());
        }
        catch
        {
            return string.Empty;
        }
    }

    private static RunState? TryRunState()
    {
        try
        {
            return RunManager.Instance?.DebugOnlyGetState();
        }
        catch
        {
            return null;
        }
    }

    private static CombatState? TryCombatState()
    {
        try
        {
            return CombatManager.Instance.DebugOnlyGetState();
        }
        catch
        {
            return null;
        }
    }

    private static Player? SafeMe(CombatState state)
    {
        try
        {
            return LocalContext.GetMe(state);
        }
        catch
        {
            return null;
        }
    }

    private static Player? SafeMe(RunState state)
    {
        try
        {
            return LocalContext.GetMe(state);
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- combat

    private static JsonObject? BuildCombat(CombatState? combat, Player? me)
    {
        if (combat == null)
        {
            return null;
        }

        var manager = CombatManager.Instance;
        var myTurn = me != null && manager.IsPartOfPlayerTurn(me);
        var handCount = me?.PlayerCombatState?.Hand.Cards.Count ?? 0;
        var energyNow = me?.PlayerCombatState?.Energy ?? 0;
        var maxEnergyNow = me?.MaxEnergy ?? 0;
        var drawNow = me?.PlayerCombatState?.DrawPile.Cards.Count ?? 0;
        // 游戏自己的回合阶段才是权威：Start/AutoPrePlay 期间能量可能已经回了、手牌还没抽完，
        // 这段时间不能当成「可出牌」。
        var turnPhase = me?.PlayerCombatState?.Phase ?? PlayerTurnPhase.None;
        var phase = !manager.IsInProgress
            ? "not_in_combat"
            : !myTurn
                ? "enemy_turn"
                : turnPhase == PlayerTurnPhase.Play
                    ? "player_turn"
                    : "turn_start_resolution";

        var playerDebuffs = new JsonArray();
        try
        {
            foreach (var power in me?.Creature.Powers ?? Array.Empty<PowerModel>())
            {
                if (power != null && power.Type == PowerType.Debuff)
                {
                    playerDebuffs.Add(new JsonObject
                    {
                        ["id"] = power.Id.Entry,
                        ["name"] = SafeTitle(power),
                        ["amount"] = power.Amount,
                    });
                }
            }
        }
        catch
        {
        }

        var combatNode = new JsonObject
        {
            ["turn"] = combat.RoundNumber,
            ["side"] = combat.CurrentSide.ToString(),
            ["in_progress"] = manager.IsInProgress,
            ["turn_phase"] = phase,
            ["player_debuffs"] = playerDebuffs,
            ["over_or_ending"] = manager.IsOverOrEnding,
            ["paused"] = manager.IsPaused,
            ["actions_disabled"] = manager.PlayerActionsDisabled,
            ["ending_turn"] = manager.EndingPlayerTurnPhaseOne || manager.EndingPlayerTurnPhaseTwo,
            ["my_turn"] = myTurn,
            ["me"] = me == null ? null : BuildCombatPlayer(me),
            ["hand"] = BuildHand(me),
            ["enemies"] = BuildEnemies(combat),
            ["players"] = BuildPlayers(combat),
            ["piles"] = BuildPiles(me),
            ["hand_selection"] = NCombatRoom.Instance?.Ui?.Hand is { } hand && hand.IsInCardSelection,
        };

        if (phase == "turn_start_resolution")
        {
            combatNode["note"] = playerDebuffs.Count > 0
                ? "本回合开始前有遗物/状态结算，且你身上有 debuff；先看 player_debuffs"
                : "本回合开始前仍在结算（遗物/状态），还没到可出牌阶段";
        }

        return combatNode;
    }

    private static JsonObject BuildCombatPlayer(Player me)
    {
        var creature = me.Creature;
        var combatState = me.PlayerCombatState;
        return new JsonObject
        {
            ["character"] = SafeTitle(me.Character),
            ["character_id"] = me.Character?.Id.Entry,
            ["hp"] = creature.CurrentHp,
            ["max_hp"] = creature.MaxHp,
            ["block"] = creature.Block,
            ["energy"] = combatState?.Energy ?? 0,
            ["max_energy"] = me.MaxEnergy,
            ["stars"] = combatState?.Stars ?? 0,
            ["alive"] = creature.IsAlive,
            ["powers"] = BuildPowers(creature),
            ["potions"] = BuildPotions(me),
        };
    }

    private static JsonArray BuildHand(Player? me)
    {
        var hand = new JsonArray();
        var cards = me?.PlayerCombatState?.Hand.Cards;
        if (cards == null)
        {
            return hand;
        }

        for (var i = 0; i < cards.Count; i++)
        {
            hand.Add(BuildCard(cards[i], i));
        }

        return hand;
    }

    /// <summary>手牌/选牌共用的卡牌描述。cost 是当前实际费用。</summary>
    public static JsonObject BuildCard(CardModel card, int? index, bool evaluatePlayable = true)
    {
        var reason = string.Empty;
        var playable = false;
        if (evaluatePlayable)
        {
            try
            {
                playable = card.CanPlay(out var unplayable, out _);
                if (!playable)
                {
                    reason = DescribeReason(unplayable);
                }
            }
            catch
            {
                playable = false;
                reason = "unknown";
            }
        }

        var targets = new JsonArray();
        if (evaluatePlayable)
        {
            foreach (var target in LegalTargets(card))
            {
                targets.Add(target);
            }
        }

        return new JsonObject
        {
            ["i"] = index,
            ["ref"] = CardRefs.RefOf(card),
            ["id"] = card.Id.Entry,
            ["name"] = GameNodes.Clean(card.Title),
            ["type"] = card.Type.ToString(),
            ["rarity"] = card.Rarity.ToString(),
            ["cost"] = SafeCost(card),
            ["star_cost"] = SafeStarCost(card),
            ["upgraded"] = card.IsUpgraded,
            ["playable"] = evaluatePlayable ? playable : null,
            ["why"] = string.IsNullOrEmpty(reason) ? null : reason,
            ["target_type"] = card.TargetType.ToString(),
            ["targets"] = targets,
            ["text"] = SafeDescription(card),
        };
    }

    private static string DescribeReason(UnplayableReason reason) => reason switch
    {
        UnplayableReason.EnergyCostTooHigh => "not_enough_energy",
        UnplayableReason.StarCostTooHigh => "not_enough_stars",
        UnplayableReason.NoLivingAllies => "no_living_allies",
        _ => reason.ToString(),
    };

    private static int SafeCost(CardModel card)
    {
        try
        {
            return card.EnergyCost?.GetWithModifiers(CostModifiers.All) ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static int SafeStarCost(CardModel card)
    {
        try
        {
            return Math.Max(0, card.GetStarCostWithModifiers());
        }
        catch
        {
            return 0;
        }
    }

    private static string SafeDescription(CardModel card)
    {
        try
        {
            card.UpdateDynamicVarPreview(CardPreviewMode.Normal, card.CurrentTarget, card.DynamicVars);
            var resolved = card.GetDescriptionForPile(card.Pile?.Type ?? PileType.None, card.CurrentTarget);
            if (string.IsNullOrWhiteSpace(resolved))
            {
                resolved = card.Description?.GetRawText();
            }

            return GameNodes.Clean(resolved);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>这张牌现在可以指哪些目标（敌人或玩家下标的数组）。</summary>
    public static List<int> LegalTargets(CardModel card)
    {
        var result = new List<int>();
        try
        {
            if (card.TargetType == TargetType.AnyEnemy)
            {
                var state = CombatManager.Instance.DebugOnlyGetState();
                if (state == null)
                {
                    return result;
                }

                for (var i = 0; i < state.Enemies.Count; i++)
                {
                    var enemy = state.Enemies[i];
                    if (enemy.IsAlive && enemy.IsHittable)
                    {
                        result.Add(i);
                    }
                }
            }
            else if (card.TargetType == TargetType.AnyAlly)
            {
                var runState = TryRunState();
                if (runState?.Players == null)
                {
                    return result;
                }

                var ordered = runState.Players.OrderBy(player => SafeSlot(runState, player)).ToList();
                for (var i = 0; i < ordered.Count; i++)
                {
                    var player = ordered[i];
                    if (player.Creature.IsAlive && !LocalContext.IsMe(player))
                    {
                        result.Add(i);
                    }
                }
            }
        }
        catch
        {
        }

        return result;
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

    private static JsonArray BuildEnemies(CombatState combat)
    {
        var arr = new JsonArray();
        var targets = combat.Players.Select(player => player.Creature).ToList();
        for (var i = 0; i < combat.Enemies.Count; i++)
        {
            var enemy = combat.Enemies[i];
            var intents = new JsonArray();
            try
            {
                var move = enemy.Monster?.NextMove;
                if (move != null)
                {
                    foreach (var intent in move.Intents)
                    {
                        intents.Add(BuildIntent(intent, targets, enemy));
                    }
                }
            }
            catch
            {
            }

            arr.Add(new JsonObject
            {
                ["i"] = i,
                ["combat_id"] = enemy.CombatId,
                ["id"] = enemy.ModelId.Entry,
                ["name"] = GameNodes.Clean(enemy.Name),
                ["hp"] = enemy.CurrentHp,
                ["max_hp"] = enemy.MaxHp,
                ["block"] = enemy.Block,
                ["alive"] = enemy.IsAlive,
                ["hittable"] = enemy.IsHittable,
                ["intents"] = intents,
                ["powers"] = BuildPowers(enemy),
            });
        }

        return arr;
    }

    private static JsonObject BuildIntent(AbstractIntent intent, IReadOnlyList<Creature> targets, Creature owner)
    {
        var node = new JsonObject
        {
            ["kind"] = intent.IntentType.ToString(),
        };

        try
        {
            node["label"] = GameNodes.Clean(intent.GetIntentLabel(targets, owner)?.GetFormattedText());
        }
        catch
        {
            node["label"] = null;
        }

        if (intent is AttackIntent attack)
        {
            try
            {
                node["damage"] = attack.GetSingleDamage(targets, owner);
                node["hits"] = attack.Repeats;
                node["total"] = attack.GetTotalDamage(targets, owner);
            }
            catch
            {
            }
        }
        else if (intent is StatusIntent status)
        {
            node["cards"] = status.CardCount;
        }

        return node;
    }

    private static JsonArray BuildPlayers(CombatState combat)
    {
        var arr = new JsonArray();
        var runState = TryRunState();
        var ordered = combat.Players
            .OrderBy(player => runState != null ? SafeSlot(runState, player) : 0)
            .ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var player = ordered[i];
            arr.Add(new JsonObject
            {
                ["i"] = i,
                ["net_id"] = player.NetId.ToString(),
                ["is_me"] = LocalContext.IsMe(player),
                ["character"] = SafeTitle(player.Character),
                ["hp"] = player.Creature.CurrentHp,
                ["max_hp"] = player.Creature.MaxHp,
                ["block"] = player.Creature.Block,
                ["energy"] = player.PlayerCombatState?.Energy ?? 0,
                ["alive"] = player.Creature.IsAlive,
                ["powers"] = BuildPowers(player.Creature),
            });
        }

        return arr;
    }

    private static JsonObject BuildPiles(Player? me)
    {
        var state = me?.PlayerCombatState;
        var draw = state?.DrawPile?.Cards.Count ?? 0;
        var discard = state?.DiscardPile?.Cards.Count ?? 0;
        var exhaust = state?.ExhaustPile?.Cards.Count ?? 0;
        var hand = state?.Hand?.Cards.Count ?? 0;
        return new JsonObject
        {
            ["draw"] = draw,
            ["discard"] = discard,
            ["exhaust"] = exhaust,
            ["hand"] = hand,
            ["total"] = draw + discard + exhaust + hand,
        };
    }

    public static JsonArray BuildPowers(Creature creature)
    {
        var arr = new JsonArray();
        try
        {
            foreach (var power in creature.Powers)
            {
                if (power == null)
                {
                    continue;
                }

                arr.Add(new JsonObject
                {
                    ["id"] = power.Id.Entry,
                    ["name"] = SafeTitle(power),
                    ["amount"] = power.Amount,
                    ["debuff"] = power.Type == PowerType.Debuff,
                });
            }
        }
        catch
        {
        }

        return arr;
    }

    private static string SafeTitle(AbstractModel? model)
    {
        if (model == null)
        {
            return string.Empty;
        }

        try
        {
            var title = model switch
            {
                CardModel card => card.Title,
                PowerModel power => power.Title?.GetFormattedText(),
                RelicModel relic => relic.Title?.GetFormattedText(),
                PotionModel potion => potion.Title?.GetFormattedText(),
                CharacterModel character => character.Title?.GetFormattedText(),
                _ => null,
            };
            return GameNodes.Clean(title ?? model.Id.Entry);
        }
        catch
        {
            return model.Id.Entry;
        }
    }

    // ------------------------------------------------------------------- run

    private static JsonObject? BuildRun(RunState? runState, Player? me)
    {
        if (runState == null || me == null)
        {
            return null;
        }

        var relics = new JsonArray();
        foreach (var relic in me.Relics)
        {
            relics.Add(new JsonObject
            {
                ["id"] = relic.Id.Entry,
                ["name"] = SafeTitle(relic),
                ["count"] = relic.ShowCounter ? relic.DisplayAmount : null,
            });
        }

        var deck = new JsonArray();
        try
        {
            foreach (var card in me.Deck.Cards)
            {
                deck.Add(new JsonObject
                {
                    ["id"] = card.Id.Entry,
                    ["name"] = GameNodes.Clean(card.Title),
                    ["upgraded"] = card.IsUpgraded,
                });
            }
        }
        catch
        {
        }

        return new JsonObject
        {
            ["character"] = SafeTitle(me.Character),
            ["character_id"] = me.Character?.Id.Entry,
            ["ascension"] = runState.AscensionLevel,
            ["floor"] = runState.TotalFloor,
            ["act"] = runState.CurrentActIndex + 1,
            ["act_id"] = runState.CurrentActIndex,
            ["gold"] = me.Gold,
            ["hp"] = me.Creature.CurrentHp,
            ["max_hp"] = me.Creature.MaxHp,
            ["deck"] = deck,
            ["relics"] = relics,
            ["potions"] = BuildPotions(me),
            ["players"] = BuildRunPlayers(runState),
            ["boss_id"] = runState.Act?.BossEncounter?.Id.Entry,
            ["room"] = runState.CurrentRoom?.GetType().Name,
        };
    }

    private static JsonArray BuildRunPlayers(RunState runState)
    {
        var arr = new JsonArray();
        try
        {
            var ordered = runState.Players.OrderBy(player => SafeSlot(runState, player)).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var player = ordered[i];
                arr.Add(new JsonObject
                {
                    ["i"] = i,
                    ["net_id"] = player.NetId.ToString(),
                    ["is_me"] = LocalContext.IsMe(player),
                    ["character"] = SafeTitle(player.Character),
                    ["hp"] = player.Creature.CurrentHp,
                    ["max_hp"] = player.Creature.MaxHp,
                    ["gold"] = player.Gold,
                    ["alive"] = player.Creature.IsAlive,
                });
            }
        }
        catch
        {
        }

        return arr;
    }

    public static JsonArray BuildPotions(Player me)
    {
        var arr = new JsonArray();
        try
        {
            for (var slot = 0; slot < me.PotionSlots.Count; slot++)
            {
                var potion = me.PotionSlots[slot];
                if (potion == null)
                {
                    arr.Add(new JsonObject { ["slot"] = slot, ["empty"] = true });
                    continue;
                }

                arr.Add(new JsonObject
                {
                    ["slot"] = slot,
                    ["empty"] = false,
                    ["id"] = potion.Id.Entry,
                    ["name"] = SafeTitle(potion),
                    ["target_type"] = potion.TargetType.ToString(),
                    ["usable"] = SafePotionUsable(potion, me),
                });
            }
        }
        catch
        {
        }

        return arr;
    }

    private static bool SafePotionUsable(PotionModel potion, Player me)
    {
        try
        {
            return potion.PassesCustomUsabilityCheck && !potion.IsQueued && me.Creature.IsAlive;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------- map

    private static JsonObject BuildMap()
    {
        var result = new JsonObject();
        var screen = NMapScreen.Instance;
        var runState = TryRunState();
        result["current"] = runState?.CurrentMapCoord is { } coord ? $"{coord.row},{coord.col}" : null;
        result["act"] = runState?.CurrentActIndex;
        if (screen == null || !GodotObject.IsInstanceValid(screen))
        {
            result["open"] = false;
            result["options"] = new JsonArray();
            return result;
        }

        // 从营火/商店回到地图时，TravelToMapCoord 留下的 IsTravelEnabled=false 有时不会自己恢复；
        // 这会让地图永远没有可选节点。这里补一次和游戏开图时相同的启用调用。
        EnsureMapTravelEnabled(screen, runState);

        result["open"] = screen.IsOpen;
        result["traveling"] = screen.IsTraveling;
        result["travel_enabled"] = screen.IsTravelEnabled;

        var options = new JsonArray();
        if (!screen.IsTraveling && screen.IsTravelEnabled)
        {
            var nodes = GameNodes.FindAll<NMapPoint>(screen)
                .Where(node => GodotObject.IsInstanceValid(node) && node.IsEnabled && node.IsVisibleInTree())
                .OrderBy(node => node.Point.coord.row)
                .ThenBy(node => node.Point.coord.col)
                .ToList();

            for (var i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                options.Add(new JsonObject
                {
                    ["i"] = i,
                    ["coord"] = $"{node.Point.coord.row},{node.Point.coord.col}",
                    ["node_type"] = node.Point.PointType.ToString(),
                });
            }
        }

        result["options"] = options;

        var votes = new JsonArray();
        if (runState != null)
        {
            try
            {
                foreach (var player in runState.Players.OrderBy(player => SafeSlot(runState, player)))
                {
                    var vote = RunManager.Instance.MapSelectionSynchronizer?.GetVote(player);
                    votes.Add(new JsonObject
                    {
                        ["is_me"] = LocalContext.IsMe(player),
                        ["coord"] = vote?.coord is { } voteCoord ? $"{voteCoord.row},{voteCoord.col}" : null,
                    });
                }
            }
            catch
            {
            }
        }

        result["votes"] = votes;
        return result;
    }

    public static void EnsureMapTravelEnabled(NMapScreen screen, RunState? runState)
    {
        try
        {
            if (screen.IsTraveling || screen.IsTravelEnabled)
            {
                return;
            }

            if (runState?.CurrentMapCoord == null)
            {
                return;
            }

            screen.SetTravelEnabled(true);
        }
        catch
        {
        }
    }

    // ----------------------------------------------------------------- event

    public static EventModel? CurrentEvent()
    {
        try
        {
            return RunManager.Instance?.EventSynchronizer?.GetLocalEvent();
        }
        catch
        {
            return null;
        }
    }

    private static JsonObject? BuildEvent()
    {
        var ev = CurrentEvent();
        if (ev == null)
        {
            return null;
        }

        var options = new JsonArray();
        var list = ev.CurrentOptions;
        if (list != null)
        {
            for (var i = 0; i < list.Count; i++)
            {
                var option = list[i];
                var node = new JsonObject
                {
                    ["i"] = i,
                    ["text_key"] = option.TextKey,
                    ["title"] = FormatEventText(ev, option.Title),
                    ["description"] = FormatEventText(ev, option.Description),
                    ["locked"] = option.IsLocked,
                    ["proceed"] = option.IsProceed,
                    ["was_chosen"] = option.WasChosen,
                    ["relic_id"] = option.Relic?.Id.Entry,
                    ["relic_name"] = option.Relic == null ? null : SafeTitle(option.Relic),
                };
                try
                {
                    node["kills"] = option.WillKillPlayer?.Invoke(ev.Owner) == true;
                }
                catch
                {
                }

                options.Add(node);
            }
        }

        var repeatable = false;
        try
        {
            repeatable = list != null && list.Any(option => option.WasChosen && !option.IsLocked);
        }
        catch
        {
        }

        return new JsonObject
        {
            ["id"] = ev.Id.Entry,
            ["title"] = SafeLoc(() => ev.Title?.GetFormattedText()),
            ["description"] = SafeLoc(() => ev.Description?.GetFormattedText()),
            ["finished"] = ev.IsFinished,
            ["repeatable"] = repeatable,
            ["repeat_note"] = repeatable ? "这里有可以重复选的选项：再选一次会再次结算（可能扣最大生命/继续升级），确认要再选请带 again:true" : null,
            ["options"] = options,
        };
    }

    private static string FormatEventText(EventModel ev, MegaCrit.Sts2.Core.Localization.LocString? text)
    {
        if (text == null)
        {
            return string.Empty;
        }

        try
        {
            ev.DynamicVars?.AddTo(text);
            return GameNodes.Clean(text.GetFormattedText());
        }
        catch
        {
            return string.Empty;
        }
    }

    // ---------------------------------------------------------------- reward

    private static JsonObject? BuildReward()
    {
        var screen = GameNodes.CurrentScreen();
        var gameRoot = NGame.Instance;

        // Boss 结算这类覆盖层里 ActiveScreenContext 仍指向战斗房间，按可见节点兜底找奖励屏。
        var cardScreen = screen as NCardRewardSelectionScreen
            ?? (gameRoot != null ? GameNodes.FindVisible<NCardRewardSelectionScreen>(gameRoot) : null);
        var rewardsScreen = screen as NRewardsScreen
            ?? (gameRoot != null ? GameNodes.FindVisible<NRewardsScreen>(gameRoot) : null);

        if (cardScreen != null)
        {
            var cards = new JsonArray();
            var holders = GameNodes.FindAll<NCardHolder>(cardScreen)
                .Where(holder => GodotObject.IsInstanceValid(holder) && holder.CardModel != null)
                .OrderBy(holder => holder.GlobalPosition.Y)
                .ThenBy(holder => holder.GlobalPosition.X)
                .ToList();
            for (var i = 0; i < holders.Count; i++)
            {
                cards.Add(BuildCard(holders[i].CardModel!, i));
            }

            var alternatives = new JsonArray();
            foreach (var button in GameNodes.FindAll<NCardRewardAlternativeButton>(cardScreen))
            {
                if (!GodotObject.IsInstanceValid(button) || !button.IsVisibleInTree())
                {
                    continue;
                }

                alternatives.Add(new JsonObject { ["label"] = ReadText(button) });
            }

            return new JsonObject
            {
                ["card_choice"] = true,
                ["cards"] = cards,
                ["skip_options"] = alternatives,
            };
        }

        if (rewardsScreen != null)
        {
            var rewards = new JsonArray();
            var buttons = GameNodes.FindAll<NRewardButton>(rewardsScreen)
                .Where(button => GodotObject.IsInstanceValid(button))
                .OrderBy(button => button.GlobalPosition.Y)
                .ThenBy(button => button.GlobalPosition.X)
                .ToList();
            for (var i = 0; i < buttons.Count; i++)
            {
                var button = buttons[i];
                var reward = button.Reward;
                rewards.Add(new JsonObject
                {
                    ["i"] = i,
                    ["kind"] = RewardKind(reward),
                    ["description"] = SafeRewardText(reward),
                    ["claimable"] = button.IsEnabled,
                });
            }

            return new JsonObject
            {
                ["card_choice"] = false,
                ["rewards"] = rewards,
                ["can_proceed"] = GameNodes.ProceedButton() != null,
            };
        }

        return null;
    }

    private static string RewardKind(Reward? reward) => reward switch
    {
        CardReward => "card",
        GoldReward => "gold",
        PotionReward => "potion",
        RelicReward => "relic",
        null => "unknown",
        _ => reward.GetType().Name,
    };

    private static string SafeRewardText(Reward? reward)
    {
        try
        {
            return GameNodes.Clean(reward?.Description?.GetFormattedText());
        }
        catch
        {
            return string.Empty;
        }
    }

    // ------------------------------------------------------------------ shop

    private static JsonObject? BuildShop()
    {
        var room = NMerchantRoom.Instance;
        var inventory = room?.Inventory?.Inventory;
        if (inventory == null)
        {
            return null;
        }

        var cards = new JsonArray();
        var index = 0;
        AddCardEntries(cards, inventory.CharacterCardEntries, "character", ref index);
        AddCardEntries(cards, inventory.ColorlessCardEntries, "colorless", ref index);

        var relics = new JsonArray();
        for (var i = 0; i < inventory.RelicEntries.Count; i++)
        {
            var entry = inventory.RelicEntries[i];
            relics.Add(new JsonObject
            {
                ["i"] = i,
                ["id"] = entry.Model?.Id.Entry,
                ["name"] = entry.Model == null ? null : SafeTitle(entry.Model),
                ["price"] = entry.IsStocked ? entry.Cost : 0,
                ["stocked"] = entry.IsStocked,
                ["affordable"] = entry.IsStocked && entry.EnoughGold,
            });
        }

        var potions = new JsonArray();
        for (var i = 0; i < inventory.PotionEntries.Count; i++)
        {
            var entry = inventory.PotionEntries[i];
            potions.Add(new JsonObject
            {
                ["i"] = i,
                ["id"] = entry.Model?.Id.Entry,
                ["name"] = entry.Model == null ? null : SafeTitle(entry.Model),
                ["price"] = entry.IsStocked ? entry.Cost : 0,
                ["stocked"] = entry.IsStocked,
                ["affordable"] = entry.IsStocked && entry.EnoughGold,
            });
        }

        var removal = inventory.CardRemovalEntry;
        return new JsonObject
        {
            ["open"] = room!.Inventory?.IsOpen == true,
            ["cards"] = cards,
            ["relics"] = relics,
            ["potions"] = potions,
            ["remove"] = removal == null
                ? null
                : new JsonObject
                {
                    ["price"] = removal.Cost,
                    ["available"] = removal.IsStocked,
                    ["affordable"] = removal.IsStocked && removal.EnoughGold,
                },
            ["gold"] = inventory.Player?.Gold ?? 0,
            ["can_proceed"] = GameNodes.ProceedButton() != null,
        };
    }

    private static void AddCardEntries(
        JsonArray arr,
        IReadOnlyList<MegaCrit.Sts2.Core.Entities.Merchant.MerchantCardEntry> entries,
        string category,
        ref int index)
    {
        if (entries == null)
        {
            return;
        }

        foreach (var entry in entries)
        {
            arr.Add(new JsonObject
            {
                ["i"] = index,
                ["category"] = category,
                ["id"] = entry.CreationResult?.Card?.Id.Entry,
                ["name"] = entry.CreationResult?.Card == null ? null : SafeTitle(entry.CreationResult.Card),
                ["price"] = entry.IsStocked ? entry.Cost : 0,
                ["stocked"] = entry.IsStocked,
                ["affordable"] = entry.IsStocked && entry.EnoughGold,
            });
            index++;
        }
    }

    // ------------------------------------------------------------------ rest

    private static JsonObject? BuildRest()
    {
        IReadOnlyList<MegaCrit.Sts2.Core.Entities.RestSite.RestSiteOption>? options;
        try
        {
            options = RunManager.Instance?.RestSiteSynchronizer?.GetLocalOptions();
        }
        catch
        {
            options = null;
        }

        if (options == null)
        {
            return null;
        }

        var arr = new JsonArray();
        for (var i = 0; i < options.Count; i++)
        {
            var option = options[i];
            arr.Add(new JsonObject
            {
                ["i"] = i,
                ["id"] = option.OptionId,
                ["title"] = SafeLoc(() => option.Title?.GetFormattedText()),
                ["description"] = SafeLoc(() => option.Description?.GetFormattedText()),
                ["enabled"] = option.IsEnabled,
                ["requires_target"] = string.Equals(option.OptionId, "MEND", StringComparison.OrdinalIgnoreCase),
            });
        }

        return new JsonObject
        {
            ["options"] = arr,
            ["can_proceed"] = GameNodes.ProceedButtonAny() != null,
        };
    }

    // ----------------------------------------------------------------- chest

    private static JsonObject? BuildChest()
    {
        var game = NGame.Instance;
        var treasureRoom = game != null && GodotObject.IsInstanceValid(game)
            ? GameNodes.FindAll<NTreasureRoom>(game).FirstOrDefault(GodotObject.IsInstanceValid)
            : null;
        var relicCollection = GameNodes.CurrentScreen() as NTreasureRoomRelicCollection;

        IReadOnlyList<RelicModel>? relics;
        try
        {
            relics = RunManager.Instance?.TreasureRoomRelicSynchronizer?.CurrentRelics;
        }
        catch
        {
            relics = null;
        }

        var arr = new JsonArray();
        if (relics != null)
        {
            for (var i = 0; i < relics.Count; i++)
            {
                arr.Add(new JsonObject
                {
                    ["i"] = i,
                    ["id"] = relics[i].Id.Entry,
                    ["name"] = SafeTitle(relics[i]),
                    ["rarity"] = relics[i].Rarity.ToString(),
                });
            }
        }

        var chestButton = treasureRoom?.GetNodeOrNull<NClickableControl>("%Chest");
        return new JsonObject
        {
            ["opened"] = relicCollection != null || (chestButton != null && !chestButton.IsEnabled),
            ["relics"] = arr,
            ["can_proceed"] = GameNodes.ProceedButton() != null,
        };
    }

    // ------------------------------------------------------------- selection

    public static JsonObject? BuildSelection()
    {
        if (GameNodes.FindSelectionScreen() is { } found)
        {
            switch (found.Kind)
            {
                case "bundle":
                    return BuildBundleSelection((NChooseABundleSelectionScreen)found.Screen);
                case "relic":
                    return BuildRelicSelection(found.Screen);
                case "crystal_sphere":
                    return BuildCrystalSphere(found.Screen);
            }
        }
        var holders = GameNodes.SelectionHolders();
        var inHand = NCombatRoom.Instance?.Ui?.Hand is { } handNode && handNode.IsInCardSelection;
        var screenNode = inHand ? (Node?)NCombatRoom.Instance!.Ui!.Hand : GameNodes.CurrentScreen() as Node;
        // 已选状态有三个来源：界面自己的 _selectedCards、手牌选择时的 NSelectedHandCardHolder、以及候选区的 holder 标记。
        var selected = ReadSelectedCards(screenNode);
        var selectedModels = GameNodes.SelectedHolderModels();
        foreach (var model in selectedModels)
        {
            selected?.Add(model);
        }

        var cards = new JsonArray();
        // 同一个 CardModel 可能同时出现在两个 holder 里（网格的预览/克隆），按实例去重，否则
        // 同一张牌会占两行、ref 也重复，selected_count 还会多算一次。
        var seenModels = new HashSet<CardModel>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < holders.Count; i++)
        {
            var holder = holders[i];
            var model = holder.CardModel;
            if (model == null || !seenModels.Add(model))
            {
                continue;
            }

            var card = BuildCard(model, i, evaluatePlayable: false);
            card["selected"] = (selected != null && selected.Contains(model)) || selectedModels.Any(model2 => ReferenceEquals(model2, model));
            cards.Add(card);
        }

        var result = new JsonObject
        {
            ["kind"] = "card",
            ["screen_type"] = (GameNodes.CurrentScreen() as Node)?.GetType().Name,
            ["prompt"] = SelectionPrompt(),
            ["cards"] = cards,
            ["confirm_available"] = SelectionConfirmAvailable(),
            ["skip_available"] = GameNodes.FindSelectionScreen() is { } active ? GameNodes.SkipButton(active.Screen) != null : false,
        };

        var meta = screenNode != null ? ReadSelectionPrefs(screenNode) : null;
        if (meta != null)
        {
            result["min"] = meta.Value.Min;
            if (meta.Value.Max >= 1000)
            {
                result["max"] = null;
                result["unbounded"] = true;
            }
            else
            {
                result["max"] = meta.Value.Max;
            }

            result["requires_confirmation"] = meta.Value.RequireConfirm;

            var selectedRefs = new JsonArray();
            var seenRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var card in cards)
            {
                if (card?["selected"]?.GetValue<bool>() == true && card["ref"] is JsonNode rf)
                {
                    var text = rf.GetValue<string>();
                    if (seenRefs.Add(text))
                    {
                        selectedRefs.Add(rf.DeepClone());
                    }
                }
            }

            result["selected_count"] = Math.Max(selectedRefs.Count, selectedModels.Count);
            result["selected_refs"] = selectedRefs;
        }
        else
        {
            result["selected_count"] = selectedModels.Count;
            result["selected_refs"] = new JsonArray();
        }

        return result;
    }

    /// <summary>选遗物界面（事件/遗物奖励会弹）。</summary>
    private static JsonObject BuildRelicSelection(Node screen)
    {
        var holders = GameNodes.RelicHolders(screen);
        var relics = new JsonArray();
        for (var i = 0; i < holders.Count; i++)
        {
            var model = holders[i].Relic?.Model;
            if (model == null)
            {
                continue;
            }

            relics.Add(new JsonObject
            {
                ["i"] = i,
                ["id"] = model.Id.Entry,
                ["name"] = SafeTitle(model),
                ["rarity"] = model.Rarity.ToString(),
            });
        }

        return new JsonObject
        {
            ["kind"] = "relic",
            ["screen_type"] = screen.GetType().Name,
            ["prompt"] = SafeLoc(() => screen is NChooseARelicSelection selection ? "选择一件遗物。" : "选择一件遗物。"),
            ["relics"] = relics,
            ["min"] = 1,
            ["max"] = 1,
            ["confirm_available"] = GameNodes.FindAll<NConfirmButton>(screen)
                .Any(button => GodotObject.IsInstanceValid(button) && button.IsVisibleInTree() && button.IsEnabled),
            ["skip_available"] = GameNodes.SkipButton(screen) != null,
        };
    }

    /// <summary>水晶球小游戏：先如实报出来，避免模型以为卡死。</summary>
    private static JsonObject BuildCrystalSphere(Node screen)
    {
        return new JsonObject
        {
            ["kind"] = "crystal_sphere",
            ["screen_type"] = screen.GetType().Name,
            ["prompt"] = "水晶球小游戏（暂不支持自动操作）",
            ["unsupported"] = true,
            ["note"] = "这个界面需要手动完成；先别提交动作，等它自己结束或用 confirm_modal/dismiss_modal 收尾。",
            ["confirm_available"] = GameNodes.FindAll<NConfirmButton>(screen)
                .Any(button => GodotObject.IsInstanceValid(button) && button.IsVisibleInTree() && button.IsEnabled),
            ["skip_available"] = GameNodes.SkipButton(screen) != null,
        };
    }

    /// <summary>「选一包卡」：每包列出卡牌，index 即包下标。</summary>
    private static JsonObject BuildBundleSelection(NChooseABundleSelectionScreen screen)
    {
        var bundles = GameNodes.Bundles(screen);
        var arr = new JsonArray();
        for (var i = 0; i < bundles.Count; i++)
        {
            var cards = new JsonArray();
            foreach (var model in bundles[i].Bundle ?? Array.Empty<CardModel>())
            {
                cards.Add(BuildCard(model, null, evaluatePlayable: false));
            }

            arr.Add(new JsonObject
            {
                ["i"] = i,
                ["cards"] = cards,
            });
        }

        var prompt = "选择一包卡牌。";
        try
        {
            var banner = screen.GetNodeOrNull<Control>("Banner");
            if (banner != null)
            {
                var label = banner.GetNodeOrNull<Control>("Label") ?? banner;
                prompt = ReadText(label);
            }
        }
        catch
        {
        }

        var confirm = GameNodes.FindAll<NConfirmButton>(screen)
            .FirstOrDefault(button => GodotObject.IsInstanceValid(button) && button.IsVisibleInTree() && button.IsEnabled);

        return new JsonObject
        {
            ["kind"] = "bundle",
            ["screen_type"] = screen.GetType().Name,
            ["prompt"] = string.IsNullOrWhiteSpace(prompt) ? "选择一包卡牌。" : prompt,
            ["bundles"] = arr,
            ["confirm_available"] = confirm != null,
            ["min"] = 1,
            ["max"] = 1,
        };
    }

    /// <summary>读游戏自己的已选卡集合（_selectedCards）；读不到就返回 null。</summary>
    private static HashSet<CardModel>? ReadSelectedCards(Node? node)
    {
        if (node == null)
        {
            return null;
        }

        try
        {
            var raw = ReadInstanceField(node, "_selectedCards");
            if (raw is not System.Collections.IEnumerable enumerable || raw is string)
            {
                return null;
            }

            var set = new HashSet<CardModel>(ReferenceEqualityComparer.Instance);
            foreach (var item in enumerable)
            {
                if (item is CardModel card)
                {
                    set.Add(card);
                }
            }

            return set;
        }
        catch
        {
            return null;
        }
    }

    private static (int Min, int Max, bool RequireConfirm)? ReadSelectionPrefs(Node node)
    {
        try
        {
            var prefs = ReadInstanceField(node, "_prefs");
            if (prefs == null)
            {
                return null;
            }

            return (
                ReadMember<int>(prefs, "MinSelect"),
                ReadMember<int>(prefs, "MaxSelect"),
                ReadMember<bool>(prefs, "RequireManualConfirmation"));
        }
        catch
        {
            return null;
        }
    }
    private static object? ReadInstanceField(object target, string name)
    {
        for (var type = target.GetType(); type != null; type = type.BaseType)
        {
            var field = type.GetField(name, System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.DeclaredOnly);
            if (field != null)
            {
                return field.GetValue(target);
            }
        }

        return null;
    }

    private static T ReadMember<T>(object target, string name)
    {
        var property = target.GetType().GetProperty(name, System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        if (property?.GetValue(target) is T value)
        {
            return value;
        }

        return default!;
    }

    public static string SelectionPrompt()
    {
        try
        {
            var hand = NCombatRoom.Instance?.Ui?.Hand;
            if (hand != null)
            {
                foreach (var name in new[] { "%SelectionHeader", "SelectionHeader" })
                {
                    if (hand.GetNodeOrNull<Control>(name) is { } control)
                    {
                        return ReadText(control);
                    }
                }
            }

            if (GameNodes.CurrentScreen() is Node screen)
            {
                foreach (var name in new[] { "%BottomLabel", "%InfoLabel", "BottomLabel", "InfoLabel" })
                {
                    if (screen.GetNodeOrNull<Control>(name) is { } control)
                    {
                        return ReadText(control);
                    }
                }
            }
        }
        catch
        {
        }

        return "请选择卡牌。";
    }

    /// <summary>Godot 节点上的文本：不同类分别是 MegaLabel / RichTextLabel，统一用反射取 Text。</summary>
    private static string ReadText(Node node)
    {
        try
        {
            var text = node.GetType().GetProperty("Text")?.GetValue(node)?.ToString();
            return GameNodes.Clean(text);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool SelectionConfirmAvailable()
    {
        if (NCombatRoom.Instance?.Ui?.Hand is { } hand && hand.IsInCardSelection)
        {
            return true;
        }

        return GameNodes.CurrentScreen() is Node screen
            && GameNodes.FindAll<NConfirmButton>(screen)
                .Any(button => GodotObject.IsInstanceValid(button) && button.IsVisibleInTree() && button.IsEnabled);
    }

    // ----------------------------------------------------------------- modal

    private static JsonObject? BuildModal()
    {
        var modal = GameNodes.OpenModal();
        if (modal == null)
        {
            return null;
        }

        var buttons = new JsonArray();
        if (modal is Node node)
        {
            foreach (var button in GameNodes.FindAll<NClickableControl>(node))
            {
                if (GodotObject.IsInstanceValid(button) && button.IsVisibleInTree())
                {
                    buttons.Add(new JsonObject
                    {
                        ["name"] = GameNodes.Clean(button.Name),
                        ["enabled"] = button.IsEnabled,
                    });
                }
            }
        }

        return new JsonObject
        {
            ["type"] = modal.GetType().Name,
            ["buttons"] = buttons,
        };
    }

    // ------------------------------------------------------ character select

    private static JsonObject? BuildCharacterSelect()
    {
        if (GameNodes.CurrentScreen() is not NCharacterSelectScreen screen)
        {
            return null;
        }

        var characters = new JsonArray();
        var buttons = GameNodes.FindAll<NCharacterSelectButton>(screen)
            .Where(button => GodotObject.IsInstanceValid(button) && button.IsVisibleInTree())
            .ToList();
        for (var i = 0; i < buttons.Count; i++)
        {
            var button = buttons[i];
            characters.Add(new JsonObject
            {
                ["i"] = i,
                ["id"] = button.Character?.Id.Entry,
                ["name"] = button.Character == null ? null : SafeTitle(button.Character),
                ["locked"] = button.IsLocked,
                ["selected"] = button.IsSelected,
                ["random"] = button.IsRandom,
            });
        }

        return new JsonObject
        {
            ["characters"] = characters,
            ["can_embark"] = GameNodes.FindAll<NConfirmButton>(screen)
                .Any(button => GodotObject.IsInstanceValid(button) && button.IsVisibleInTree() && button.IsEnabled),
        };
    }

    private static JsonObject? BuildSingleplayerMenu()
    {
        var game = NGame.Instance;
        if (game == null)
        {
            return null;
        }

        var submenu = GameNodes.FindAll<NSingleplayerSubmenu>(game)
            .FirstOrDefault(node => GodotObject.IsInstanceValid(node) && node.IsVisibleInTree());
        if (submenu == null)
        {
            return null;
        }

        var options = new JsonArray();
        var buttons = GameNodes.FindAll<NSubmenuButton>(submenu);
        for (var i = 0; i < buttons.Count; i++)
        {
            var button = buttons[i];
            if (!GodotObject.IsInstanceValid(button))
            {
                continue;
            }

            options.Add(new JsonObject
            {
                ["i"] = i,
                ["name"] = GameNodes.Clean(button.Name),
                ["enabled"] = button.IsEnabled,
            });
        }

        return new JsonObject
        {
            ["options"] = options,
            ["can_back"] = GameNodes.FindAll<NBackButton>(submenu).Any(button => GameNodes.IsUsable(button)),
        };
    }

    private static JsonObject? BuildGameOver()
    {
        if (GameNodes.CurrentScreen() is not Node screen)
        {
            return new JsonObject { ["can_continue"] = false, ["can_main_menu"] = false };
        }

        bool Visible(string keyword) => GameNodes.FindAll<NClickableControl>(screen)
            .Any(button => GodotObject.IsInstanceValid(button) && button.IsVisibleInTree() && button.IsEnabled
                && GameNodes.Clean(button.Name).Contains(keyword, StringComparison.OrdinalIgnoreCase));

        var canContinue = Visible("Continue");
        var canMainMenu = Visible("MainMenu") || Visible("Return");
        return new JsonObject
        {
            ["can_continue"] = canContinue,
            ["can_main_menu"] = canMainMenu,
            ["phase"] = canMainMenu ? "summary_ready" : (canContinue ? "summary_animating" : "waiting"),
        };
    }

    // ---------------------------------------------------------------- actions

    private static JsonArray BuildActions(JsonObject state)
    {
        var actions = new JsonArray();
        var screen = state["screen"]?.GetValue<string>() ?? Screen.Unknown;
        switch (screen)
        {
            case Screen.Combat:
            {
                var combat = state["combat"] as JsonObject;
                var me = combat?["me"] as JsonObject;
                var myTurn = combat?["my_turn"]?.GetValue<bool>() == true;
                var playPhase = combat?["turn_phase"]?.GetValue<string>() == "player_turn";
                if (myTurn && playPhase)
                {
                    if ((combat?["hand"] as JsonArray)?.Any(card => card?["playable"]?.GetValue<bool>() == true) == true)
                    {
                        actions.Add("play_card");
                    }

                    actions.Add("end_turn");
                }

                var hasPotion = (me?["potions"] as JsonArray)?.Any(potion => potion?["empty"]?.GetValue<bool>() == false) == true;
                if (hasPotion)
                {
                    if (myTurn)
                    {
                        actions.Add("use_potion");
                    }

                    actions.Add("discard_potion");
                }

                break;
            }

            case Screen.CardSelection:
            {
                var kind = state["selection"]?["kind"]?.GetValue<string>();
                if (kind == "bundle") actions.Add("choose_bundle");
                else if (kind == "relic") actions.Add("choose_relic");
                else if (kind != "crystal_sphere") actions.Add("select_card");

                if (state["selection"]?["confirm_available"]?.GetValue<bool>() == true)
                {
                    actions.Add("confirm_selection");
                }

                if (state["selection"]?["skip_available"]?.GetValue<bool>() == true)
                {
                    actions.Add("skip_selection");
                }

                break;
            }
            case Screen.Map:
                if ((state["map"]?["options"] as JsonArray)?.Count > 0)
                {
                    actions.Add("choose_map_node");
                }

                break;
            case Screen.Event:
                if ((state["event"]?["options"] as JsonArray)?.Count > 0)
                {
                    actions.Add("choose_event_option");
                }

                if (state["event"]?["finished"]?.GetValue<bool>() == true)
                {
                    actions.Add("proceed");
                }

                break;
            case Screen.Rest:
                if ((state["rest"]?["options"] as JsonArray)?.Any(option => option?["enabled"]?.GetValue<bool>() == true) == true)
                {
                    actions.Add("choose_rest_option");
                }

                if (state["rest"]?["can_proceed"]?.GetValue<bool>() == true)
                {
                    actions.Add("proceed");
                }

                break;
            case Screen.Chest:
                if (state["chest"]?["opened"]?.GetValue<bool>() != true)
                {
                    actions.Add("open_chest");
                }

                if ((state["chest"]?["relics"] as JsonArray)?.Count > 0)
                {
                    actions.Add("choose_treasure_relic");
                }

                if (state["chest"]?["can_proceed"]?.GetValue<bool>() == true)
                {
                    actions.Add("proceed");
                }

                break;
            case Screen.Shop:
                actions.Add("open_shop_inventory");
                actions.Add("buy_card");
                actions.Add("buy_relic");
                actions.Add("buy_potion");
                actions.Add("remove_card_at_shop");
                if (state["shop"]?["open"]?.GetValue<bool>() == true)
                {
                    actions.Add("close_shop_inventory");
                }

                // 商店的 exit 按钮可能要先开关一次货架才出现，所以 proceed 常驻。
                actions.Add("proceed");

                break;
            case Screen.Reward:
                if (state["reward"]?["card_choice"]?.GetValue<bool>() == true)
                {
                    actions.Add("choose_reward_card");
                    actions.Add("skip_reward_cards");
                }
                else
                {
                    actions.Add("claim_reward");
                    // 没有继续按钮时 proceed 会去点「跳过」箭头（Boss 的「搜刮！」面板就是这样）。
                    actions.Add("proceed");
                }

                break;
            case Screen.CharacterSelect:
                actions.Add("choose_character");
                actions.Add("embark");
                break;
            case Screen.SingleplayerMenu:
                actions.Add("start_standard_run");
                actions.Add("back");
                break;
            case Screen.MainMenu:
                if (HasContinueButton())
                {
                    actions.Add("continue_run");
                }

                actions.Add("open_singleplayer");
                break;
            case Screen.Modal:
                actions.Add("confirm_modal");
                actions.Add("dismiss_modal");
                break;
            case Screen.GameOver:
                actions.Add("continue_game_over");
                break;
        }

        // 只要地图上有点可选（包括奖励屏还没让位的情况），就允许 choose_map_node。
        if ((state["map"]?["options"] as JsonArray)?.Count > 0 && !actions.Contains("choose_map_node"))
        {
            actions.Add("choose_map_node");
        }

        return actions;
    }

    /// <summary>主菜单的「继续」按钮只有存在存档时才可见可用。</summary>
    private static bool HasContinueButton()
    {
        try
        {
            var menu = NGame.Instance?.MainMenu;
            if (menu == null)
            {
                return false;
            }

            var button = menu.GetNodeOrNull<NClickableControl>("MainMenuTextButtons/ContinueButton");
            return button != null && GodotObject.IsInstanceValid(button) && button.IsVisibleInTree() && button.IsEnabled;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>给 World 的「现在能不能动 / 为什么不能」。</summary>
    public static JsonObject Readiness()
    {
        var manager = CombatManager.Instance;
        var state = TryCombatState();
        var me = state != null ? SafeMe(state) : null;

        if (me == null)
        {
            return new JsonObject
            {
                ["can_act"] = false,
                ["reason"] = "no_player",
                ["my_turn"] = false,
                ["all_ready"] = false,
            };
        }

        var reasons = new List<string>();
        if (!manager.IsInProgress)
        {
            reasons.Add("not_in_combat");
        }

        if (manager.IsPaused)
        {
            reasons.Add("paused");
        }

        if (manager.PlayerActionsDisabled)
        {
            reasons.Add("actions_disabled");
        }

        if (manager.IsOverOrEnding)
        {
            reasons.Add("combat_ending");
        }

        if (!me.Creature.IsAlive)
        {
            reasons.Add("dead");
        }

        var myTurn = manager.IsPartOfPlayerTurn(me);
        if (!myTurn)
        {
            reasons.Add("not_my_turn");
        }
        else if (me.PlayerCombatState?.Phase != PlayerTurnPhase.Play)
        {
            reasons.Add("turn_starting");
        }

        return new JsonObject
        {
            ["can_act"] = reasons.Count == 0,
            ["reason"] = reasons.Count == 0 ? "ready" : reasons[0],
            ["reasons"] = new JsonArray(reasons.Select(reason => (JsonNode)reason!).ToArray()),
            ["my_turn"] = myTurn,
            ["all_ready"] = manager.AllPlayersReadyToEndTurn(),
        };
    }
}
