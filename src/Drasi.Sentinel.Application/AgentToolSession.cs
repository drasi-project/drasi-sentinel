using System.Text.Json;
using System.Text.Json.Serialization;
using Drasi.Sentinel.Domain;

namespace Drasi.Sentinel.Application;

public sealed record AgentToolArgument(string Name, bool Required);

public sealed record AgentTool
{
    public AgentTool(string name, string description, string? argumentName)
        : this(
            name,
            description,
            argumentName is null ? [] : [new AgentToolArgument(argumentName, true)])
    {
    }

    public AgentTool(string name, string description, IReadOnlyList<AgentToolArgument> arguments)
    {
        Name = name;
        Description = description;
        Arguments = arguments;
    }

    public string Name { get; }
    public string Description { get; }
    public IReadOnlyList<AgentToolArgument> Arguments { get; }
}

public interface IAgentTools
{
    IReadOnlyList<AgentTool> Tools { get; }

    Task<string> InvokeAsync(string name, IReadOnlyDictionary<string, string?>? arguments = null);
}

public sealed class AgentToolSession : IAgentTools
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IConversationSubscriptions _commands;
    private readonly IQueryWorkbench? _workbench;
    private readonly Principal _principal;
    private readonly TaskTurnId _turn;
    private readonly CancellationToken _cancellationToken;

    private AgentToolSession(
        IConversationSubscriptions commands,
        Principal principal,
        TaskTurnId turn,
        CancellationToken cancellationToken,
        IQueryWorkbench? workbench)
    {
        _commands = commands;
        _workbench = workbench;
        _principal = principal;
        _turn = turn;
        _cancellationToken = cancellationToken;
        var tools = new List<AgentTool>
        {
            new(AgentTurn.SubscribeTool, AgentTurn.SubscribeToolDescription, "query"),
            new(AgentTurn.ListTool, AgentTurn.ListToolDescription, (string?)null),
            new(AgentTurn.CurrentResultsTool, AgentTurn.CurrentResultsToolDescription, (string?)null),
            new(AgentTurn.CancelTool, AgentTurn.CancelToolDescription, "id"),
        };
        if (workbench is not null)
            tools.AddRange(workbench.Tools);

        Tools = tools;
    }

    public IReadOnlyList<AgentTool> Tools { get; }

    public static AgentToolSession Create(
        IConversationSubscriptions commands,
        Principal principal,
        TaskTurnId turn,
        CancellationToken cancellationToken,
        IQueryWorkbench? workbench = null) =>
        new(commands, principal, turn, cancellationToken, workbench);

    public Task<string> InvokeAsync(string name, IReadOnlyDictionary<string, string?>? arguments = null)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        return name switch
        {
            AgentTurn.SubscribeTool => Argument(arguments, "query") is { } query
                ? SubscribeAsync(query)
                : Task.FromResult(Rejected("invalid_tool_arguments", "Pass the query id.")),
            AgentTurn.ListTool => InvokeAsync(new ListTaskSubscriptions()),
            AgentTurn.CurrentResultsTool => InvokeAsync(new GetCurrentTaskResults()),
            AgentTurn.CancelTool => Argument(arguments, "id") is { } id
                ? CancelAsync(id)
                : Task.FromResult(Rejected("invalid_tool_arguments", "Pass the subscription id.")),
            _ when _workbench?.Tools.Any(tool => tool.Name == name) == true => WorkbenchAsync(name, arguments),
            _ => Task.FromResult(Rejected("invalid_tool_arguments", "The tool name is not valid.")),
        };
    }

    private async Task<string> SubscribeAsync(string query) =>
        await InvokeAsync(new SubscribeToQuery(
            new QueryId(query),
            new SubscriptionRequestKey($"a2a:{_turn.TaskId}:{_turn.MessageId}:request")));

    private async Task<string> WorkbenchAsync(string name, IReadOnlyDictionary<string, string?>? arguments)
    {
        if (_workbench is null)
            return Rejected("invalid_tool_arguments", "The tool name is not valid.");
        try
        {
            return await _workbench.InvokeAsync(name, arguments, _cancellationToken);
        }
        catch (SentinelException exception) when (IsRecoverable(exception))
        {
            return Rejected(exception.Code, exception.Message);
        }
    }

    private Task<string> CancelAsync(string id) =>
        Guid.TryParse(id, out var parsed)
            ? InvokeAsync(new CancelTaskSubscription(new SubscriptionId(parsed)))
            : Task.FromResult(Rejected("invalid_subscription_id", "The subscription id is not valid."));

    private async Task<string> InvokeAsync(GovernedToolCall call)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return Success(await _commands.InvokeAsync(_principal, _turn, call, _cancellationToken));
        }
        catch (SentinelException exception) when (IsRecoverable(exception))
        {
            return Rejected(exception.Code, exception.Message);
        }
    }

    private static string? Argument(IReadOnlyDictionary<string, string?>? arguments, string name) =>
        arguments is not null && arguments.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static bool IsRecoverable(SentinelException exception) =>
        exception.Code is "subscription_not_found"
            or "task_already_has_subscription"
            or "query_unknown"
            or "query_ambiguous"
            or "reaction_unbound"
            or "reaction_unknown"
            or "watch_ended"
            or "invalid_tool_arguments"
            or "query_rejected"
            or "source_unknown"
            or "query_exists";

    private static string Success(ToolResult result) => JsonSerializer.Serialize(new
    {
        result.Kind,
        Subscription = result.Subscription is null ? null : View(result.Subscription),
        Subscriptions = result.Subscriptions?.Select(View).ToArray(),
        result.Message,
        Data = result.Data is null ? null : new
        {
            result.Data.Type,
            result.Data.TaskId,
            result.Data.SubscriptionId,
            result.Data.QueryId,
            result.Data.Rows,
        },
    }, Json);

    private static object View(ConversationSubscription subscription) => new
    {
        SubscriptionId = subscription.Id.Value,
        QueryId = subscription.Request.QueryId.Value,
        State = subscription.State.ToString(),
    };

    private static string Rejected(string code, string message) => JsonSerializer.Serialize(new
    {
        Kind = AgentTurn.ToolRejectedKind,
        Code = code,
        Message = message,
        Next = AgentTurn.Recovery(code),
    }, Json);
}
