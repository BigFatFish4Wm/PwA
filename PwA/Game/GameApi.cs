using System.Text.Json;
using System.Text.Json.Nodes;
using MegaCrit.Sts2.Core.Logging;
using PwA.Server;

namespace PwA.Game;

/// <summary>对外（REST / MCP 共用）的门面：健康检查、读状态、执行动作、批量计划。</summary>
internal static class GameApi
{
    public static JsonObject Health(BridgeServer? server)
    {
        // 这个接口每秒被 World 轮询，绝不能在 HTTP 线程上碰 Godot 节点；只读脉冲缓存。
        return new JsonObject
        {
            ["service"] = "pwa",
            ["status"] = "ready",
            ["mod_version"] = ModEntry.Version,
            ["game_version"] = ModEntry.GameVersion,
            ["pid"] = Environment.ProcessId,
            ["role"] = PulseCache.Role,
            ["port"] = server?.Port ?? 0,
            ["started_at"] = server?.StartedAt.ToString("O"),
            ["process_started_at"] = InstanceRole.ProcessStartedAt.ToString("O"),
            ["screen"] = PulseCache.Screen,
            ["in_combat"] = PulseCache.InCombat,
            ["in_run"] = PulseCache.InRun,
            ["my_turn"] = PulseCache.MyTurn,
            ["can_act"] = PulseCache.CanAct,
            ["floor"] = PulseCache.Floor,
            ["events_seq"] = EventRing.LastSeq,
            ["cached_ms"] = PulseCache.UpdatedAtTicks == 0
                ? -1
                : (long)(DateTime.UtcNow - new DateTime(PulseCache.UpdatedAtTicks, DateTimeKind.Utc)).TotalMilliseconds,
            ["endpoints"] = new JsonArray("/mcp", "/state", "/actions", "/act", "/plan", "/events"),
        };
    }

    public static async Task<JsonObject> GetStateAsync()
    {
        try
        {
            // 先等局面稳定再交出去：刚出完牌/刚抽完牌时读到的是演出中途的半截状态。
            return await GameThread.InvokeAsync(StateSettler.WaitAsync);
        }
        catch (Exception ex)
        {
            Log.Error($"[PwA] 读状态失败: {ex}");
            throw ApiException.Unavailable("state_unavailable", $"读状态失败: {ex.Message}");
        }
    }

    public static async Task<JsonObject> GetActionsAsync()
    {
        try
        {
            return await GameThread.InvokeAsync(() =>
            {
                var state = StateBuilder.Build();
                return new JsonObject
                {
                    ["screen"] = state["screen"]?.DeepClone(),
                    ["turn"] = state["turn"]?.DeepClone(),
                    ["actions"] = state["actions"]?.DeepClone(),
                    ["readiness"] = StateBuilder.Readiness(),
                };
            });
        }
        catch (Exception ex)
        {
            throw ApiException.Unavailable("state_unavailable", $"读动作失败: {ex.Message}");
        }
    }

    public static async Task<JsonObject> ActAsync(JsonNode body)
    {
        var (action, args) = Split(body);
        if (string.IsNullOrWhiteSpace(action))
        {
            throw ApiException.BadRequest("invalid_request", "缺少 action 字段。");
        }

        var server = BridgeServer.Instance;
        if (server == null)
        {
            throw ApiException.Unavailable("not_ready", "服务还没起来。");
        }

        return await server.WithActionGateAsync(async () =>
        {
            try
            {
                // 执行、等稳定、回执都在游戏线程上完成：回执要读界面节点，不能在 HTTP 线程碰。
                var response = await GameThread.InvokeAsync(async () =>
                {
                    var result = await ActionService.ExecuteAsync(action, args);
                    var settled = await StateSettler.WaitAsync();
                    return BuildActResponse(action, result, settled);
                });
                EventRing.Record("action", new JsonObject
                {
                    ["action"] = action,
                    ["status"] = response["status"]?.GetValue<string>(),
                });
                return response;
            }
            catch (ApiException api)
            {
                EventRing.Record("action_failed", new JsonObject
                {
                    ["action"] = action,
                    ["code"] = api.Code,
                });
                throw;
            }
        });
    }

    public static async Task<JsonObject> PlanAsync(JsonNode body)
    {
        var steps = body is JsonObject obj && obj["steps"] is JsonArray array ? array : null;
        if (steps == null || steps.Count == 0)
        {
            throw ApiException.BadRequest("invalid_request", "plan 需要非空 steps 数组。");
        }

        if (steps.Count > 16)
        {
            throw ApiException.BadRequest("too_many_steps", "一次最多 16 步。");
        }

        var results = new JsonArray();
        var index = 0;
        foreach (var step in steps)
        {
            index++;
            var (action, args) = Split(step);
            if (string.IsNullOrWhiteSpace(action))
            {
                results.Add(new JsonObject { ["i"] = index, ["ok"] = false, ["error"] = "缺少 action" });
                break;
            }

            try
            {
                var server = BridgeServer.Instance ?? throw ApiException.Unavailable("not_ready", "服务还没起来。");
                var response = await server.WithActionGateAsync(async () =>
                {
                    var result = await GameThread.InvokeAsync(async () => await ActionService.ExecuteAsync(action, args));
                    return new JsonObject
                    {
                        ["i"] = index,
                        ["action"] = action,
                        ["ok"] = true,
                        ["status"] = result.Status,
                        ["extra"] = result.Extra?.DeepClone(),
                    };
                });
                results.Add(response);
            }
            catch (ApiException api)
            {
                results.Add(new JsonObject
                {
                    ["i"] = index,
                    ["action"] = action,
                    ["ok"] = false,
                    ["error"] = new JsonObject
                    {
                        ["code"] = api.Code,
                        ["message"] = api.Message,
                        ["details"] = api.Details == null ? null : JsonSerializer.SerializeToNode(api.Details),
                        ["retryable"] = api.Retryable,
                    },
                });
                return new JsonObject
                {
                    ["ok"] = false,
                    ["steps"] = results,
                    ["failed_at"] = index,
                    ["screen"] = await GameThread.InvokeAsync(GameNodes.ResolveScreen),
                    ["actions"] = (await GetActionsAsync())["actions"]?.DeepClone(),
                };
            }
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["steps"] = results,
            ["screen"] = await GameThread.InvokeAsync(GameNodes.ResolveScreen),
            ["actions"] = (await GetActionsAsync())["actions"]?.DeepClone(),
        };
    }

    private static JsonObject BuildActResponse(string action, ActionResult result, JsonObject settled)
    {
        var state = new JsonObject
        {
            ["screen"] = settled["screen"]?.DeepClone(),
            ["turn"] = settled["turn"]?.DeepClone(),
        };

        try
        {
            foreach (var key in new[] { "combat", "selection", "event", "map", "rest", "reward", "shop", "chest", "modal", "singleplayer", "character_select", "game_over" })
            {
                state[key] = SafeView(settled, key, brief: key == "combat");
            }

            state["actions"] = settled["actions"]?.DeepClone();
            state["run"] = SafeView(settled, "run");
        }
        catch
        {
        }

        var response = new JsonObject
        {
            ["ok"] = true,
            ["action"] = action,
            ["status"] = result.Status,
            ["extra"] = result.Extra?.DeepClone(),
            ["state"] = state,
        };

        if (result.Status == "pending")
        {
            // pending 表示「游戏已经收下这个动作，还在过场」：绝不能当失败重发，否则会重复出牌。
            response["note"] = "动作已被游戏接受，还在过场；不要重复提交同一动作，先 sts2_view 看结果";
            response["do_not_repeat"] = true;
        }

        return response;
    }

    private static JsonNode? SafeView(JsonObject full, string key, bool brief = false)
    {
        if (full[key] is not JsonObject section)
        {
            return null;
        }

        var clone = section.DeepClone() as JsonObject;
        if (brief && clone?["me"] is JsonObject me)
        {
            me.Remove("potions");
        }

        return clone;
    }

    private static (string? Action, JsonElement Args) Split(JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            return (null, default);
        }

        var action = obj["action"]?.GetValue<string>();
        JsonNode argNode = obj["arguments"] as JsonObject ?? obj;
        var element = JsonSerializer.SerializeToElement(argNode);
        return (action, element);
    }

    private static MegaCrit.Sts2.Core.Runs.RunState? TryRun()
    {
        try
        {
            return MegaCrit.Sts2.Core.Runs.RunManager.Instance?.DebugOnlyGetState();
        }
        catch
        {
            return null;
        }
    }

    private static MegaCrit.Sts2.Core.Combat.CombatState? TryCombat()
    {
        try
        {
            return MegaCrit.Sts2.Core.Combat.CombatManager.Instance.DebugOnlyGetState();
        }
        catch
        {
            return null;
        }
    }

    private static JsonObject SafeReadiness()
    {
        try
        {
            return StateBuilder.Readiness();
        }
        catch
        {
            return new JsonObject { ["can_act"] = false, ["reason"] = "unavailable", ["my_turn"] = false };
        }
    }
}
