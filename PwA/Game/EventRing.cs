using System.Text.Json.Nodes;

namespace PwA.Game;

/// <summary>事件环：单调 seq，容量 256。World 断线后用 since 补齐，状态仍以 /state 为准。</summary>
internal static class EventRing
{
    private const int Capacity = 256;

    private static readonly object Gate = new();
    private static readonly Queue<JsonObject> Buffer = new();
    private static int _seq;

    public static int LastSeq
    {
        get
        {
            lock (Gate)
            {
                return _seq;
            }
        }
    }

    public static void Record(string type, JsonObject? data = null)
    {
        lock (Gate)
        {
            _seq++;
            var entry = new JsonObject
            {
                ["seq"] = _seq,
                ["type"] = type,
                ["ts"] = DateTimeOffset.Now.ToString("O"),
                ["data"] = data?.DeepClone() ?? new JsonObject(),
            };
            Buffer.Enqueue(entry);
            while (Buffer.Count > Capacity)
            {
                Buffer.Dequeue();
            }
        }
    }

    public static JsonObject Since(int since)
    {
        lock (Gate)
        {
            var events = new JsonArray();
            foreach (var entry in Buffer)
            {
                if ((int)entry["seq"]! <= since)
                {
                    continue;
                }

                events.Add(entry.DeepClone());
            }

            return new JsonObject
            {
                ["events"] = events,
                ["last_seq"] = _seq,
            };
        }
    }
}

/// <summary>
/// 每秒采一次轻量状态，把"画面变了 / 轮到我了 / 要我做选择 / 战斗结束"写进事件环。
/// 只在有变化时产生事件，稳态零噪声。
/// </summary>
internal static class StatePulse
{
    private static Timer? _timer;
    private static string _lastKey = string.Empty;
    private static string _lastAttentionKey = string.Empty;

    public static void Start()
    {
        _timer ??= new Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(1.2), TimeSpan.FromSeconds(1.2));
    }

    public static void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private static async Task TickAsync()
    {
        if (!GameThread.IsReady)
        {
            return;
        }

        try
        {
            var lite = await GameThread.InvokeAsync(StateBuilder.BuildLite);
            if (lite == null)
            {
                return;
            }

            var readiness = await GameThread.InvokeAsync(StateBuilder.Readiness);
            var runState = await GameThread.InvokeAsync(StateBuilder.HasRun);
            PulseCache.Update(lite, readiness["my_turn"]?.GetValue<bool>() == true, readiness["can_act"]?.GetValue<bool>() == true, runState);

            // 「现在是不是在等我操作」：与轻量局面分开算，避免平台差异导致漏推。
            var attention = await GameThread.InvokeAsync(StateBuilder.BuildAttention);
            var attentionKind = attention?["kind"]?.GetValue<string>();
            var attentionKey = string.Join('|',
                attentionKind ?? "-",
                attention?["screen"]?.GetValue<string>() ?? "-",
                attention?["turn"]?.GetValue<int>() ?? 0,
                attention?["detail"]?.GetValue<string>() ?? "-");
            if (attentionKey != _lastAttentionKey)
            {
                _lastAttentionKey = attentionKey;
                if (!string.IsNullOrEmpty(attentionKind) && attention != null)
                {
                    EventRing.Record("attention", attention);
                }
            }

            var key = lite.ToJsonString();
            if (key == _lastKey)
            {
                return;
            }

            _lastKey = key;
            var screen = lite["screen"]?.GetValue<string>() ?? "UNKNOWN";
            var turn = lite["turn"]?.GetValue<int>() ?? 0;
            var choice = lite["choice"]?.GetValue<string>();

            if (choice != null)
            {
                EventRing.Record("player_choice_required", new JsonObject
                {
                    ["screen"] = screen,
                    ["kind"] = choice,
                    ["turn"] = turn,
                });
            }

            if (lite["turn_started"]?.GetValue<bool>() == true)
            {
                EventRing.Record("turn_started", new JsonObject
                {
                    ["screen"] = screen,
                    ["turn"] = turn,
                });
            }

            EventRing.Record("screen_changed", new JsonObject
            {
                ["screen"] = screen,
                ["turn"] = turn,
            });
        }
        catch
        {
            // 游戏还没起来或正在切场景；下一个 tick 再试。
        }
    }
}
