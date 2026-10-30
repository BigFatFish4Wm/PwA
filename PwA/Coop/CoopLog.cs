using MegaCrit.Sts2.Core.Logging;

namespace PwA.Coop;

/// <summary>
/// 联机调试日志：同时写游戏日志和 %LOCALAPPDATA%\PwA\coop-&lt;pid&gt;.log。
/// 游戏日志会随窗口重启轮转，两个窗口互相覆盖，排查联机问题必须用独立文件。
/// </summary>
public static class CoopLog
{
    private static readonly object Gate = new();
    private static string? _path;

    public static string Path
    {
        get
        {
            if (_path != null)
            {
                return _path;
            }

            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PwA");
            Directory.CreateDirectory(dir);
            _path = System.IO.Path.Combine(dir, $"coop-{Environment.ProcessId}.log");
            return _path;
        }
    }

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{Environment.ProcessId}] {message}";
        lock (Gate)
        {
            try
            {
                File.AppendAllText(Path, line + Environment.NewLine);
            }
            catch
            {
            }
        }

        try
        {
            Log.Info($"[PwA.Coop] {message}");
        }
        catch
        {
        }
    }
}
