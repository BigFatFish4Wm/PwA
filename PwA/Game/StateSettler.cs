using System.Text;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Runs;
using System.Text.Json.Nodes;

namespace PwA.Game;

/// <summary>
/// 「等局面稳住」：游戏里的出牌、抽牌、伤害、回合切换都是异步演出，动作函数返回的那一刻，
/// 模型状态往往还在逐帧变化（手牌一张张抽、伤害一段段结算）。这里用「签名连续 stableMs 不变」
/// 判定稳定，再把稳定后的状态交出去，避免回执里的 state 落后于画面。
/// </summary>
internal static class StateSettler
{
    private const int StableMs = 180;
    private const int MaxMs = 2500;
    private const int PollMs = 30;

    /// <summary>给状态算一个轻量签名：只包含会随演出变化的字段。</summary>
    public static string Signature(JsonObject state)
    {
        var sb = new StringBuilder(256);
        sb.Append(state["screen"]?.GetValue<string>()).Append('|');
        sb.Append(state["turn"]?.GetValue<int>()).Append('|');
        sb.Append(state["actions"]?.ToJsonString()).Append('|');

        if (state["combat"] is JsonObject combat)
        {
            sb.Append(combat["turn"]?.GetValue<int>()).Append(':')
              .Append(combat["side"]?.GetValue<string>()).Append(':')
              .Append(combat["ending_turn"]?.GetValue<bool>()).Append(':')
              .Append(combat["my_turn"]?.GetValue<bool>()).Append(':');
            if (combat["me"] is JsonObject me)
            {
                sb.Append(me["hp"]?.GetValue<int>()).Append('/')
                  .Append(me["block"]?.GetValue<int>()).Append('/')
                  .Append(me["energy"]?.GetValue<int>()).Append('/')
                  .Append(me["stars"]?.GetValue<int>()).Append(':');
            }

            if (combat["hand"] is JsonArray hand)
            {
                foreach (var card in hand)
                {
                    sb.Append(card?["ref"]?.GetValue<string>()).Append(',');
                }

                sb.Append(':');
            }

            if (combat["enemies"] is JsonArray enemies)
            {
                foreach (var enemy in enemies)
                {
                    sb.Append(enemy?["hp"]?.GetValue<int>()).Append('/')
                      .Append(enemy?["block"]?.GetValue<int>()).Append(',');
                }
            }

            if (combat["piles"] is JsonObject piles)
            {
                sb.Append(':').Append(piles["draw"]?.GetValue<int>())
                  .Append('/').Append(piles["discard"]?.GetValue<int>())
                  .Append('/').Append(piles["exhaust"]?.GetValue<int>());
            }
        }

        foreach (var key in new[] { "selection", "event", "map", "reward", "shop", "rest", "chest", "modal", "character_select", "singleplayer", "game_over" })
        {
            if (state[key] is JsonObject section)
            {
                sb.Append('|').Append(key).Append(':');
                if (section["cards"] is JsonArray cards) sb.Append("cards=").Append(cards.Count);
                if (section["options"] is JsonArray options) sb.Append("options=").Append(options.Count);
                if (section["rewards"] is JsonArray rewards) sb.Append("rewards=").Append(rewards.Count);
                if (section["relics"] is JsonArray relics) sb.Append("relics=").Append(relics.Count);
                if (section["finished"] != null) sb.Append("fin=").Append(section["finished"]?.GetValue<bool>());
                if (section["open"] != null) sb.Append("open=").Append(section["open"]?.GetValue<bool>());
                if (section["can_proceed"] != null) sb.Append("proceed=").Append(section["can_proceed"]?.GetValue<bool>());
                if (section["prompt"] != null) sb.Append("prompt=").Append(section["prompt"]?.GetValue<string>());
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 回合开始前有一段「遗物/状态结算」：此时 my_turn 已经是 true，但能量还是 0、手牌还没抽。
    /// 这不是可决策的局面，回执不该停在这里；刚进战斗的第一帧同理。
    /// </summary>
    public static bool IsPreTurnStart(JsonObject state)
    {
        if (state["screen"]?.GetValue<string>() != Screen.Combat)
        {
            return false;
        }

        if (state["combat"] is not JsonObject combat)
        {
            return true;
        }

        // 游戏自己报的阶段：turn_start_resolution = 遗物/状态还在结算。
        if (combat["turn_phase"]?.GetValue<string>() != "turn_start_resolution")
        {
            return false;
        }

        // 只有「游戏真的在等你做选择」才立刻放行；debuff/buff/遗物/药水本身不构成放行理由，
        // 它们继续等，直到真正进入可出牌阶段。
        if (combat["hand_selection"]?.GetValue<bool>() == true)
        {
            return false;
        }

        if (ChoicePending())
        {
            return false;
        }

        return true;
    }
    /// <summary>游戏是否正停在「等你选牌」上：动作执行器/玩家动作队列被暂停。</summary>
    private static bool ChoicePending()
    {
        try
        {
            var run = RunManager.Instance;
            if (run == null)
            {
                return false;
            }

            if (run.ActionExecutor?.IsPaused == true)
            {
                return true;
            }

            var me = LocalContext.GetMe(run.DebugOnlyGetState());
            return me != null && run.ActionQueueSet?.ActionQueueIsPaused(me.NetId) == true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>状态已经稳就直接返回；正在变化就等到连续 180ms 不变；回合开始结算阶段最多再等 2.5s。</summary>
    public static async Task<JsonObject> WaitAsync()
    {
        var previous = PulseCache.LastSignature;
        var state = StateBuilder.Build();
        var signature = Signature(state);
        if (string.Equals(signature, previous, StringComparison.Ordinal) && !IsPreTurnStart(state))
        {
            PulseCache.LastSignature = signature;
            return state;
        }

        var last = signature;
        var stableSince = DateTime.UtcNow;
        var deadline = DateTime.UtcNow.AddMilliseconds(MaxMs);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollMs);
            state = StateBuilder.Build();
            signature = Signature(state);
            if (!string.Equals(signature, last, StringComparison.Ordinal))
            {
                last = signature;
                stableSince = DateTime.UtcNow;
                continue;
            }

            if ((DateTime.UtcNow - stableSince).TotalMilliseconds < StableMs)
            {
                continue;
            }

            // 转场/读档时画面会短暂是 UNKNOWN，这时候交出去只会让模型以为游戏没画面。
            var screenNow = state["screen"]?.GetValue<string>();
            if (!IsPreTurnStart(state) && screenNow != Screen.Unknown)
            {
                break;
            }

            // 回合开始结算还没走完：继续等，但总时长受 deadline（2.5s）限制。
        }

        PulseCache.LastSignature = last;
        return state;
    }
}
