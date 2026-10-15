using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Drasi.Sentinel.Application;
using Drasi.Sentinel.Domain;
using Drasi.Sentinel.Host;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Drasi.Sentinel.Tests;

public sealed class A2aStreamingTests
{
    [Fact]
    public void Inspector_streaming_requests_preserve_protobuf_user_parts()
    {
        using var document = JsonDocument.Parse("""
            {"jsonrpc":"2.0","id":"1","method":"SendStreamingMessage","params":{"message":{"role":"ROLE_USER","taskId":"task-1","messageId":"message-1","parts":[{"text":"Tell me about changes."}]}}}
            """);
        var request = A2aMessageRequest.Parse(document.RootElement);
        Assert.Equal(A2aStreamingMethod.SendStreamingMessage, request.Method);
        Assert.True(request.UsesInspectorV1);
        Assert.Equal("task-1", request.Message.TaskId);
    }

    [Fact]
    public void Follow_up_without_task_id_continues_the_context()
    {
        using var document = JsonDocument.Parse("""
            {"jsonrpc":"2.0","id":"2","method":"SendStreamingMessage","params":{"message":{"role":"ROLE_USER","contextId":"task-1","messageId":"msg-2","parts":[{"text":"vehicle location"}]}}}
            """);
        var request = A2aMessageRequest.Parse(document.RootElement);
        Assert.Equal("task-1", request.Message.TaskId);
        Assert.Equal("msg-2", request.Message.MessageId);
        Assert.Equal("vehicle location", request.Message.Text);
    }

    [Fact]
    public async Task A_new_watch_request_does_not_reuse_an_occupied_task()
    {
        var repository = new InMemorySentinelRepository();
        var principal = A2aPrincipalFactory.Create(new ClaimsPrincipal(), new A2aAnonymousManifest());
        await repository.GetOrCreateTaskAsync(principal, new("watch-1"), CancellationToken.None);
        await repository.GetOrCreateSubscriptionAsync(new ConversationSubscription(
            new(Guid.NewGuid()),
            new("watch-1"),
            principal.Tenant,
            principal.Owner,
            new(new("pickup-order-preparing-watch"), new("notify"), new("one")),
            SubscriptionState.Active,
            new("reaction"),
            DateTimeOffset.UnixEpoch,
            new Unbounded()), CancellationToken.None);
        var seen = new List<string>();
        var responder = new RecordingResponder(seen);

        await SendAsync(repository, responder, """
            {"jsonrpc":"2.0","id":"2","method":"SendMessage","params":{"message":{"role":"ROLE_USER","contextId":"watch-1","messageId":"msg-parking","parts":[{"text":"Tell me when a pickup vehicle is in the parking lot. Keep the watch for 7 days."}]}}}
            """);
        await SendAsync(repository, responder, """
            {"jsonrpc":"2.0","id":"3","method":"SendMessage","params":{"message":{"role":"ROLE_USER","contextId":"watch-1","messageId":"msg-rows","parts":[{"text":"Show the current rows."}]}}}
            """);

        Assert.Equal(["msg-parking", "watch-1"], seen);
        Assert.False(WatchTurn.ContinuesOccupiedTask("Buzz me about trucks sitting out front."));
        Assert.True(WatchTurn.ContinuesOccupiedTask("What watches do I have?"));
        Assert.False(WatchTurn.ContinuesOccupiedTask("Show me when a vehicle is waiting."));
        Assert.False(WatchTurn.ContinuesOccupiedTask("I need another watch: orders whose status is ready. Keep it for 1h."));
        Assert.True(WatchTurn.ContinuesOccupiedTask("What is the status of my watches?"));
        Assert.Equal(0, A2aEndpoint.ResumeCursor(8, "watch-1", "msg-parking"));
        Assert.Equal(8, A2aEndpoint.ResumeCursor(8, "watch-1", "watch-1"));
        Assert.Contains(
            await repository.ReadEventsAsync(principal.Owner, new("msg-parking"), new(0), 20, CancellationToken.None),
            item => item.Kind == ConversationEventKind.CallerUtterance && item.Content.Contains("parking lot", StringComparison.Ordinal));
        Assert.False(await repository.TaskExistsAsync(principal.Owner, new("msg-rows"), CancellationToken.None));
    }

    [Fact]
    public async Task An_unseen_watch_phrase_does_not_reuse_an_occupied_task()
    {
        var repository = new InMemorySentinelRepository();
        var principal = A2aPrincipalFactory.Create(new ClaimsPrincipal(), new A2aAnonymousManifest());
        await repository.GetOrCreateTaskAsync(principal, new("watch-1"), CancellationToken.None);
        await repository.GetOrCreateSubscriptionAsync(new ConversationSubscription(
            new(Guid.NewGuid()),
            new("watch-1"),
            principal.Tenant,
            principal.Owner,
            new(new("ready-orders"), new("notify"), new("one")),
            SubscriptionState.Active,
            new("reaction"),
            DateTimeOffset.UnixEpoch,
            new Unbounded()), CancellationToken.None);
        var seen = new List<string>();
        await SendAsync(repository, new RecordingResponder(seen), """
            {"jsonrpc":"2.0","id":"4","method":"SendMessage","params":{"message":{"role":"ROLE_USER","contextId":"watch-1","messageId":"msg-buzz","parts":[{"text":"Buzz me about trucks sitting out front. Keep the watch for 1h."}]}}}
            """);
        Assert.Equal(["msg-buzz"], seen);
    }

    [Fact]
    public async Task A_mapping_answer_stays_before_a_subscription_exists()
    {
        var repository = new InMemorySentinelRepository();
        var principal = A2aPrincipalFactory.Create(new ClaimsPrincipal(), new A2aAnonymousManifest());
        await repository.GetOrCreateTaskAsync(principal, new("watch-1"), CancellationToken.None);
        await repository.AppendEventAsync(
            principal.Owner,
            new ConversationEvent(new("watch-1"), default, ConversationEventKind.CallerUtterance, null, "Tell me when the customer is waiting.", DateTimeOffset.UnixEpoch),
            CancellationToken.None);
        var seen = new List<string>();
        await SendAsync(repository, new RecordingResponder(seen), """
            {"jsonrpc":"2.0","id":"5","method":"SendMessage","params":{"message":{"role":"ROLE_USER","contextId":"watch-1","messageId":"msg-map","parts":[{"text":"Curbside"}]}}}
            """);
        Assert.Equal(["watch-1"], seen);
    }

    [Fact]
    public async Task List_tasks_returns_every_watch_for_the_caller()
    {
        var repository = new InMemorySentinelRepository();
        var principal = A2aPrincipalFactory.Create(new ClaimsPrincipal(), new A2aAnonymousManifest());
        await repository.GetOrCreateTaskAsync(principal, new("watch-a"), CancellationToken.None);
        await repository.GetOrCreateTaskAsync(principal, new("watch-b"), CancellationToken.None);
        await repository.GetOrCreateSubscriptionAsync(new ConversationSubscription(
            new(Guid.NewGuid()),
            new("watch-a"),
            principal.Tenant,
            principal.Owner,
            new(new("ready-orders"), new("notify"), new("a")),
            SubscriptionState.Active,
            new("reaction-a"),
            DateTimeOffset.UnixEpoch,
            new Unbounded()), CancellationToken.None);
        var body = await PostAsync(repository, """
            {"jsonrpc":"2.0","id":"9","method":"ListTasks","params":{}}
            """);
        using var document = JsonDocument.Parse(body);
        var ids = document.RootElement.GetProperty("result").GetProperty("tasks").EnumerateArray()
            .Select(task => task.GetProperty("id").GetString())
            .ToArray();
        Assert.Contains("watch-a", ids);
        Assert.Contains("watch-b", ids);
        Assert.Equal("ready-orders", document.RootElement.GetProperty("result").GetProperty("tasks").EnumerateArray()
            .Single(task => task.GetProperty("id").GetString() == "watch-a")
            .GetProperty("metadata").GetProperty("queryId").GetString());
        var one = await PostAsync(repository, """
            {"jsonrpc":"2.0","id":"12","method":"GetTask","params":{"id":"watch-b"}}
            """);
        using var got = JsonDocument.Parse(one);
        Assert.Equal("watch-b", got.RootElement.GetProperty("result").GetProperty("id").GetString());
        Assert.Equal("TASK_STATE_WORKING", got.RootElement.GetProperty("result").GetProperty("status").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Cancel_task_ends_one_watch_and_leaves_the_other()
    {
        var repository = new InMemorySentinelRepository();
        var principal = A2aPrincipalFactory.Create(new ClaimsPrincipal(), new A2aAnonymousManifest());
        await repository.GetOrCreateTaskAsync(principal, new("watch-a"), CancellationToken.None);
        await repository.GetOrCreateTaskAsync(principal, new("watch-b"), CancellationToken.None);
        await repository.GetOrCreateSubscriptionAsync(new ConversationSubscription(
            GuidSubscription("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            new("watch-a"),
            principal.Tenant,
            principal.Owner,
            new(new("ready-orders"), new("notify"), new("a")),
            SubscriptionState.Active,
            new("reaction-a"),
            DateTimeOffset.UnixEpoch,
            new Unbounded()), CancellationToken.None);
        await repository.GetOrCreateSubscriptionAsync(new ConversationSubscription(
            GuidSubscription("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            new("watch-b"),
            principal.Tenant,
            principal.Owner,
            new(new("parking-vehicles"), new("notify"), new("b")),
            SubscriptionState.Active,
            new("reaction-b"),
            DateTimeOffset.UnixEpoch,
            new Unbounded()), CancellationToken.None);
        await PostAsync(repository, """
            {"jsonrpc":"2.0","id":"10","method":"CancelTask","params":{"id":"watch-a"}}
            """);
        var body = await PostAsync(repository, """
            {"jsonrpc":"2.0","id":"11","method":"ListTasks","params":{}}
            """);
        using var document = JsonDocument.Parse(body);
        var tasks = document.RootElement.GetProperty("result").GetProperty("tasks").EnumerateArray().ToArray();
        Assert.Equal("TASK_STATE_CANCELED", tasks.Single(task => task.GetProperty("id").GetString() == "watch-a").GetProperty("status").GetProperty("state").GetString());
        Assert.Equal("TASK_STATE_WORKING", tasks.Single(task => task.GetProperty("id").GetString() == "watch-b").GetProperty("status").GetProperty("state").GetString());
    }

    private static SubscriptionId GuidSubscription(string value) => new(Guid.Parse(value));

    [Fact]
    public void Subscribe_to_task_accepts_inspector_method()
    {
        using var document = JsonDocument.Parse("""{"jsonrpc":"2.0","id":1,"method":"SubscribeToTask","params":{"taskId":"task-1"}}""");
        var request = A2aMessageRequest.Parse(document.RootElement);
        Assert.Equal(A2aStreamingMethod.SubscribeToTask, request.Method);
        Assert.Equal("task-1", request.Message.TaskId);
    }

    [Fact]
    public void Agent_card_advertises_sse_streaming_json_rpc()
    {
        using var card = JsonDocument.Parse(JsonSerializer.Serialize(AgentCard.Create(ServiceProfile.Demo, new A2aAnonymousManifest(), new Uri("http://localhost:5000/"), EmptyCatalog())));
        Assert.True(card.RootElement.GetProperty("capabilities").GetProperty("streaming").GetBoolean());
        Assert.Equal("JSONRPC", card.RootElement.GetProperty("supportedInterfaces")[0].GetProperty("protocolBinding").GetString());
    }

    [Fact]
    public void Agent_card_declares_a_single_standard_json_rpc_interface()
    {
        using var card = JsonDocument.Parse(JsonSerializer.Serialize(AgentCard.Create(ServiceProfile.Demo, new A2aAnonymousManifest(), new Uri("http://localhost:5000/"), EmptyCatalog())));
        Assert.Single(card.RootElement.GetProperty("supportedInterfaces").EnumerateArray());
    }

    private static SafeCatalog EmptyCatalog() => new(
        new Dictionary<QueryId, QueryCatalogItem>(),
        new Dictionary<ReactionId, ReactionCatalogItem>());

    private static async Task<string> PostAsync(IConversationRepository repository, string body)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        var result = await A2aEndpoint.SendAsync(
            context,
            repository,
            new ConversationSubscriptions(EmptyCatalog(), new FakeNativeEngine(), repository, new TaskEventNotifier(), TimeProvider.System),
            new TaskEventNotifier(),
            new RecordingResponder([]),
            new A2aAnonymousManifest());
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        return await reader.ReadToEndAsync();
    }

    private static async Task SendAsync(IConversationRepository repository, IAgentResponder responder, string body)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        var result = await A2aEndpoint.SendAsync(
            context,
            repository,
            new ConversationSubscriptions(EmptyCatalog(), new FakeNativeEngine(), repository, new TaskEventNotifier(), TimeProvider.System),
            new TaskEventNotifier(),
            responder,
            new A2aAnonymousManifest());
        await result.ExecuteAsync(context);
    }

    private sealed class RecordingResponder(List<string> seen) : IAgentResponder
    {
        public Task<string> RespondAsync(
            Principal principal,
            TaskTurnId turn,
            string input,
            IReadOnlyList<AgentMessage> priorTurns,
            CancellationToken cancellationToken)
        {
            seen.Add(turn.TaskId);
            return Task.FromResult("ok");
        }
    }

    [Fact]
    public void Owner_derivation_remains_stable()
    {
        var claims = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "alice"), new Claim("tenant", "contoso")], "Bearer"));
        Assert.Equal(A2aPrincipalFactory.CreateFromJwt(claims).Owner, A2aPrincipalFactory.CreateFromJwt(claims).Owner);
    }
}
