using System.Text.Json;
using System.Text.Json.Nodes;
using Drasi.Sentinel.Application;
using Microsoft.Extensions.AI;

namespace Drasi.Sentinel.Agent.Maf;

internal sealed class SessionFunction : AIFunction
{
    private readonly IAgentTools _tools;
    private readonly AgentTool _tool;
    private readonly JsonElement _schema;

    public SessionFunction(IAgentTools tools, AgentTool tool)
    {
        _tools = tools;
        _tool = tool;
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var argument in tool.Arguments)
        {
            properties[argument.Name] = new JsonObject { ["type"] = "string" };
            if (argument.Required)
                required.Add(argument.Name);
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
        };
        if (required.Count > 0)
            schema["required"] = required;
        _schema = JsonSerializer.SerializeToElement(schema);
    }

    public override string Name => _tool.Name;

    public override string Description => _tool.Description;

    public override JsonElement JsonSchema => _schema;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string?>? values = null;
        if (_tool.Arguments.Count > 0)
        {
            var provided = new Dictionary<string, string?>();
            foreach (var argument in _tool.Arguments)
            {
                if (!arguments.TryGetValue(argument.Name, out var raw) || raw is null)
                    continue;
                provided[argument.Name] = Text(raw);
            }

            values = provided;
        }

        return await _tools.InvokeAsync(_tool.Name, values);
    }

    private static string Text(object raw) => raw switch
    {
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? "",
        JsonElement element => element.GetRawText(),
        _ => raw.ToString() ?? "",
    };
}
