using System.Text.Json;
using Drasi.Sentinel.Application;
using Drasi.Sentinel.Domain;

namespace Drasi.Sentinel.Host;

public static class A2aTaskAdmin
{
    public static bool IsMethod(string? method) => TryMethod(method, out _);

    public static async Task<IResult> HandleAsync(
        JsonElement request,
        JsonElement? requestId,
        Principal principal,
        IConversationRepository repository,
        IConversationSubscriptions subscriptions,
        CancellationToken cancellationToken)
    {
        if (!request.TryGetProperty("method", out var method) || !TryMethod(method.GetString(), out var parsed))
            throw new A2aProtocolException("method_not_found", "The A2A method is not supported.");
        var parameters = request.TryGetProperty("params", out var paramsElement) && paramsElement.ValueKind == JsonValueKind.Object
            ? paramsElement
            : default;
        return parsed switch
        {
            TaskAdminMethod.List => Result(requestId, new { tasks = await ViewsAsync(repository, principal.Owner, cancellationToken), nextPageToken = "", pageSize = 100, totalSize = (await repository.ListOwnerTasksAsync(principal.Owner, cancellationToken)).Count }),
            TaskAdminMethod.Get => Result(requestId, await OneAsync(repository, principal.Owner, RequireId(parameters), cancellationToken)),
            TaskAdminMethod.Cancel => Result(requestId, await CancelAsync(repository, subscriptions, principal, RequireId(parameters), cancellationToken)),
            _ => throw new A2aProtocolException("method_not_found", "The A2A method is not supported."),
        };
    }

    private static async Task<object> CancelAsync(
        IConversationRepository repository,
        IConversationSubscriptions subscriptions,
        Principal principal,
        string taskId,
        CancellationToken cancellationToken)
    {
        var id = new ConversationTaskId(taskId);
        if (!await repository.TaskExistsAsync(principal.Owner, id, cancellationToken))
            throw new A2aProtocolException("TaskNotFoundError", "The task does not exist.");
        var subscription = (await repository.ListSubscriptionsAsync(principal.Owner, id, cancellationToken))
            .FirstOrDefault(item => item.State is SubscriptionState.Arming or SubscriptionState.Active or SubscriptionState.Paused or SubscriptionState.Degraded);
        if (subscription is not null)
        {
            await subscriptions.InvokeAsync(
                principal,
                new TaskTurnId(taskId, $"cancel:{taskId}"),
                new CancelTaskSubscription(subscription.Id),
                cancellationToken);
        }
        return await OneAsync(repository, principal.Owner, taskId, cancellationToken);
    }

    private static async Task<object> OneAsync(IConversationRepository repository, OwnerId owner, string taskId, CancellationToken cancellationToken)
    {
        var tasks = await repository.ListOwnerTasksAsync(owner, cancellationToken);
        var task = tasks.FirstOrDefault(item => item.TaskId.Value == taskId)
            ?? throw new A2aProtocolException("TaskNotFoundError", "The task does not exist.");
        return View(task);
    }

    private static async Task<object[]> ViewsAsync(IConversationRepository repository, OwnerId owner, CancellationToken cancellationToken)
    {
        var tasks = await repository.ListOwnerTasksAsync(owner, cancellationToken);
        return tasks.Select(View).ToArray();
    }

    private static object View(OwnerTaskSummary task) => new
    {
        id = task.TaskId.Value,
        contextId = task.TaskId.Value,
        status = new { state = Status(task) },
        metadata = new
        {
            queryId = task.QueryId?.Value,
            subscriptionId = task.SubscriptionId?.Value.ToString("D"),
            subscriptionState = task.SubscriptionState?.ToString(),
        },
    };

    private static string Status(OwnerTaskSummary task) => task.SubscriptionState switch
    {
        SubscriptionState.Cancelled or SubscriptionState.Revoked or SubscriptionState.Expired => "TASK_STATE_CANCELED",
        SubscriptionState.Arming or SubscriptionState.Active or SubscriptionState.Paused or SubscriptionState.Degraded => "TASK_STATE_WORKING",
        _ => task.State == ConversationTaskState.Failed ? "TASK_STATE_FAILED" : "TASK_STATE_WORKING",
    };

    private static string RequireId(JsonElement parameters)
    {
        var id = Optional(parameters, "id") ?? Optional(parameters, "taskId");
        if (string.IsNullOrWhiteSpace(id))
            throw new A2aProtocolException("invalid_params", "The task request requires id.");
        return id;
    }

    private static string? Optional(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryMethod(string? method, out TaskAdminMethod parsed)
    {
        if (string.Equals(method, "ListTasks", StringComparison.Ordinal) || string.Equals(method, "tasks/list", StringComparison.Ordinal))
        {
            parsed = TaskAdminMethod.List;
            return true;
        }
        if (string.Equals(method, "GetTask", StringComparison.Ordinal) || string.Equals(method, "tasks/get", StringComparison.Ordinal))
        {
            parsed = TaskAdminMethod.Get;
            return true;
        }
        if (string.Equals(method, "CancelTask", StringComparison.Ordinal) || string.Equals(method, "tasks/cancel", StringComparison.Ordinal))
        {
            parsed = TaskAdminMethod.Cancel;
            return true;
        }
        parsed = default;
        return false;
    }

    private static IResult Result(JsonElement? requestId, object result) =>
        Results.Json(new { jsonrpc = "2.0", id = requestId, result });

    private enum TaskAdminMethod { List, Get, Cancel }
}
