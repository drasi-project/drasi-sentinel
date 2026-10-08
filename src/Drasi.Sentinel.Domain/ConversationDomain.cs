using System.Text.Json.Nodes;

namespace Drasi.Sentinel.Domain;

public readonly record struct TenantId(string Value);
public readonly record struct OwnerId(string Value);
public readonly record struct QueryId(string Value);
public readonly record struct ReactionId(string Value);
public readonly record struct ConversationTaskId(string Value);
public readonly record struct SubscriptionId(Guid Value);
public readonly record struct NativeSubscriptionId(string Value);
public readonly record struct TaskEventSequence(long Value);
public readonly record struct SubscriptionRequestKey(string Value);
public readonly record struct TaskTurnId(string TaskId, string MessageId);

public sealed record Principal(
    TenantId Tenant,
    OwnerId Owner);

public enum ConversationTaskState { Working, Completed, Cancelled, Failed }
public enum SubscriptionState { Arming, Active, Paused, Cancelled, Revoked, Expired, Degraded }
public enum ConversationEventKind
{
    AssistantArtifact,
    TaskWorking,
    SubscriptionCreated,
    SubscriptionChanged,
    Observation,
    TaskCompleted,
    TaskFailed,
}

public sealed record ConversationTask(
    ConversationTaskId Id,
    TenantId Tenant,
    OwnerId Owner,
    ConversationTaskState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SubscriptionRequest(
    QueryId QueryId,
    ReactionId ReactionId,
    SubscriptionRequestKey RequestKey);

public sealed record ConversationSubscription(
    SubscriptionId Id,
    ConversationTaskId TaskId,
    TenantId Tenant,
    OwnerId Owner,
    SubscriptionRequest Request,
    SubscriptionState State,
    NativeSubscriptionId NativeSubscriptionId,
    DateTimeOffset CreatedAt);

public sealed record ConversationEvent(
    ConversationTaskId TaskId,
    TaskEventSequence Sequence,
    ConversationEventKind Kind,
    SubscriptionId? SubscriptionId,
    string Content,
    DateTimeOffset OccurredAt);

public sealed class NativeQueryDiff
{
    public NativeQueryDiff(
        string type,
        JsonObject? data,
        JsonObject? before,
        JsonObject? after,
        JsonArray? groupingKeys,
        JsonObject? metadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        Type = type;
        Data = Clone(data);
        Before = Clone(before);
        After = Clone(after);
        GroupingKeys = Clone(groupingKeys);
        Metadata = Clone(metadata);
    }

    public string Type { get; }
    public JsonObject? Data { get; }
    public JsonObject? Before { get; }
    public JsonObject? After { get; }
    public JsonArray? GroupingKeys { get; }
    public JsonObject? Metadata { get; }

    private static JsonObject? Clone(JsonObject? value) => value?.DeepClone().AsObject();
    private static JsonArray? Clone(JsonArray? value) => value?.DeepClone().AsArray();
}

public sealed record QueryCatalogItem(
    QueryId Id,
    string Description,
    IReadOnlySet<ReactionId> AllowedReactions,
    IReadOnlyList<string> Examples);

public sealed record ReactionCatalogItem(ReactionId Id);

public sealed record SafeCatalog(
    IReadOnlyDictionary<QueryId, QueryCatalogItem> Queries,
    IReadOnlyDictionary<ReactionId, ReactionCatalogItem> Reactions);

public sealed class SentinelException(string code, string message)
    : Exception(message)
{
    public string Code { get; } = code;
}
