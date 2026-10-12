using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Models.Mcp;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Mcp.Tools;

/// <summary>
/// Write tool: fills every unpinned device with one exact flat color by setting the global Static
/// look to that color (the lighting page shows it as the picked color). Pinned devices keep their pin.
/// </summary>
public sealed class SetStaticColorTool : IMcpTool
{
    private readonly ILightingProvider _lighting;
    private readonly MultiplexHub _hub;
    private readonly FeatureGates _gates;

    public SetStaticColorTool(ILightingProvider lighting, MultiplexHub hub, FeatureGates? gates = null)
    {
        _lighting = lighting;
        _hub = hub;
        _gates = gates ?? FeatureGates.AllEnabled;
    }

    public string Name => "set_static_color";
    public string Title => "Set Static Color";

    public string Description =>
        "Fills every RGB device that follows the global mode with one exact flat color. " +
        "Devices pinned to their own look keep it. Use when the user asks for a specific solid color " +
        "(e.g. 'make it red', '#ff8800'). For a " +
        "named animated look use apply_lighting_scenario instead.";

    public McpCapability Capability => McpCapability.Lighting;
    public bool ReadOnly => false;

    public string InputSchemaJson =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"color\":{\"type\":\"string\",\"pattern\":\"^#[0-9a-fA-F]{6}$\",\"description\":\"Hex RGB color, e.g. #ff8800.\"}" +
        "},\"required\":[\"color\"],\"additionalProperties\":false}";

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        if (!_gates.Lighting)
        {
            return Task.FromResult(McpToolExecutionResult.Error("Lighting is disabled in Settings."));
        }
        var color = McpArgs.StringArg(args, "color");
        if (!TryParseHexColor(color, out var r, out var g, out var b))
        {
            return Task.FromResult(McpToolExecutionResult.Error(
                "'color' must be a hex RGB string like #ff8800."));
        }

        _lighting.StartStatic(new StaticHeadlessStart { Color = $"#{r:x2}{g:x2}{b:x2}", Persist = true });
        PanelTopics.BroadcastLighting(_hub);

        var result = new McpSetStaticColorResult { Color = $"#{r:x2}{g:x2}{b:x2}" };
        var json = JsonSerializer.Serialize(result, AppJsonContext.Default.McpSetStaticColorResult);
        return Task.FromResult(McpToolExecutionResult.Ok(json));
    }

    private static bool TryParseHexColor(string? input, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (input is null || input.Length != 7 || input[0] != '#')
        {
            return false;
        }
        for (var i = 1; i < 7; i++)
        {
            if (!Uri.IsHexDigit(input[i]))
            {
                return false;
            }
        }
        r = Convert.ToByte(input.Substring(1, 2), 16);
        g = Convert.ToByte(input.Substring(3, 2), 16);
        b = Convert.ToByte(input.Substring(5, 2), 16);
        return true;
    }
}
