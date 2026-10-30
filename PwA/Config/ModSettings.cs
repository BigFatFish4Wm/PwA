using MegaCrit.Sts2.Core.Logging;

namespace PwA.Config;

/// <summary>运行期配置。全部来自环境变量，方便两个窗口各带一套启动参数。</summary>
internal sealed class ModSettings
{
    private const string LogPrefix = "[PwA.Config]";

    /// <summary>显式端口。不设时按角色取 8080 / 8081 / 8082。</summary>
    public int? Port { get; init; }

    /// <summary>端口被占用时是否顺延到附近端口。</summary>
    public bool AllowFallback { get; init; } = true;

    /// <summary>写端点文件，供 World 发现本窗口。</summary>
    public bool PublishEndpoint { get; init; } = true;

    /// <summary>0 = 关闭服务（Mod 仍加载，只是不开端口）。</summary>
    public bool Enabled { get; init; } = true;

    public static ModSettings Load()
    {
        var enabled = ReadBool("PWA_ENABLED", true);
        var port = ReadInt("PWA_PORT");
        var fallback = ReadBool("PWA_PORT_FALLBACK", true);
        var publish = ReadBool("PWA_PUBLISH_ENDPOINT", true);
        var settings = new ModSettings
        {
            Port = port,
            AllowFallback = fallback,
            PublishEndpoint = publish,
            Enabled = enabled,
        };
        Log.Info($"{LogPrefix} enabled={settings.Enabled} port={settings.Port?.ToString() ?? "(auto)"} fallback={settings.AllowFallback}");
        return settings;
    }

    private static bool ReadBool(string name, bool fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        raw = raw.Trim();
        return raw is "1" or "true" or "TRUE" or "True" or "yes" or "on";
    }

    private static int? ReadInt(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw) || !int.TryParse(raw.Trim(), out var value))
        {
            return null;
        }

        return value is > 0 and <= 65535 ? value : null;
    }
}
