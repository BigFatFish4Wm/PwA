using PwA.Game;

namespace PwA.Coop.Transport;

/// <summary>
/// 游戏只在自己的 Update 里抽传输层的包；broker 场景下有时没有人调用（比如主机停在角色选择），
/// 这里用游戏主线程每 15ms 主动抽一次，保证握手包不会一直躺在队列里。
/// </summary>
internal static class BrokerTransportPump
{
    private static readonly object Gate = new();
    private static readonly List<Action> Ticks = new();
    private static int _started;
    private static int _ticks;

    public static void Register(Action tick)
    {
        lock (Gate)
        {
            Ticks.Add(tick);
        }

        CoopLog.Write($"pump 注册 tick（共 {Ticks.Count} 个）");
    }

    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        CoopLog.Write("pump 启动");
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(15);
                try
                {
                    Action[] snapshot;
                    lock (Gate)
                    {
                        snapshot = Ticks.ToArray();
                    }

                    if (snapshot.Length == 0)
                    {
                        continue;
                    }

                    _ = GameThread.InvokeAsync(() =>
                    {
                        foreach (var tick in snapshot)
                        {
                            try
                            {
                                tick();
                                if (++_ticks % 3000 == 0)
                                {
                                    CoopLog.Write($"pump alive ticks={_ticks} actions={snapshot.Length}");
                                }
                            }
                            catch (Exception exception)
                            {
                                CoopLog.Write($"pump tick 失败: {exception.Message}");
                            }
                        }
                    });
                }
                catch
                {
                    // 游戏关了/线程没起来：下一轮再试。
                }
            }
        });
    }
}
