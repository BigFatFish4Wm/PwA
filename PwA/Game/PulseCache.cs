using System.Text.Json.Nodes;

namespace PwA.Game;

/// <summary>
/// HTTP 线程只能读这里的缓存值。Godot 节点和游戏状态只在游戏线程上读，
/// 从别的线程碰它们会把主线程卡死（已经踩过：World 每几秒轮询 /health 就会冻一次）。
/// </summary>
internal static class PulseCache
{
    public static volatile string Screen = "UNKNOWN";
    public static volatile bool InCombat;
    public static volatile bool InRun;
    public static volatile bool MyTurn;
    public static volatile bool CanAct;
    public static volatile int Turn;
    public static volatile int Floor;
    public static volatile string Role = "unknown";
    public static long UpdatedAtTicks;

    /// <summary>上一次交出去的状态签名，用来判断「已经稳了」还是「正在变」。</summary>
    public static volatile string LastSignature = string.Empty;

    public static void Update(JsonObject lite)
    {
        Screen = lite["screen"]?.GetValue<string>() ?? "UNKNOWN";
        InCombat = lite["in_combat"]?.GetValue<bool>() == true;
        Turn = lite["turn"]?.GetValue<int>() ?? 0;
        Floor = lite["floor"]?.GetValue<int>() ?? 0;
        Role = InstanceRole.Current.ToString().ToLowerInvariant();
        UpdatedAtTicks = DateTime.UtcNow.Ticks;
    }

    public static void Update(JsonObject lite, bool myTurn, bool canAct, bool inRun)
    {
        Update(lite);
        MyTurn = myTurn;
        CanAct = canAct;
        InRun = inRun;
    }
}
