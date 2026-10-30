using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MegaCrit.Sts2.Core.Logging;

namespace PwA.Server;

/// <summary>
/// 端点文件：每个游戏窗口在 %LOCALAPPDATA%\PwA\endpoints\&lt;pid&gt;.json 留一张名片，
/// World 不需要猜端口。进程退出时删除；崩溃留下的文件由 World 侧按 /health 探活过滤。
/// </summary>
internal static class EndpointFile
{
    private const string LogPrefix = "[PwA.Endpoint]";

    public static string Directory
    {
        get
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(baseDir))
            {
                baseDir = Path.GetTempPath();
            }

            return Path.Combine(baseDir, "PwA", "endpoints");
        }
    }

    public static void Write(int port, string role, DateTime startedAt)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var payload = new JsonObject
            {
                ["service"] = "pwa",
                ["pid"] = Environment.ProcessId,
                ["port"] = port,
                ["role"] = role,
                ["started_at"] = startedAt.ToString("O"),
                ["mod_version"] = ModEntry.Version,
                ["process"] = SafeProcessName(),
            };
            var path = PathFor(Environment.ProcessId);
            File.WriteAllText(path, payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            Log.Info($"{LogPrefix} 已写端点文件 {path}");
        }
        catch (Exception ex)
        {
            Log.Warn($"{LogPrefix} 写端点文件失败: {ex.Message}");
        }
    }

    public static void Delete()
    {
        try
        {
            var path = PathFor(Environment.ProcessId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"{LogPrefix} 删端点文件失败: {ex.Message}");
        }
    }

    private static string PathFor(int pid) => Path.Combine(Directory, $"{pid}.json");

    private static string SafeProcessName()
    {
        try
        {
            return System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        }
        catch
        {
            return "unknown";
        }
    }
}
