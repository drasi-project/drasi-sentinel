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
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(),
        };
        if (tool.ArgumentName is { } argument)
        {
            schema["properties"]![argument] = new JsonObject { ["type"] = "string" };
            schema["required"] = new JsonArray(argument);
        }

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
        if (_tool.ArgumentName is { } argument)
        {
            arguments.TryGetValue(argument, out var raw);
            values = new Dictionary<string, string?> { [argument] = raw?.ToString() };
        }

        return await _tools.InvokeAsync(_tool.Name, values);
    }
}
