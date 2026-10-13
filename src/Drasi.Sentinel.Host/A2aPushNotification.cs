using System.Text.Json;
using Drasi.Sentinel.Application;
using Drasi.Sentinel.Domain;
using Microsoft.AspNetCore.Http;

namespace Drasi.Sentinel.Host;

public static class A2aPushNotification
{
    public static bool IsMethod(string? method) => TryMethod(method, out _);

    public static PushNotificationConfig? ReadMessageConfig(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("configuration", out var configuration) ||
            configuration.ValueKind != JsonValueKind.Object ||
            Nested(configuration) is not { } config)
        {
            return null;
        }

        return ReadConfig(config);
    }

    public static async Task<IResult> HandleAsync(
        JsonElement request,
        JsonElement? requestId,
        Principal principal,
        IConversationRepository repository,
        CancellationToken cancellationToken)
    {
        if (!request.TryGetProperty("jsonrpc", out var version) ||
            version.ValueKind != JsonValueKind.String ||
            !StringComparer.Ordinal.Equals(version.GetString(), "2.0"))
        {
            throw new A2aProtocolException("invalid_request", "The request must be JSON-RPC 2.0.");
        }

        if (!request.TryGetProperty("method", out var method) || !TryMethod(method.GetString(), out var parsed))
            throw new A2aProtocolException("method_not_found", "The A2A method is not supported.");
        if (!request.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object)
            throw new A2aProtocolException("invalid_params", "The A2A params must be an object.");

        var configElement = Nested(parameters) ?? parameters;
        var taskId = new ConversationTaskId(RequireTaskId(parameters, configElement));
        if (!await repository.TaskExistsAsync(principal.Owner, taskId, cancellationToken))
            throw new A2aProtocolException("TaskNotFoundError", "The task does not exist.");

        return parsed switch
        {
            PushMethod.Set => await SetAsync(requestId, repository, principal, taskId, configElement, cancellationToken),
            PushMethod.Get => await GetAsync(requestId, repository, principal, taskId, OptionalString(parameters, "id") ?? OptionalString(configElement, "id"), cancellationToken),
            PushMethod.List => await ListAsync(requestId, repository, principal, taskId, cancellationToken),
            PushMethod.Delete => await DeleteAsync(requestId, repository, principal, taskId, OptionalString(parameters, "id") ?? OptionalString(configElement, "id"), cancellationToken),
            _ => throw new A2aProtocolException("method_not_found", "The A2A method is not supported."),
        };
    }

    private static async Task<IResult> SetAsync(
        JsonElement? requestId,
        IConversationRepository repository,
        Principal principal,
        ConversationTaskId taskId,
        JsonElement configElement,
        CancellationToken cancellationToken)
    {
        var config = ReadConfig(configElement);
        await repository.SetPushConfigAsync(principal.Owner, taskId, config, cancellationToken);
        return Result(requestId, new { id = config.Id, taskId = taskId.Value, url = config.Url.ToString() });
    }

    private static async Task<IResult> GetAsync(
        JsonElement? requestId,
        IConversationRepository repository,
        Principal principal,
        ConversationTaskId taskId,
        string? id,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new A2aProtocolException("invalid_params", "The push notification request requires id.");
        var config = await repository.GetPushConfigAsync(principal.Owner, taskId, cancellationToken);
        if (config is null || !StringComparer.Ordinal.Equals(config.Id, id))
            throw new A2aProtocolException("TaskNotFoundError", "The push notification configuration does not exist.");
        return Result(requestId, new { id = config.Id, taskId = taskId.Value, url = config.Url.ToString() });
    }

    private static async Task<IResult> ListAsync(
        JsonElement? requestId,
        IConversationRepository repository,
        Principal principal,
        ConversationTaskId taskId,
        CancellationToken cancellationToken)
    {
        var config = await repository.GetPushConfigAsync(principal.Owner, taskId, cancellationToken);
        var configs = config is null
            ? Array.Empty<object>()
            : new object[] { new { id = config.Id, taskId = taskId.Value, url = config.Url.ToString() } };
        return Result(requestId, new { configs, nextPageToken = "" });
    }

    private static async Task<IResult> DeleteAsync(
        JsonElement? requestId,
        IConversationRepository repository,
        Principal principal,
        ConversationTaskId taskId,
        string? id,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new A2aProtocolException("invalid_params", "The push notification request requires id.");
        var config = await repository.GetPushConfigAsync(principal.Owner, taskId, cancellationToken);
        if (config is not null && StringComparer.Ordinal.Equals(config.Id, id))
            await repository.DeletePushConfigAsync(principal.Owner, taskId, cancellationToken);
        return Result(requestId, new { });
    }

    private static PushNotificationConfig ReadConfig(JsonElement config)
    {
        var url = RequireUrl(OptionalString(config, "url"));
        var id = OptionalString(config, "id") ?? Guid.NewGuid().ToString("D");
        var token = OptionalString(config, "token");
        string? scheme = null;
        string? credentials = null;
        if (config.TryGetProperty("authentication", out var authentication))
        {
            if (authentication.ValueKind != JsonValueKind.Object)
                throw new A2aProtocolException("invalid_params", "Push notification authentication must be an object.");
            scheme = OptionalString(authentication, "scheme");
            credentials = OptionalString(authentication, "credentials");
        }

        return new(id, url, token, scheme, credentials);
    }

    private static Uri RequireUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            string.IsNullOrEmpty(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new A2aProtocolException("invalid_params", "The push notification url must be an absolute http or https URL.");
        }

        return uri;
    }

    private static string RequireTaskId(JsonElement parameters, JsonElement config) =>
        OptionalString(parameters, "taskId")
        ?? OptionalString(config, "taskId")
        ?? throw new A2aProtocolException("invalid_params", "The push notification request requires taskId.");

    private static JsonElement? Nested(JsonElement parent)
    {
        if (parent.TryGetProperty("taskPushNotificationConfig", out var nested) && nested.ValueKind == JsonValueKind.Object)
            return nested;
        if (parent.TryGetProperty("pushNotificationConfig", out nested) && nested.ValueKind == JsonValueKind.Object)
            return nested;
        return null;
    }

    private static bool TryMethod(string? method, out PushMethod parsed)
    {
        switch (method)
        {
            case "tasks/pushNotificationConfig/set":
            case "CreateTaskPushNotificationConfig":
                parsed = PushMethod.Set;
                return true;
            case "tasks/pushNotificationConfig/get":
            case "GetTaskPushNotificationConfig":
                parsed = PushMethod.Get;
                return true;
            case "tasks/pushNotificationConfig/list":
            case "ListTaskPushNotificationConfigs":
                parsed = PushMethod.List;
                return true;
            case "tasks/pushNotificationConfig/delete":
            case "DeleteTaskPushNotificationConfig":
                parsed = PushMethod.Delete;
                return true;
            default:
                parsed = default;
                return false;
        }
    }

    private static string? OptionalString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static IResult Result(JsonElement? requestId, object result) =>
        Results.Json(new { jsonrpc = "2.0", id = requestId, result });

    private enum PushMethod { Set, Get, List, Delete }
}
