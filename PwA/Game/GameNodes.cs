using System.Text.RegularExpressions;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Rooms;
using PwA.Server;

namespace PwA.Game;

/// <summary>当前画面。取值与旧版本保持一致，方便 World 侧沿用。</summary>
internal static class Screen
{
    public const string Unknown = "UNKNOWN";
    public const string MainMenu = "MAIN_MENU";
    public const string CharacterSelect = "CHARACTER_SELECT";
    public const string SingleplayerMenu = "SINGLEPLAYER_MENU";
    public const string MultiplayerMenu = "MULTIPLAYER_MENU";
    public const string Map = "MAP";
    public const string Combat = "COMBAT";
    public const string Event = "EVENT";
    public const string Rest = "REST";
    public const string Shop = "SHOP";
    public const string Chest = "CHEST";
    public const string Reward = "REWARD";
    public const string CardSelection = "CARD_SELECTION";
    public const string GameOver = "GAME_OVER";
    public const string Modal = "MODAL";
}

/// <summary>UI 节点上的公共操作：找节点、看画面、点按钮、清文本。</summary>
internal static class GameNodes
{
    public static IScreenContext? CurrentScreen()
    {
        try
        {
            return ActiveScreenContext.Instance?.GetCurrentScreen();
        }
        catch
        {
            return null;
        }
    }

    public static IScreenContext? OpenModal()
    {
        try
        {
            return NModalContainer.Instance?.OpenModal;
        }
        catch
        {
            return null;
        }
    }

    public static string ResolveScreen()
    {
        try
        {
            if (OpenModal() != null)
            {
                return Screen.Modal;
            }

            var screen = CurrentScreen();

            if (NCombatRoom.Instance?.Ui?.Hand is { } hand && hand.IsInCardSelection)
            {
                return Screen.CardSelection;
            }

            // 选牌/选遗物/选卡包/水晶球都是覆盖层，ActiveScreenContext 常常指向底下的房间；
            // 只要树上有一个可见的选择界面，就按 CARD_SELECTION 处理（kind 在 state.selection 里区分）。
            if (FindSelectionScreen() != null)
            {
                return Screen.CardSelection;
            }

            if (screen is NGameOverScreen)
            {
                return Screen.GameOver;
            }

            // 奖励面板是覆盖层，Boss 结算时会盖在仍然处于 ActiveCombat 的战斗房间上，必须先判它。
            // 但领完奖励后它常常只是「还在树上、内容已空」，这时要让位给地图/下一个房间。
            if (NGame.Instance is { } gameForOverlay)
            {
                var rewardNode = (Node?)FindVisible<NRewardsScreen>(gameForOverlay)
                    ?? FindVisible<NCardRewardSelectionScreen>(gameForOverlay);
                if (rewardNode != null)
                {
                    var otherRoomVisible = FindVisible<NMapScreen>(gameForOverlay) != null
                        || FindVisible<NMerchantRoom>(gameForOverlay) != null
                        || FindVisible<NRestSiteRoom>(gameForOverlay) != null
                        || FindVisible<NEventRoom>(gameForOverlay) != null
                        || FindVisible<NTreasureRoom>(gameForOverlay) != null;

                    if (HasRewardContent(rewardNode) || !otherRoomVisible)
                    {
                        return Screen.Reward;
                    }
                }
            }

            var combatRoom = screen as NCombatRoom ?? (CombatManager.Instance.IsInProgress ? FindActiveCombatRoom() : null);
            if (combatRoom is { Mode: CombatRoomMode.ActiveCombat })
            {
                return Screen.Combat;
            }

            if (screen is NCharacterSelectScreen)
            {
                return Screen.CharacterSelect;
            }

            if (NGame.Instance is { } game)
            {
                // 子菜单栈里上一层的节点可能仍然 visible，所以先看是否有更具体的画面在上面。
                if (FindAll<NSingleplayerSubmenu>(game).Any(node => GodotObject.IsInstanceValid(node) && node.IsVisibleInTree()))
                {
                    return Screen.SingleplayerMenu;
                }

                if (FindAll<NMultiplayerSubmenu>(game).Any(node => GodotObject.IsInstanceValid(node) && node.IsVisibleInTree()))
                {
                    return Screen.MultiplayerMenu;
                }
            }

            var mapped = screen switch
            {
                NRewardsScreen => Screen.Reward,
                NCardRewardSelectionScreen => Screen.Reward,
                NCardGridSelectionScreen => Screen.CardSelection,
                NChooseACardSelectionScreen => Screen.CardSelection,
                NTreasureRoomRelicCollection => Screen.Chest,
                NTreasureRoom => Screen.Chest,
                NRestSiteRoom => Screen.Rest,
                NMerchantInventory => Screen.Shop,
                NMerchantRoom => Screen.Shop,
                NEventRoom => Screen.Event,
                NMapScreen => Screen.Map,
                NMapRoom => Screen.Map,
                NCharacterSelectScreen => Screen.CharacterSelect,
                NMainMenu => Screen.MainMenu,
                NSubmenu => Screen.MainMenu,
                NLogoAnimation => Screen.MainMenu,
                _ => null,
            };

            // ActiveScreenContext 有时给 null 或给不到房间节点（进事件/读档/转场时都遇到过），
            // 这时按「树上可见的界面节点」兜底判断，否则会停在 UNKNOWN 什么也做不了。
            return mapped ?? VisibleScreenFallback() ?? Screen.Unknown;
        }
        catch
        {
            return Screen.Unknown;
        }
    }

    /// <summary>当前可见的地图屏。</summary>
    public static NMapScreen? VisibleMapScreen()
    {
        var game = NGame.Instance;
        if (game == null || !GodotObject.IsInstanceValid(game))
        {
            return null;
        }

        return FindVisible<NMapScreen>(game);
    }

    /// <summary>奖励屏里还有没有「能领的东西 / 能点的跳过」。</summary>
    public static bool HasRewardContent(Node rewardNode)
    {
        try
        {
            // 有还能领的奖励
            foreach (var button in FindAll<NRewardButton>(rewardNode))
            {
                if (GodotObject.IsInstanceValid(button) && button.IsVisibleInTree() && button.IsEnabled)
                {
                    return true;
                }
            }

            // 或者继续按钮是亮的（能离场）
            foreach (var proceed in FindAll<NProceedButton>(rewardNode))
            {
                if (GodotObject.IsInstanceValid(proceed) && proceed.IsVisibleInTree() && proceed.IsEnabled)
                {
                    return true;
                }
            }

            foreach (var holder in FindAll<NCardHolder>(rewardNode))
            {
                if (GodotObject.IsInstanceValid(holder) && holder.IsVisibleInTree() && holder.CardModel != null)
                {
                    return true;
                }
            }

            foreach (var control in FindAll<NClickableControl>(rewardNode))
            {
                if (!GodotObject.IsInstanceValid(control) || !control.IsVisibleInTree() || !control.IsEnabled)
                {
                    continue;
                }

                var name = Clean(control.Name);
                if (name.Contains("Skip", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Claim", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Continue", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    /// <summary>按可见节点兜底判断画面；顺序＝覆盖层优先于房间。</summary>
    private static string? VisibleScreenFallback()
    {
        var game = NGame.Instance;
        if (game == null || !GodotObject.IsInstanceValid(game))
        {
            return null;
        }

        if (FindVisible<NGameOverScreen>(game) != null) return Screen.GameOver;
        if (FindSelectionScreen() != null) return Screen.CardSelection;
        var rewardNode = (Node?)FindVisible<NCardRewardSelectionScreen>(game) ?? FindVisible<NRewardsScreen>(game);
        if (rewardNode != null)
        {
            var otherRoom = FindVisible<NMapScreen>(game) != null
                || FindVisible<NMerchantRoom>(game) != null
                || FindVisible<NRestSiteRoom>(game) != null
                || FindVisible<NEventRoom>(game) != null
                || FindVisible<NTreasureRoom>(game) != null;
            if (HasRewardContent(rewardNode) || !otherRoom) return Screen.Reward;
        }
        if (FindVisible<NCombatRoom>(game) is { Mode: CombatRoomMode.ActiveCombat }) return Screen.Combat;
        if (FindVisible<NTreasureRoomRelicCollection>(game) != null || FindVisible<NTreasureRoom>(game) != null) return Screen.Chest;
        if (FindVisible<NRestSiteRoom>(game) != null) return Screen.Rest;
        if (FindVisible<NMerchantInventory>(game) != null || FindVisible<NMerchantRoom>(game) != null) return Screen.Shop;
        if (FindVisible<NEventRoom>(game) != null) return Screen.Event;
        if (FindVisible<NMapScreen>(game) != null) return Screen.Map;
        if (FindVisible<NCharacterSelectScreen>(game) != null) return Screen.CharacterSelect;
        if (FindVisible<NMainMenu>(game) != null) return Screen.MainMenu;
        return null;
    }

    public static T? FindVisible<T>(Node root) where T : CanvasItem
        => FindAll<T>(root).FirstOrDefault(node => GodotObject.IsInstanceValid(node) && node.IsVisibleInTree());

    /// <summary>战斗界面即使不是当前 screen（过场/结算）也要能找到，用于「我能不能动」。</summary>
    public static NCombatRoom? FindActiveCombatRoom()
    {
        var game = NGame.Instance;
        if (game == null || !GodotObject.IsInstanceValid(game))
        {
            return null;
        }

        foreach (var room in FindAll<NCombatRoom>(game))
        {
            if (GodotObject.IsInstanceValid(room) && room.IsVisibleInTree() && room.Mode == CombatRoomMode.ActiveCombat)
            {
                return room;
            }
        }

        return null;
    }

    public static List<T> FindAll<T>(Node root) where T : Node
    {
        var found = new List<T>();
        if (root == null || !GodotObject.IsInstanceValid(root))
        {
            return found;
        }

        Walk(root, found);
        return found;
    }

    private static void Walk<T>(Node node, List<T> found) where T : Node
    {
        if (node is T match)
        {
            found.Add(match);
        }

        foreach (var child in node.GetChildren())
        {
            Walk(child, found);
        }
    }

    public static NProceedButton? ProceedButton() => ProceedButtonAny(requireEnabled: true);

    /// <summary>不看 enabled 的版本：营火在选项动画结束时按钮可能先禁用后启用。</summary>
    public static NProceedButton? ProceedButtonAny(bool requireEnabled = false)
    {
        var screen = CurrentScreen();
        if (screen is NRewardsScreen)
        {
            return Usable(FindAll<NProceedButton>((Node)screen), requireEnabled);
        }

        if (screen is IRoomWithProceedButton roomWithProceed)
        {
            var button = roomWithProceed.ProceedButton;
            return button != null && IsUsable(button, requireEnabled) ? button : null;
        }

        if (screen is Node node)
        {
            return Usable(FindAll<NProceedButton>(node), requireEnabled);
        }

        return null;
    }

    private static NProceedButton? Usable(IEnumerable<NProceedButton> buttons, bool requireEnabled = true) =>
        buttons.FirstOrDefault(button => GodotObject.IsInstanceValid(button)
            && button.IsVisibleInTree()
            && (!requireEnabled || button.IsEnabled));

    public static bool IsUsable(NClickableControl? control, bool requireEnabled = true) =>
        control != null && GodotObject.IsInstanceValid(control) && (!requireEnabled || control.IsEnabled) && control.IsVisibleInTree();

    public static void Press(NClickableControl? control)
    {
        if (control == null || !GodotObject.IsInstanceValid(control))
        {
            throw ApiException.Conflict("ui_missing", "界面里找不到这个按钮。", retryable: true);
        }

        // 主菜单文字按钮的处理器直接挂在 Released 信号上：DebugPress/DebugRelease 依赖内部按下状态，
        // 状态一旦不同步就点不动；直接发信号最稳。其余按钮都吃 ForceClick。
        if (control is MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMainMenuTextButton)
        {
            control.EmitSignal(NClickableControl.SignalName.Released, control);
            return;
        }

        control.ForceClick();
    }

    public static void PressCardHolder(NCardHolder? holder)
    {
        if (holder == null || !GodotObject.IsInstanceValid(holder))
        {
            throw ApiException.Conflict("ui_missing", "界面里找不到这张牌。", retryable: true);
        }

        // 卡牌 holder 的点击处理器挂在它自己的 Pressed 信号上（ForceClick 不触发）。
        holder.EmitSignal(NCardHolder.SignalName.Pressed, holder);
    }

    /// <summary>
    /// 所有「等你做选择」的界面。取自游戏自己的 AutoSlay 界面处理器清单
    /// （AutoSlay.Handlers.Screens：选卡包/选卡/选遗物/牌库选牌/变换/附魔/升级/战斗牌堆/水晶球），
    /// 以后游戏加新界面，只要往这张表补一行。
    /// </summary>
    private static readonly (Type Type, string Kind)[] SelectionKinds =
    {
        (typeof(NChooseABundleSelectionScreen), "bundle"),
        (typeof(NChooseARelicSelection), "relic"),
        (typeof(NCrystalSphereScreen), "crystal_sphere"),
        (typeof(NChooseACardSelectionScreen), "card"),
        (typeof(NCardGridSelectionScreen), "card"),
    };

    /// <summary>当前可见的选择界面（有就按 CARD_SELECTION 处理，kind 区分）。</summary>
    public static (Node Screen, string Kind)? FindSelectionScreen()
    {
        var game = NGame.Instance;
        if (game == null || !GodotObject.IsInstanceValid(game))
        {
            return null;
        }

        (Node Screen, string Kind)? found = null;
        Visit(game, node =>
        {
            if (found != null || node is not CanvasItem canvas || !canvas.IsVisibleInTree())
            {
                return;
            }

            foreach (var (type, kind) in SelectionKinds)
            {
                if (type.IsInstanceOfType(node))
                {
                    found = (node, kind);
                    return;
                }
            }
        });
        return found;
    }

    private static void Visit(Node root, Action<Node> visitor)
    {
        if (root == null || !GodotObject.IsInstanceValid(root))
        {
            return;
        }

        visitor(root);
        foreach (var child in root.GetChildren())
        {
            Visit(child, visitor);
        }
    }

    /// <summary>选遗物界面里可见的遗物，按视觉顺序。</summary>
    public static List<NRelicBasicHolder> RelicHolders(Node screen)
    {
        return FindAll<NRelicBasicHolder>(screen)
            .Where(holder => GodotObject.IsInstanceValid(holder) && holder.IsVisibleInTree())
            .OrderBy(holder => holder.GlobalPosition.X)
            .ThenBy(holder => holder.GlobalPosition.Y)
            .ToList();
    }

    /// <summary>已经选中的手牌（手牌选择界面会把它们移到 NSelectedHandCardContainer）。</summary>
    public static List<CardModel> SelectedHolderModels()
    {
        var game = NGame.Instance;
        if (game == null || !GodotObject.IsInstanceValid(game))
        {
            return new List<CardModel>();
        }

        var seen = new HashSet<CardModel>(ReferenceEqualityComparer.Instance);
        return FindAll<NSelectedHandCardHolder>(game)
            .Where(holder => GodotObject.IsInstanceValid(holder) && holder.IsVisibleInTree() && holder.CardModel != null)
            .Select(holder => holder.CardModel!)
            .Where(model => seen.Add(model))
            .ToList();
    }

    /// <summary>这个选择界面能不能跳过。</summary>
    public static NChoiceSelectionSkipButton? SkipButton(Node screen)
    {
        return FindAll<NChoiceSelectionSkipButton>(screen)
            .FirstOrDefault(button => GodotObject.IsInstanceValid(button) && button.IsVisibleInTree() && button.IsEnabled);
    }

    /// <summary>「卡牌包选一包」界面（涅奥卷轴箱这类遗物/事件会弹）。</summary>
    public static NChooseABundleSelectionScreen? BundleScreen()
    {
        var game = NGame.Instance;
        if (game == null || !GodotObject.IsInstanceValid(game))
        {
            return null;
        }

        return FindAll<NChooseABundleSelectionScreen>(game)
            .FirstOrDefault(node => GodotObject.IsInstanceValid(node) && node.IsVisibleInTree());
    }

    /// <summary>可见的卡包，按视觉顺序（左→右）。</summary>
    public static List<NCardBundle> Bundles(NChooseABundleSelectionScreen screen)
    {
        return FindAll<NCardBundle>(screen)
            .Where(bundle => GodotObject.IsInstanceValid(bundle) && bundle.IsVisibleInTree() && bundle.Bundle != null)
            .OrderBy(bundle => bundle.GlobalPosition.X)
            .ThenBy(bundle => bundle.GlobalPosition.Y)
            .ToList();
    }

    /// <summary>选牌界面里可见的候选牌（按视觉顺序）。</summary>
    /// <remarks>
    /// 有三种承载方式：战斗内手牌选择（NPlayerHand）、网格选择（NGridCardHolder），
    /// 以及「从三张里选一张」的 NChooseACardSelectionScreen（不是网格屏，用的是普通 NCardHolder）。
    /// 早先只扫了网格，导致 PowerPotion 这类「选一张能力牌」的界面读不到候选。
    /// </remarks>
    public static List<NCardHolder> SelectionHolders()
    {
        var result = new List<NCardHolder>();

        if (NCombatRoom.Instance?.Ui?.Hand is { } hand && hand.IsInCardSelection)
        {
            // 战斗内选牌：候选可能还在 CardHolderContainer（ActiveHolders），
            // 也可能已经被移到 SelectedHandCardContainer 里变成 NSelectedHandCardHolder
            // （「选择任意张牌进行替换/消耗」这类多选就是这样）。两处都要收。
            foreach (var holder in FindAll<NCardHolder>(hand))
            {
                if (GodotObject.IsInstanceValid(holder) && holder.IsVisibleInTree() && holder.CardModel != null && !result.Contains(holder))
                {
                    result.Add(holder);
                }
            }

            if (result.Count == 0)
            {
                foreach (var holder in hand.ActiveHolders)
                {
                    if (holder != null && GodotObject.IsInstanceValid(holder) && holder.IsVisibleInTree() && holder.CardModel != null)
                    {
                        result.Add(holder);
                    }
                }
            }

            return result.OrderBy(holder => holder.GlobalPosition.X).ToList();
        }

        void Collect(Node root)
        {
            foreach (var holder in FindAll<NCardHolder>(root))
            {
                if (holder is NHandCardHolder)
                {
                    continue;
                }

                if (GodotObject.IsInstanceValid(holder) && holder.IsVisibleInTree() && holder.CardModel != null && !result.Contains(holder))
                {
                    result.Add(holder);
                }
            }
        }

        if (FindSelectionScreen() is { } selection)
        {
            Collect(selection.Screen);
        }
        else if (CurrentScreen() is Node screen)
        {
            Collect(screen);
        }

        if (result.Count == 0 && NGame.Instance is { } game)
        {
            // 选择界面有时挂在覆盖层容器里，不在 CurrentScreen 下面；只在没找到时做整树扫描。
            Collect(game);
        }

        return result
            .OrderBy(holder => holder.GlobalPosition.Y)
            .ThenBy(holder => holder.GlobalPosition.X)
            .ToList();
    }

    /// <summary>按「选好了」：手牌选择走内部确认；网格界面找确认按钮。</summary>
    public static bool ConfirmSelection()
    {
        var hand = NCombatRoom.Instance?.Ui?.Hand;
        if (hand != null && GodotObject.IsInstanceValid(hand) && hand.IsInCardSelection)
        {
            // 手牌选择的确认按钮是 hand 下面的 SelectModeConfirmButton，直接点它最可靠。
            var confirmButton = hand.GetNodeOrNull<NConfirmButton>("SelectModeConfirmButton")
                ?? FindAll<NConfirmButton>(hand).FirstOrDefault(button => GodotObject.IsInstanceValid(button) && button.IsVisibleInTree() && button.IsEnabled);
            if (confirmButton != null && GodotObject.IsInstanceValid(confirmButton) && confirmButton.IsEnabled)
            {
                confirmButton.ForceClick();
                return true;
            }

            if (hand.HasMethod(NPlayerHand.MethodName.OnSelectModeConfirmButtonPressed))
            {
                hand.Call(NPlayerHand.MethodName.OnSelectModeConfirmButtonPressed);
                return true;
            }
        }

        var confirmRoot = FindSelectionScreen()?.Screen ?? CurrentScreen() as Node;
        if (confirmRoot is Node screen)
        {
            var confirm = FindAll<NConfirmButton>(screen)
                .FirstOrDefault(button => GodotObject.IsInstanceValid(button) && button.IsVisibleInTree() && button.IsEnabled);
            if (confirm != null)
            {
                confirm.ForceClick();
                return true;
            }

            foreach (var name in new[] { "ConfirmSelection", "CompleteSelection" })
            {
                if (screen.HasMethod(name))
                {
                    screen.Call(name);
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 去掉 [gold]…[/gold] 这类富文本标签，压平空白；
    /// 卡面文字里的内联图标会把 res:// 资源路径带进来，按资源名换回「能量 / 辉星 / 金币 / 格挡」。
    /// </summary>
    public static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = value;
        text = Regex.Replace(text, @"res://[^\s\]\[]*energy[^\s\]\[]*", "能量", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"res://[^\s\]\[]*star[^\s\]\[]*", "辉星", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"res://[^\s\]\[]*gold[^\s\]\[]*", "金币", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"res://[^\s\]\[]*block[^\s\]\[]*", "格挡", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"res://[^\s\]\[]+", string.Empty);
        text = Regex.Replace(text, @"\[(?:/?[^\]]+)\]", string.Empty);
        text = Regex.Replace(text, @"\s+", " ");
        return text.Trim();
    }

    public static bool IsMultiplayer()
    {
        try
        {
            var type = MegaCrit.Sts2.Core.Runs.RunManager.Instance?.NetService?.Type;
            return type is MegaCrit.Sts2.Core.Multiplayer.Game.NetGameType.Host or MegaCrit.Sts2.Core.Multiplayer.Game.NetGameType.Client;
        }
        catch
        {
            return false;
        }
    }
}
