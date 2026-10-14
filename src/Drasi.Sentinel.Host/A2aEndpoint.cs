using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Drasi.Sentinel.Application;
using Drasi.Sentinel.Domain;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Drasi.Sentinel.Host;

public sealed class UnconfiguredAgentResponder : IAgentResponder
{
    public Task<string> RespondAsync(
        Principal principal,
        TaskTurnId turn,
        string input,
        IReadOnlyList<AgentMessage> priorTurns,
        CancellationToken cancellationToken) =>
        throw new A2aAgentUnconfiguredException();
}

public sealed class A2aAgentUnconfiguredException() : Exception("No agent is configured in the Sentinel manifest.");

public static class A2aJwtAuthentication
{
    public static void Configure(JwtBearerOptions options, A2aJwtManifest manifest)
    {
        options.MapInboundClaims = false;
        var rsa = RSA.Create();
        try { rsa.ImportFromPem(manifest.PublicKeyPem); }
        catch (CryptographicException exception) { rsa.Dispose(); throw new ManifestLoadException("service.a2a.authentication.public_key must contain a valid PEM RSA public key.", exception); }
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true, IssuerSigningKey = new RsaSecurityKey(rsa),
            ValidateIssuer = true, ValidIssuer = manifest.Issuer,
            ValidateAudience = true, ValidAudience = manifest.Audience,
            ValidateLifetime = true, RequireExpirationTime = true, RequireSignedTokens = true, ClockSkew = TimeSpan.FromMinutes(1),
        };
    }
}

public static class A2aEndpoint
{
    public static async Task<IResult> SendAsync(
        HttpContext context,
        IConversationRepository repository,
        IConversationSubscriptions subscriptions,
        ITaskEventNotifier notifier,
        IAgentResponder responder,
        A2aAuthenticationManifest authentication)
    {
        A2aMessageRequest request;
        JsonElement? requestId = null;
        try
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            if (document.RootElement.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.String or JsonValueKind.Number) requestId = id.Clone();
            if (document.RootElement.TryGetProperty("method", out var methodElement) &&
                methodElement.ValueKind == JsonValueKind.String &&
                A2aPushNotification.IsMethod(methodElement.GetString()))
            {
                Principal pushPrincipal;
                try { pushPrincipal = A2aPrincipalFactory.Create(context.User, authentication); }
                catch (A2aProtocolException exception) { return Error(requestId, -32001, exception.Message, exception.Code, StatusCodes.Status401Unauthorized); }
                return await A2aPushNotification.HandleAsync(
                    document.RootElement,
                    requestId,
                    pushPrincipal,
                    repository,
                    context.RequestAborted);
            }
            request = A2aMessageRequest.Parse(document.RootElement);
        }
        catch (JsonException) { return Error(null, -32700, "The request body must be valid JSON."); }
        catch (A2aProtocolException exception) { return Error(requestId, exception.Code == "method_not_found" ? -32601 : -32602, exception.Message, exception.Code); }
        Principal principal;
        try { principal = A2aPrincipalFactory.Create(context.User, authentication); }
        catch (A2aProtocolException exception) { return Error(request.RequestId, -32001, exception.Message, exception.Code, StatusCodes.Status401Unauthorized); }

        var taskId = new ConversationTaskId(request.Message.TaskId);
        try
        {
            if (request.Method == A2aStreamingMethod.SendMessage)
            {
                var turn = new TaskTurnId(taskId.Value, request.Message.MessageId);
                await repository.GetOrCreateTaskAsync(principal, taskId, context.RequestAborted);
                await RememberPushConfigAsync(repository, principal, taskId, request, context.RequestAborted);
                var priorTurns = await ConversationTurnHistory.LoadTurnsAsync(
                    repository, principal.Owner, taskId, context.RequestAborted);
                await RememberCallerAsync(repository, principal, taskId, request.Message.Text, context.RequestAborted);
                var response = await responder.RespondAsync(
                    principal, turn, request.Message.Text, priorTurns, context.RequestAborted);
                await repository.AppendAssistantArtifactAsync(principal, taskId, response, context.RequestAborted);
                notifier.Notify(taskId);
                return Results.Json(OneShot(request, taskId, response));
            }
            if (request.Method == A2aStreamingMethod.SendStreamingMessage)
            {
                var turn = new TaskTurnId(taskId.Value, request.Message.MessageId);
                await repository.GetOrCreateTaskAsync(principal, taskId, context.RequestAborted);
                await RememberPushConfigAsync(repository, principal, taskId, request, context.RequestAborted);
                await repository.AppendEventAsync(
                    principal.Owner,
                    new ConversationEvent(
                        taskId,
                        default,
                        ConversationEventKind.TaskWorking,
                        null,
                        "Task is working.",
                        DateTimeOffset.UtcNow),
                    context.RequestAborted);
                notifier.Notify(taskId);
                _ = CompleteStreamingTurnAsync(
                    principal,
                    taskId,
                    turn,
                    request.Message.Text,
                    repository,
                    notifier,
                    responder,
                    context.RequestServices?.GetService<IHostApplicationLifetime>()?.ApplicationStopping ?? CancellationToken.None);
            }
            else
            {
                await repository.GetOrCreateTaskAsync(principal, taskId, context.RequestAborted);
            }
            return new SseTaskResult(
                request.RequestId,
                principal,
                taskId,
                subscriptions,
                notifier,
                ResumeAfter(context),
                request.UsesInspectorV1);
        }
        catch (A2aAgentUnconfiguredException exception) { return Error(request.RequestId, -32003, exception.Message, "agent_unconfigured", StatusCodes.Status503ServiceUnavailable); }
        catch (AgentFailureException exception) when (exception.Failure == AgentFailure.CredentialUnavailable) { return Error(request.RequestId, -32003, exception.Message, "agent_credential_unavailable", StatusCodes.Status503ServiceUnavailable); }
        catch (AgentFailureException exception) when (exception.Failure == AgentFailure.ProviderUnavailable) { return Error(request.RequestId, -32003, exception.Message, "agent_provider_unavailable", StatusCodes.Status503ServiceUnavailable); }
        catch (SentinelException exception) { return Error(request.RequestId, -32002, exception.Message, exception.Code); }
    }

    private static Task RememberCallerAsync(
        IConversationRepository repository,
        Principal principal,
        ConversationTaskId taskId,
        string input,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input))
            return Task.CompletedTask;
        return repository.AppendEventAsync(
            principal.Owner,
            new ConversationEvent(taskId, default, ConversationEventKind.CallerUtterance, null, input, DateTimeOffset.UtcNow),
            cancellationToken);
    }

    private static async Task RememberPushConfigAsync(
        IConversationRepository repository,
        Principal principal,
        ConversationTaskId taskId,
        A2aMessageRequest request,
        CancellationToken cancellationToken)
    {
        if (request.PushConfig is { } config)
            await repository.SetPushConfigAsync(principal.Owner, taskId, config, cancellationToken);
    }

    private static long ResumeAfter(HttpContext context) =>
        long.TryParse(context.Request.Headers["Last-Event-ID"], out var parsed) && parsed >= 0 ? parsed : 0;

    private static async Task CompleteStreamingTurnAsync(
        Principal principal,
        ConversationTaskId taskId,
        TaskTurnId turn,
        string input,
        IConversationRepository repository,
        ITaskEventNotifier notifier,
        IAgentResponder responder,
        CancellationToken cancellationToken)
    {
        try
        {
            var priorTurns = await ConversationTurnHistory.LoadTurnsAsync(
                repository,
                principal.Owner,
                taskId,
                CancellationToken.None);
            await RememberCallerAsync(repository, principal, taskId, input, CancellationToken.None);
            var response = await responder.RespondAsync(
                principal,
                turn,
                input,
                priorTurns,
                cancellationToken);
            await repository.AppendAssistantArtifactAsync(
                principal,
                taskId,
                response,
                CancellationToken.None);
            notifier.Notify(taskId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            var text = exception is A2aAgentUnconfiguredException or AgentFailureException or SentinelException
                ? exception.Message
                : "The task could not be completed.";
            await repository.AppendAssistantArtifactAsync(principal, taskId, text, CancellationToken.None);
            notifier.Notify(taskId);
        }
    }

    private static object OneShot(A2aMessageRequest request, ConversationTaskId task, string response) =>
        request.UsesInspectorV1
            ? new { jsonrpc = "2.0", id = request.RequestId, result = new { task = new { id = task.Value, contextId = task.Value, status = new { state = "TASK_STATE_WORKING" }, artifacts = new[] { new { parts = new[] { new { text = response } } } } } } }
            : new { jsonrpc = "2.0", id = request.RequestId, result = new { id = task.Value, contextId = task.Value, kind = "task", status = new { state = "working" }, artifacts = new[] { new { parts = new[] { new { kind = "text", text = response } } } } } };

    private static IResult Error(JsonElement? id, int code, string message, string? sentinelCode = null, int? status = null) =>
        Results.Json(new { jsonrpc = "2.0", id, error = new { code, message, data = sentinelCode is null ? null : new { code = sentinelCode } } }, statusCode: status);
}

internal sealed class SseTaskResult(
    JsonElement requestId,
    Principal principal,
    ConversationTaskId taskId,
    IConversationSubscriptions subscriptions,
    ITaskEventNotifier notifier,
    long after,
    bool usesStandardV1 = false) : IResult
{
    public async Task ExecuteAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";
        var cursor = new TaskEventSequence(after);
        var observed = notifier.Observe(taskId);
        while (!context.RequestAborted.IsCancellationRequested)
        {
            var events = await subscriptions.ReadEventsAsync(principal, taskId, cursor, 100, context.RequestAborted);
            ConversationEvent? last = null;
            foreach (var item in events)
            {
                if (item.Kind == ConversationEventKind.CallerUtterance)
                {
                    cursor = item.Sequence;
                    last = item;
                    continue;
                }
                await context.Response.WriteAsync($"id: {item.Sequence.Value}\ndata: {JsonSerializer.Serialize(Project(item))}\n\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                cursor = item.Sequence;
                last = item;
            }
            if (last?.Kind is ConversationEventKind.TaskCompleted or ConversationEventKind.TaskFailed)
            {
                var later = await subscriptions.ReadEventsAsync(principal, taskId, cursor, 1, context.RequestAborted);
                if (later.Count == 0)
                    return;
                continue;
            }
            await notifier.WaitAsync(taskId, observed, context.RequestAborted);
            observed = notifier.Observe(taskId);
        }
    }

    private object Project(ConversationEvent item)
    {
        if (usesStandardV1)
        {
            return ProjectStandardV1(item);
        }

        object result = item.Kind switch
        {
            ConversationEventKind.TaskWorking => new
            {
                kind = "status-update",
                taskId = item.TaskId.Value,
                contextId = item.TaskId.Value,
                status = new
                {
                    state = "TASK_STATE_WORKING",
                    message = new
                    {
                        messageId = item.Sequence.Value.ToString(),
                        role = "ROLE_AGENT",
                        taskId = item.TaskId.Value,
                        contextId = item.TaskId.Value,
                        parts = ArtifactParts(item),
                    },
                    timestamp = item.OccurredAt,
                },
                final = false,
            },
            ConversationEventKind.TaskCompleted or ConversationEventKind.TaskFailed => new
            {
                kind = "status-update",
                taskId = item.TaskId.Value,
                contextId = item.TaskId.Value,
                status = new
                {
                    state = item.Kind == ConversationEventKind.TaskCompleted ? "TASK_STATE_COMPLETED" : "TASK_STATE_FAILED",
                    message = new
                    {
                        messageId = item.Sequence.Value.ToString(),
                        role = "ROLE_AGENT",
                        taskId = item.TaskId.Value,
                        contextId = item.TaskId.Value,
                        parts = new[] { new { kind = "text", text = item.Content, mediaType = "text/plain" } },
                    },
                    timestamp = item.OccurredAt,
                },
                final = true,
            },
            _ => new
            {
                kind = "artifact-update",
                taskId = item.TaskId.Value,
                contextId = item.TaskId.Value,
                artifact = new
                {
                    artifactId = item.Sequence.Value.ToString(),
                    parts = ArtifactParts(item),
                },
                append = false,
                lastChunk = true,
            },
        };
        return new { jsonrpc = "2.0", id = requestId, result };
    }

    private object ProjectStandardV1(ConversationEvent item)
    {
        object result = item.Kind switch
        {
            ConversationEventKind.TaskWorking => new
            {
                statusUpdate = new
                {
                    taskId = item.TaskId.Value,
                    contextId = item.TaskId.Value,
                    status = new
                    {
                        state = "TASK_STATE_WORKING",
                        message = new
                        {
                            messageId = item.Sequence.Value.ToString(),
                            role = "ROLE_AGENT",
                            taskId = item.TaskId.Value,
                            contextId = item.TaskId.Value,
                            parts = StandardV1ArtifactParts(item),
                        },
                        timestamp = item.OccurredAt,
                    },
                },
            },
            ConversationEventKind.TaskCompleted or ConversationEventKind.TaskFailed => new
            {
                statusUpdate = new
                {
                    taskId = item.TaskId.Value,
                    contextId = item.TaskId.Value,
                    status = new
                    {
                        state = item.Kind == ConversationEventKind.TaskCompleted ? "TASK_STATE_COMPLETED" : "TASK_STATE_FAILED",
                        message = new
                        {
                            messageId = item.Sequence.Value.ToString(),
                            role = "ROLE_AGENT",
                            taskId = item.TaskId.Value,
                            contextId = item.TaskId.Value,
                            parts = new[] { new { text = item.Content, mediaType = "text/plain" } },
                        },
                        timestamp = item.OccurredAt,
                    },
                },
            },
            _ => new
            {
                artifactUpdate = new
                {
                    taskId = item.TaskId.Value,
                    contextId = item.TaskId.Value,
                    artifact = new
                    {
                        artifactId = item.Sequence.Value.ToString(),
                        parts = StandardV1ArtifactParts(item),
                    },
                    append = false,
                    lastChunk = true,
                },
            },
        };
        return new { jsonrpc = "2.0", id = requestId, result };
    }

    private static object[] ArtifactParts(ConversationEvent item) =>
        item.Kind == ConversationEventKind.Observation
            ? [new { kind = "data", data = ObservationData(item) }]
            : [new { kind = "text", text = item.Content, mediaType = "text/plain" }];

    private static object[] StandardV1ArtifactParts(ConversationEvent item) =>
        item.Kind == ConversationEventKind.Observation
            ? [new { data = ObservationData(item) }]
            : [new { text = item.Content, mediaType = "text/plain" }];

    private static JsonElement ObservationData(ConversationEvent item)
    {
        using var document = JsonDocument.Parse(item.Content);
        return document.RootElement.Clone();
    }
}
