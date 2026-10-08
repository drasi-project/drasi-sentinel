using System.Text.Json;
using System.Text.Json.Serialization;
using Drasi.Sentinel.Domain;

namespace Drasi.Sentinel.Application;

public sealed record AgentTool(string Name, string Description, string? ArgumentName);

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
    private readonly Principal _principal;
    private readonly TaskTurnId _turn;
    private readonly CancellationToken _cancellationToken;

    private AgentToolSession(
        IConversationSubscriptions commands,
        Principal principal,
        TaskTurnId turn,
        CancellationToken cancellationToken)
    {
        _commands = commands;
        _principal = principal;
        _turn = turn;
        _cancellationToken = cancellationToken;
        Tools =
        [
            new(AgentTurn.SubscribeTool, AgentTurn.SubscribeToolDescription, "query"),
            new(AgentTurn.ListTool, AgentTurn.ListToolDescription, null),
            new(AgentTurn.CurrentResultsTool, AgentTurn.CurrentResultsToolDescription, null),
            new(AgentTurn.CancelTool, AgentTurn.CancelToolDescription, "id"),
        ];
    }

    public IReadOnlyList<AgentTool> Tools { get; }

    public static AgentToolSession Create(
        IConversationSubscriptions commands,
        Principal principal,
        TaskTurnId turn,
        CancellationToken cancellationToken) =>
        new(commands, principal, turn, cancellationToken);

    public Task<string> InvokeAsync(string name, IReadOnlyDictionary<string, string?>? arguments = null)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        return name switch
        {
            AgentTurn.SubscribeTool => SubscribeAsync(Required(arguments, "query")),
            AgentTurn.ListTool => InvokeAsync(new ListTaskSubscriptions()),
            AgentTurn.CurrentResultsTool => InvokeAsync(new GetCurrentTaskResults()),
            AgentTurn.CancelTool => CancelAsync(Required(arguments, "id")),
            _ => throw new InvalidOperationException($"Unknown agent tool '{name}'."),
        };
    }

    private async Task<string> SubscribeAsync(string query) =>
        await InvokeAsync(new SubscribeToQuery(
            new QueryId(query),
            new SubscriptionRequestKey($"a2a:{_turn.TaskId}:{_turn.MessageId}:request")));

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

    private static string Required(IReadOnlyDictionary<string, string?>? arguments, string name) =>
        arguments is not null && arguments.TryGetValue(name, out var value) && value is not null
            ? value
            : throw new InvalidOperationException($"Agent tool argument '{name}' is required.");

    private static bool IsRecoverable(SentinelException exception) =>
        exception.Code is "subscription_not_found"
            or "task_already_has_subscription"
            or "query_unknown"
            or "query_ambiguous"
            or "reaction_unbound";

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
