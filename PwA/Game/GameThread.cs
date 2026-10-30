using System.Threading;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes;
using Environment = System.Environment;

namespace PwA.Game;

/// <summary>
/// 游戏逻辑只能在 Godot 主线程上跑。Mod 一加载就抓住当时的 SynchronizationContext，
/// 之后所有 HTTP 线程都通过它把工作投递回主线程，并等结果。
/// </summary>
internal static class GameThread
{
    private const string LogPrefix = "[PwA.GameThread]";

    private static readonly object Gate = new();
    private static SynchronizationContext? _syncContext;
    private static int _threadId;

    public static void Initialize()
    {
        lock (Gate)
        {
            _syncContext = SynchronizationContext.Current;
            _threadId = Environment.CurrentManagedThreadId;
            if (_syncContext == null)
            {
                Log.Error($"{LogPrefix} 没抓到 SynchronizationContext，动作将不可用。");
                return;
            }

            Log.Info($"{LogPrefix} 已捕获游戏线程（managed thread {_threadId}）");
        }
    }

    public static bool IsCurrentThread => Environment.CurrentManagedThreadId == _threadId;

    public static bool IsReady => _syncContext != null;

    public static Task<T> InvokeAsync<T>(Func<T> action)
    {
        var context = _syncContext ?? throw new InvalidOperationException("game thread context not initialized");
        if (Environment.CurrentManagedThreadId == _threadId)
        {
            return Task.FromResult(action());
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Post(_ =>
        {
            try
            {
                tcs.TrySetResult(action());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }, null);
        return tcs.Task;
    }

    public static Task<T> InvokeAsync<T>(Func<Task<T>> action)
    {
        var context = _syncContext ?? throw new InvalidOperationException("game thread context not initialized");
        if (Environment.CurrentManagedThreadId == _threadId)
        {
            return action();
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Post(_ => _ = RunAsync(action, tcs), null);
        return tcs.Task;
    }

    public static Task InvokeAsync(Action action) => InvokeAsync(() =>
    {
        action();
        return true;
    });

    public static Task InvokeAsync(Func<Task> action) => InvokeAsync(async () =>
    {
        await action();
        return true;
    });

    private static async Task RunAsync<T>(Func<Task<T>> action, TaskCompletionSource<T> tcs)
    {
        try
        {
            tcs.TrySetResult(await action());
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
    }

    /// <summary>等一帧。窗口被遮挡时 ProcessFrame 可能长时间不来，所以带 50ms 上限。</summary>
    public static async Task WaitForNextFrameAsync()
    {
        if (_syncContext == null)
        {
            await Task.Delay(16);
            return;
        }

        if (!IsCurrentThread)
        {
            await InvokeAsync(WaitForNextFrameCoreAsync);
            return;
        }

        await WaitForNextFrameCoreAsync();
    }

    private static async Task WaitForNextFrameCoreAsync()
    {
        var game = NGame.Instance;
        if (game == null || !GodotObject.IsInstanceValid(game))
        {
            await Task.Delay(16);
            return;
        }

        var tree = game.GetTree();
        if (tree == null || !GodotObject.IsInstanceValid(tree))
        {
            await Task.Delay(16);
            return;
        }

        var frame = NextFrameAsync(game, tree);
        var completed = await Task.WhenAny(frame, Task.Delay(50));
        if (completed == frame)
        {
            await frame;
        }
    }

    private static async Task NextFrameAsync(NGame game, SceneTree tree)
    {
        await game.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
    }
}
