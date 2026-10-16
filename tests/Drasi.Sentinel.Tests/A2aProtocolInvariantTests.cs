using System.Text;
using System.Text.Json;
using Drasi.Sentinel.Application;
using Drasi.Sentinel.Domain;
using Drasi.Sentinel.Host;
using Microsoft.AspNetCore.Http;

namespace Drasi.Sentinel.Tests;

public sealed class A2aProtocolInvariantTests
{
    [Theory]
    [InlineData("message/send", "user", false, A2aStreamingMethod.SendMessage)]
    [InlineData("SendMessage", "ROLE_USER", true, A2aStreamingMethod.SendMessage)]
    [InlineData("message/stream", "user", false, A2aStreamingMethod.SendStreamingMessage)]
    [InlineData("SendStreamingMessage", "ROLE_USER", true, A2aStreamingMethod.SendStreamingMessage)]
    public void Send_methods_accept_standard_and_inspector_v1_user_parts(string method, string role, bool inspector, A2aStreamingMethod expected)
    {
        using var document = JsonDocument.Parse(
            $"{{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"method\":\"{method}\",\"params\":{{\"message\":{{\"role\":\"{role}\",\"taskId\":\"task\",\"messageId\":\"message\",\"parts\":[{{\"text\":\"watch changes\",\"mediaType\":\"text/plain\"}}]}}}}}}");
        var request = A2aMessageRequest.Parse(document.RootElement);
        Assert.Equal(expected, request.Method);
        Assert.Equal(inspector, request.UsesInspectorV1);
        Assert.Equal("watch changes", request.Message.Text);
    }

    [Theory]
    [InlineData("tasks/subscribe")]
    [InlineData("SubscribeToTask")]
    public void Subscribe_methods_accept_task_id(string method)
    {
        using var document = JsonDocument.Parse(
            $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"{method}\",\"params\":{{\"taskId\":\"task\"}}}}");
        Assert.Equal(A2aStreamingMethod.SubscribeToTask, A2aMessageRequest.Parse(document.RootElement).Method);
    }

    [Theory]
    [InlineData("assistant", "message/send")]
    [InlineData("user", "unknown")]
    public void Unsupported_messages_are_rejected(string role, string method)
    {
        using var document = JsonDocument.Parse(
            $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"{method}\",\"params\":{{\"message\":{{\"role\":\"{role}\",\"messageId\":\"message\",\"parts\":[{{\"text\":\"x\"}}]}}}}}}");
        Assert.Throws<A2aProtocolException>(() => A2aMessageRequest.Parse(document.RootElement));
    }

    [Fact]
    public async Task Sse_projects_standard_v1_status_and_artifact_events_with_task_local_ids()
    {
        var repository = new InMemorySentinelRepository();
        var notifier = new TaskEventNotifier();
        var principal = new Principal(new("tenant"), new("owner"));
        await repository.GetOrCreateTaskAsync(principal, new("task"), CancellationToken.None);
        await repository.AppendEventAsync(principal.Owner, new(new("task"), default, ConversationEventKind.TaskWorking, null, "Task is working.", DateTimeOffset.UtcNow), CancellationToken.None);
        await repository.AppendEventAsync(principal.Owner, new(new("task"), default, ConversationEventKind.Observation, null, ObservationJson(), DateTimeOffset.UtcNow), CancellationToken.None);
        await repository.AppendEventAsync(principal.Owner, new(new("task"), default, ConversationEventKind.TaskCompleted, null, "Done.", DateTimeOffset.UtcNow), CancellationToken.None);

        using var requestIdDocument = JsonDocument.Parse("\"request-1\"");
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        await new SseTaskResult(requestIdDocument.RootElement.Clone(), principal, new("task"), new Reader(repository), notifier, 0, true).ExecuteAsync(context);

        context.Response.Body.Position = 0;
        var body = Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
        Assert.Contains("id: 1", body);
        Assert.Contains("\"statusUpdate\"", body);
        Assert.Contains("\"state\":\"TASK_STATE_WORKING\"", body);
        Assert.Contains("\"artifactUpdate\"", body);
        Assert.Contains("\"artifactId\":\"2\"", body);
        Assert.Contains("\"data\":{\"type\":\"native_query_diff\"", body);
        Assert.DoesNotContain("Observed.", body);
        Assert.DoesNotContain("\"kind\":", body);
    }

    [Fact]
    public async Task Sse_projects_observations_as_standard_data_parts()
    {
        var repository = new InMemorySentinelRepository();
        var notifier = new TaskEventNotifier();
        var principal = new Principal(new("tenant"), new("owner"));
        await repository.GetOrCreateTaskAsync(principal, new("task"), CancellationToken.None);
        await repository.AppendEventAsync(principal.Owner, new(new("task"), default, ConversationEventKind.Observation, null, ObservationJson(), DateTimeOffset.UtcNow), CancellationToken.None);
        await repository.AppendEventAsync(principal.Owner, new(new("task"), default, ConversationEventKind.TaskCompleted, null, "Done.", DateTimeOffset.UtcNow), CancellationToken.None);

        using var requestIdDocument = JsonDocument.Parse("1");
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        await new SseTaskResult(requestIdDocument.RootElement.Clone(), principal, new("task"), new Reader(repository), notifier, 0).ExecuteAsync(context);

        var body = Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
        Assert.Contains("\"kind\":\"data\",\"data\":{\"type\":\"native_query_diff\"", body);
        Assert.DoesNotContain("\"text\":\"{", body);
    }

    [Fact]
    public async Task Sse_projects_current_results_as_a_data_artifact()
    {
        var repository = new InMemorySentinelRepository();
        var notifier = new TaskEventNotifier();
        var principal = new Principal(new("tenant"), new("owner"));
        await repository.GetOrCreateTaskAsync(principal, new("task"), CancellationToken.None);
        await repository.AppendEventAsync(principal.Owner, new(new("task"), default, ConversationEventKind.Observation, null, """
            {"type":"current_query_results","taskId":"task","subscriptionId":"00000000-0000-0000-0000-000000000001","queryId":"query","rows":[{"id":"row-1"}]}
            """, DateTimeOffset.UtcNow), CancellationToken.None);
        await repository.AppendEventAsync(principal.Owner, new(new("task"), default, ConversationEventKind.TaskCompleted, null, "Done.", DateTimeOffset.UtcNow), CancellationToken.None);

        using var requestIdDocument = JsonDocument.Parse("1");
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        await new SseTaskResult(requestIdDocument.RootElement.Clone(), principal, new("task"), new Reader(repository), notifier, 0).ExecuteAsync(context);

        var body = Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
        Assert.Contains("\"kind\":\"data\",\"data\":{\"type\":\"current_query_results\",\"taskId\":\"task\"", body);
        Assert.Contains("\"rows\":[{\"id\":\"row-1\"}]", body);
    }

    [Fact]
    public async Task Sse_replay_starts_strictly_after_last_event_id()
    {
        var repository = new InMemorySentinelRepository();
        var notifier = new TaskEventNotifier();
        var principal = new Principal(new("tenant"), new("owner"));
        await repository.GetOrCreateTaskAsync(principal, new("task"), CancellationToken.None);
        await repository.AppendEventAsync(principal.Owner, new(new("task"), default, ConversationEventKind.Observation, null, ObservationJson(), DateTimeOffset.UtcNow), CancellationToken.None);
        await repository.AppendEventAsync(principal.Owner, new(new("task"), default, ConversationEventKind.TaskCompleted, null, "Done.", DateTimeOffset.UtcNow), CancellationToken.None);

        using var requestIdDocument = JsonDocument.Parse("1");
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        await new SseTaskResult(requestIdDocument.RootElement.Clone(), principal, new("task"), new Reader(repository), notifier, 1).ExecuteAsync(context);

        var body = Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
        Assert.DoesNotContain("id: 1", body);
        Assert.Contains("id: 2", body);
    }

    [Fact]
    public void Idle_sse_comment_interval_stays_under_a_thirty_second_proxy_timeout()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), SseTaskResult.IdleCommentInterval);
    }

    [Fact]
    public async Task Idle_sse_stream_sends_comments_and_then_the_next_event()
    {
        var repository = new InMemorySentinelRepository();
        var notifier = new TaskEventNotifier();
        var principal = new Principal(new("tenant"), new("owner"));
        var taskId = new ConversationTaskId("task");
        await repository.GetOrCreateTaskAsync(principal, taskId, CancellationToken.None);

        using var requestIdDocument = JsonDocument.Parse("1");
        using var abort = new CancellationTokenSource();
        var body = new LockedBody();
        var context = new DefaultHttpContext();
        context.RequestAborted = abort.Token;
        context.Response.Body = body;
        var running = new SseTaskResult(
            requestIdDocument.RootElement.Clone(),
            principal,
            taskId,
            new Reader(repository),
            notifier,
            0,
            heartbeatInterval: TimeSpan.FromMilliseconds(20)).ExecuteAsync(context);

        try
        {
            await WaitUntilAsync(() => body.Text().Split(": ping\n\n").Length - 1 >= 2);
            Assert.False(running.IsCompleted);
            Assert.DoesNotContain("id:", body.Text(), StringComparison.Ordinal);
            Assert.DoesNotContain("data:", body.Text(), StringComparison.Ordinal);

            await repository.AppendEventAsync(
                principal.Owner,
                new(taskId, default, ConversationEventKind.Observation, null, ObservationJson(), DateTimeOffset.UtcNow),
                CancellationToken.None);
            notifier.Notify(taskId);

            await WaitUntilAsync(() => body.Text().Contains("id: 1", StringComparison.Ordinal));
            var delivered = body.Text();
            Assert.Contains(": ping\n\n", delivered, StringComparison.Ordinal);
            Assert.Contains("native_query_diff", delivered, StringComparison.Ordinal);
            Assert.False(running.IsCompleted);
        }
        finally
        {
            abort.Cancel();
            try { await running; }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task Sse_replay_continues_past_a_terminal_event_that_is_not_latest()
    {
        var principal = new Principal(new("tenant"), new("owner"));
        var taskId = new ConversationTaskId("task");
        var reads = 0;
        var reader = new PageReader(() =>
        {
            reads++;
            return reads switch
            {
                1 => [Event(1, ConversationEventKind.TaskFailed, "The model failed.")],
                2 => [Event(2, ConversationEventKind.Observation, ObservationJson())],
                _ => [Event(2, ConversationEventKind.Observation, ObservationJson()), Event(3, ConversationEventKind.TaskCompleted, "Done.")],
            };
        });
        using var requestIdDocument = JsonDocument.Parse("1");
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await new SseTaskResult(requestIdDocument.RootElement.Clone(), principal, taskId, reader, new TaskEventNotifier(), 0).ExecuteAsync(context);

        var body = Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
        Assert.Contains("id: 1", body);
        Assert.Contains("The model failed.", body);
        Assert.Contains("id: 2", body);
        Assert.Contains("native_query_diff", body);
        Assert.Contains("id: 3", body);
    }

    private static string ObservationJson() => """
        {"type":"native_query_diff","subscriptionId":"00000000-0000-0000-0000-000000000001","queryId":"query","reactionId":"reaction","occurredAt":"2026-10-06T00:00:00+00:00","diffType":"Add","data":{"entity_id":"entity-200"},"metadata":{"source":"test"}}
        """;

    private static ConversationEvent Event(long sequence, ConversationEventKind kind, string content) =>
        new(new("task"), new(sequence), kind, null, content, DateTimeOffset.UtcNow);

    private sealed class PageReader(Func<IReadOnlyList<ConversationEvent>> read) : IConversationSubscriptions
    {
        public Task<ToolResult> InvokeAsync(Principal principal, TaskTurnId turn, GovernedToolCall call, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ConversationEvent>> ReadEventsAsync(Principal principal, ConversationTaskId taskId, TaskEventSequence after, int limit, CancellationToken cancellationToken) =>
            Task.FromResult(read().Where(item => item.Sequence.Value > after.Value).Take(limit).ToArray() as IReadOnlyList<ConversationEvent>);
        public Task RestoreWatchesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static async Task WaitUntilAsync(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class LockedBody : MemoryStream
    {
        private readonly object _gate = new();

        public string Text()
        {
            lock (_gate)
                return Encoding.UTF8.GetString(ToArray());
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_gate)
                base.Write(buffer, offset, count);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            lock (_gate)
                base.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Reader(IConversationRepository repository) : IConversationSubscriptions
    {
        public Task<ToolResult> InvokeAsync(Principal principal, TaskTurnId turn, GovernedToolCall call, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ConversationEvent>> ReadEventsAsync(Principal principal, ConversationTaskId taskId, TaskEventSequence after, int limit, CancellationToken cancellationToken) =>
            repository.ReadEventsAsync(principal.Owner, taskId, after, limit, cancellationToken);
        public Task RestoreWatchesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
