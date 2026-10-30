using System.Text.Json;
using System.Text.Json.Nodes;
using PwA.Game;

namespace PwA.Server;

/// <summary>MCP Streamable HTTP 的 JSON-RPC 部分：initialize / tools.list / tools.call。</summary>
internal static class McpEndpoint
{
    private const string DefaultProtocol = "2025-06-18";
    private static readonly string[] Protocols = { "2025-06-18", "2025-03-26", "2024-11-05" };

    private sealed record ToolSpec(string Name, string Description, JsonObject Schema, string? DirectAction)
    {
        public bool IsPassThrough => DirectAction == null;
    }

    private static readonly ToolSpec[] Tools = BuildTools();

    public static async Task<JsonNode> HandleAsync(JsonNode? request)
    {
        if (request is not JsonObject message)
        {
            return Error(null, -32600, "请求体必须是 JSON-RPC 对象。");
        }

        var id = message["id"]?.DeepClone();
        var method = message["method"]?.GetValue<string>();
        var parameters = message["params"] as JsonObject;

        switch (method)
        {
            case null:
                return Error(id, -32600, "缺少 method。");
            case "initialize":
                return Result(id, new JsonObject
                {
                    ["protocolVersion"] = PickProtocol(parameters?["protocolVersion"]?.GetValue<string>()),
                    ["capabilities"] = new JsonObject
                    {
                        ["tools"] = new JsonObject { ["listChanged"] = false },
                    },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = "pwa",
                        ["version"] = ModEntry.Version,
                    },
                    ["instructions"] = "Slay the Spire 2 的一个游戏窗口。先 get_state 看局面，再用具体动作工具；动作不做决策。",
                });
            case "notifications/initialized":
            case "notifications/cancelled":
                return new JsonObject();
            case "ping":
                return Result(id, new JsonObject());
            case "tools/list":
                return Result(id, new JsonObject { ["tools"] = BuildToolList() });
            case "tools/call":
                return await CallToolAsync(id, parameters);
            default:
                return Error(id, -32601, $"不支持的方法 {method}。");
        }
    }

    private static string PickProtocol(string? requested) =>
        !string.IsNullOrWhiteSpace(requested) && Protocols.Contains(requested) ? requested : DefaultProtocol;

    private static async Task<JsonNode> CallToolAsync(JsonNode? id, JsonObject? parameters)
    {
        var name = parameters?["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name))
        {
            return Error(id, -32602, "tools/call 缺少 name。");
        }

        var spec = Tools.FirstOrDefault(tool => tool.Name == name);
        if (spec == null)
        {
            return Error(id, -32602, $"没有这个工具：{name}。");
        }

        var arguments = parameters?["arguments"] as JsonObject ?? new JsonObject();
        try
        {
            JsonObject payload = spec.Name switch
            {
                "get_state" => await GameApi.GetStateAsync(),
                "get_actions" => await GameApi.GetActionsAsync(),
                "get_events" => EventRing.Since(ReadInt(arguments, "since") ?? 0),
                "health" => GameApi.Health(BridgeServer.Instance),
                "plan" => await GameApi.PlanAsync(new JsonObject { ["steps"] = arguments["steps"]?.DeepClone() }),
                _ => await GameApi.ActAsync(new JsonObject
                {
                    ["action"] = spec.DirectAction,
                    ["arguments"] = arguments.DeepClone(),
                }),
            };

            return new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = payload.ToJsonString(),
                    }),
                    ["isError"] = false,
                },
            };
        }
        catch (ApiException api)
        {
            return ToolFailure(id, api.Code, api.Message, api.ToJson());
        }
        catch (Exception ex)
        {
            return ToolFailure(id, "internal_error", ex.Message, null);
        }
    }

    private static JsonNode ToolFailure(JsonNode? id, string code, string message, JsonObject? detail)
    {
        var body = new JsonObject
        {
            ["ok"] = false,
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
                ["details"] = detail?["error"]?["details"]?.DeepClone(),
                ["retryable"] = detail?["error"]?["retryable"]?.GetValue<bool>() ?? false,
            },
        };

        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = body.ToJsonString(),
                }),
                ["isError"] = true,
            },
        };
    }

    private static JsonArray BuildToolList()
    {
        var list = new JsonArray();
        foreach (var tool in Tools)
        {
            list.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.Schema.DeepClone(),
            });
        }

        return list;
    }

    private static JsonObject Result(JsonNode? id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = result,
    };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        },
    };

    private static int? ReadInt(JsonObject obj, string name)
    {
        var node = obj[name];
        if (node == null)
        {
            return null;
        }

        try
        {
            return node.GetValue<int>();
        }
        catch
        {
            return int.TryParse(node.ToString(), out var parsed) ? parsed : null;
        }
    }

    // ------------------------------------------------------------- 工具表

    private static ToolSpec[] BuildTools() => new[]
    {
        Spec("get_state", "读当前局面：画面、战斗、手牌（带 ref）、敌人、地图/事件/奖励等服务端结构。", NoArgs()),
        Spec("get_actions", "只读当前可用动作列表和就绪原因。", NoArgs()),
        Spec("health", "服务是否就绪、本窗口角色（host/client/singleplayer）、端口。", NoArgs()),
        Spec("get_events", "读事件环（since 之后的新事件）。", Props(("since", "integer", "从哪个 seq 之后开始，缺省 0"))),

        Spec("play_card", "打出一张手牌。优先用 get_state 里的 card_ref；指向性牌要 target_index。",
            Props(("card_ref", "string", "手牌 ref，如 c3"), ("card_index", "integer", "手牌下标（会随出牌重排）"), ("target_index", "integer", "目标下标；敌人是 enemies[i]，队友是 players[i]"), ("card_id", "string", "卡牌 id（不推荐）"))),

        Spec("end_turn", "结束本回合。", NoArgs()),

        Spec("use_potion", "使用药水槽里的药水。", Props(("slot", "integer", "药水槽下标"), ("target_index", "integer", "需要目标时：敌人或队友下标"))),
        Spec("discard_potion", "丢掉一瓶药水。", Props(("slot", "integer", "药水槽下标"))),

        Spec("choose_map_node", "在地图上选择下一个节点。", Props(("index", "integer", "state.map.options[i].i"))),
        Spec("choose_event_option", "选择事件选项；事件结束后会走 proceed。", Props(("index", "integer", "state.event.options[i].i"))),

        Spec("open_chest", "打开宝箱。", NoArgs()),
        Spec("choose_treasure_relic", "从宝箱里拿一件遗物。", Props(("index", "integer", "state.chest.relics[i].i"))),

        Spec("choose_rest_option", "营火选一个选项。", Props(("index", "integer", "state.rest.options[i].i"), ("target_index", "integer", "MEND 需要的队友下标"))),

        Spec("claim_reward", "领取一个奖励（金币/药水/遗物/卡牌奖励入口）。", Props(("index", "integer", "state.reward.rewards[i].i"))),
        Spec("choose_reward_card", "在卡牌奖励里选一张。", Props(("index", "integer", "state.reward.cards[i].i"))),
        Spec("skip_reward_cards", "跳过卡牌奖励。", NoArgs()),

        Spec("buy_card", "商店买卡。", Props(("index", "integer", "state.shop.cards[i].i"))),
        Spec("buy_relic", "商店买遗物。", Props(("index", "integer", "state.shop.relics[i].i"))),
        Spec("buy_potion", "商店买药水。", Props(("index", "integer", "state.shop.potions[i].i"))),
        Spec("remove_card_at_shop", "商店删牌（之后会进选牌界面）。", NoArgs()),
        Spec("open_shop_inventory", "打开商店货架。", NoArgs()),
        Spec("close_shop_inventory", "关闭商店货架。", NoArgs()),

        Spec("proceed", "点继续/离开当前房间。", NoArgs()),

        Spec("select_card", "在选牌界面里选一张（战斗内选牌或牌库选牌）。", Props(("card_ref", "string", "候选里的 ref"), ("index", "integer", "候选下标"))),
        Spec("confirm_selection", "确认选牌。", NoArgs()),
        Spec("choose_bundle", "在「从若干卡牌包里选一包」的界面里选第 index 包。", Props(("index", "integer", "state.selection.bundles[i].i"))),
        Spec("choose_relic", "在选遗物界面里选第 index 件。", Props(("index", "integer", "state.selection.relics[i].i"))),
        Spec("skip_selection", "跳过当前选择（界面有跳过按钮时）。", NoArgs()),

        Spec("choose_character", "选角色。", Props(("index", "integer", "state.character_select.characters[i].i"))),
        Spec("embark", "准备/开始。", NoArgs()),
        Spec("continue_run", "主菜单继续存档。", NoArgs()),
        Spec("open_singleplayer", "主菜单进入单人模式（进入选角）。", NoArgs()),
        Spec("start_standard_run", "单人子菜单里选「标准模式」，进入选角色。", NoArgs()),
        Spec("back", "返回上一层菜单。", NoArgs()),

        Spec("confirm_modal", "确认弹窗。", NoArgs()),
        Spec("dismiss_modal", "取消/关闭弹窗。", NoArgs()),
        Spec("continue_game_over", "结算界面继续。", NoArgs()),

        Spec("plan", "一次提交多步动作，按顺序执行，遇到失败停止。",
            Props(("steps", "array", "数组，每项 {action, arguments}"))),

        Spec("inspect_tree", "只读：打印界面节点树，排查按钮路径用。",
            Props(("path", "string", "相对 NGame 的节点路径，缺省根节点"), ("depth", "integer", "递归深度 1-8，缺省 3"))),
        Spec("click_node", "按节点路径点一下（兜底手段）。",
            Props(("path", "string", "相对 NGame 的节点路径"), ("mode", "string", "force / debug / released / pressed，缺省 force"))),
    };

    private static ToolSpec Spec(string name, string description, JsonObject schema) => new(name, description, schema, name);

    private static JsonObject NoArgs() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(),
        ["additionalProperties"] = false,
    };

    private static JsonObject Props(params (string Name, string Type, string Description)[] properties)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, type, description) in properties)
        {
            var schema = new JsonObject
            {
                ["type"] = type,
                ["description"] = description,
            };
            if (type == "array")
            {
                schema["items"] = new JsonObject { ["type"] = "object" };
            }

            props[name] = schema;
            if (type != "array")
            {
                required.Add(name);
            }
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = required,
            ["additionalProperties"] = false,
        };
    }
}
