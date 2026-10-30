using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PwA.Server;

/// <summary>动作失败的统一形状。HTTP 状态码与 MCP 回执都用它。</summary>
internal sealed class ApiException : Exception
{
    public ApiException(int status, string code, string message, object? details = null, bool retryable = false)
        : base(message)
    {
        Status = status;
        Code = code;
        Details = details;
        Retryable = retryable;
    }

    public int Status { get; }

    public string Code { get; }

    public object? Details { get; }

    public bool Retryable { get; }

    public JsonObject ToJson() => new()
    {
        ["error"] = new JsonObject
        {
            ["code"] = Code,
            ["message"] = Message,
            ["details"] = Details == null ? null : JsonSerializer.SerializeToNode(Details),
            ["retryable"] = Retryable,
            ["status_code"] = Status,
        },
    };

    public static ApiException BadRequest(string code, string message, object? details = null) =>
        new(400, code, message, details);

    public static ApiException Conflict(string code, string message, object? details = null, bool retryable = false) =>
        new(409, code, message, details, retryable);

    public static ApiException Unavailable(string code, string message, object? details = null) =>
        new(503, code, message, details, retryable: true);
}

/// <summary>JSON 读取小工具，所有参数都从 <see cref="JsonElement"/> 里取。</summary>
internal static class Args
{
    public static string? Str(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    public static int? Int(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    public static bool? Bool(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    public static JsonElement? Object(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Object ? value : null;
    }

    public static JsonArray? Array(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Array ? JsonNode.Parse(value.GetRawText()) as JsonArray : null;
    }
}
