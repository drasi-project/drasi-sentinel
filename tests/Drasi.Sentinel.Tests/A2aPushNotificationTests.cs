using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Drasi.Sentinel.Application;
using Drasi.Sentinel.Domain;
using Drasi.Sentinel.Host;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Drasi.Sentinel.Tests;

public sealed class A2aPushNotificationTests
{
    [Fact]
    public async Task Set_requires_an_existing_task_and_get_hides_the_token()
    {
        var repository = new InMemorySentinelRepository();
        using var missing = await PostAsync(repository, """
            {"jsonrpc":"2.0","id":"1","method":"tasks/pushNotificationConfig/set","params":{"taskId":"task","url":"http://127.0.0.1:8082/"}}
            """);
        Assert.Equal("TaskNotFoundError", missing.RootElement.GetProperty("error").GetProperty("data").GetProperty("code").GetString());
        Assert.False(await repository.TaskExistsAsync(Principal().Owner, new("task"), CancellationToken.None));

        await repository.GetOrCreateTaskAsync(Principal(), new("task"), CancellationToken.None);
        using var created = await PostAsync(repository, """
            {"jsonrpc":"2.0","id":"1","method":"tasks/pushNotificationConfig/set","params":{"taskId":"task","id":"cfg","url":"http://127.0.0.1:8082/","token":"secret","authentication":{"scheme":"Bearer","credentials":"cred"}}}
            """);
        var result = created.RootElement.GetProperty("result");
        Assert.Equal("cfg", result.GetProperty("id").GetString());
        Assert.Equal("http://127.0.0.1:8082/", result.GetProperty("url").GetString());
        Assert.False(result.TryGetProperty("token", out _));

        using var listed = await PostAsync(repository, """
            {"jsonrpc":"2.0","id":"1","method":"tasks/pushNotificationConfig/list","params":{"taskId":"task"}}
            """);
        Assert.Equal("cfg", listed.RootElement.GetProperty("result").GetProperty("configs")[0].GetProperty("id").GetString());

        using var fetched = await PostAsync(repository, """
            {"jsonrpc":"2.0","id":"1","method":"tasks/pushNotificationConfig/get","params":{"taskId":"task","id":"cfg"}}
            """);
        Assert.False(fetched.RootElement.GetProperty("result").TryGetProperty("token", out _));
        var stored = await repository.GetPushConfigAsync(Principal().Owner, new("task"), CancellationToken.None);
        Assert.Equal("secret", stored!.Token);
        Assert.Equal("Bearer", stored.AuthScheme);

        using var removed = await PostAsync(repository, """
            {"jsonrpc":"2.0","id":"1","method":"tasks/pushNotificationConfig/delete","params":{"taskId":"task","id":"cfg"}}
            """);
        Assert.True(removed.RootElement.TryGetProperty("result", out _));
        Assert.Null(await repository.GetPushConfigAsync(Principal().Owner, new("task"), CancellationToken.None));
    }

    [Fact]
    public async Task Invalid_push_url_is_rejected()
    {
        var repository = new InMemorySentinelRepository();
        await repository.GetOrCreateTaskAsync(Principal(), new("task"), CancellationToken.None);
        using var rejected = await PostAsync(repository, """
            {"jsonrpc":"2.0","id":"1","method":"tasks/pushNotificationConfig/set","params":{"taskId":"task","url":"http://user:pass@127.0.0.1:8082/"}}
            """);
        Assert.Equal("invalid_params", rejected.RootElement.GetProperty("error").GetProperty("data").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Opening_message_can_register_the_task_push_url()
    {
        var repository = new InMemorySentinelRepository();
        var context = Request("""
            {"jsonrpc":"2.0","id":"1","method":"message/stream","params":{"message":{"role":"user","taskId":"task","messageId":"message","parts":[{"kind":"text","text":"watch","mediaType":"text/plain"}]},"configuration":{"taskPushNotificationConfig":{"url":"http://127.0.0.1:8082/","id":"from-send"}}}}
            """);
        await A2aEndpoint.SendAsync(
            context,
            repository,
            Subscriptions(),
            new TaskEventNotifier(),
            new QuietResponder(),
            new A2aAnonymousManifest());

        var config = await repository.GetPushConfigAsync(Principal().Owner, new("task"), CancellationToken.None);
        Assert.Equal("from-send", config!.Id);
        Assert.Equal("http://127.0.0.1:8082/", config.Url.ToString());
    }

    [Fact]
    public async Task Registered_listener_receives_only_its_task_artifact_update()
    {
        using var first = CaptureListener.Start();
        using var second = CaptureListener.Start();
        var repository = new InMemorySentinelRepository();
        var principal = Principal();
        await RegisterAsync(repository, "task-a", first.Url, "push-token", "push-credential");
        await RegisterAsync(repository, "task-b", second.Url, token: null, credentials: null);
        var left = await WatchAsync(repository, principal, "task-a", "native-a");
        var right = await WatchAsync(repository, principal, "task-b", "native-b");
        using var sender = new HttpPushNotification();
        var ingestion = new NativeResultIngestion(repository, new TaskEventNotifier(), TimeProvider.System, sender);

        await ingestion.IngestAsync(left, Diff("Delete"), CancellationToken.None);
        await ingestion.IngestAsync(right, Diff("Add"), CancellationToken.None);

        var postedLeft = await first.ReceiveAsync();
        var postedRight = await second.ReceiveAsync();
        Assert.Equal("push-token", postedLeft.Token);
        Assert.Equal("Bearer push-credential", postedLeft.Authorization);
        Assert.StartsWith("application/json", postedLeft.ContentType, StringComparison.OrdinalIgnoreCase);
        Assert.Null(postedRight.Token);
        Assert.Null(postedRight.Authorization);
        using var leftBody = JsonDocument.Parse(postedLeft.Body);
        using var rightBody = JsonDocument.Parse(postedRight.Body);
        Assert.Equal("Delete", DiffType(leftBody, "task-a"));
        Assert.Equal("Add", DiffType(rightBody, "task-b"));
        await first.AssertNoFurtherPostAsync();
        await second.AssertNoFurtherPostAsync();
    }

    [Fact]
    public async Task Failed_push_post_leaves_the_journaled_diff()
    {
        using var listener = CaptureListener.Start(statusCode: 500);
        var repository = new InMemorySentinelRepository();
        var principal = Principal();
        await RegisterAsync(repository, "task", listener.Url, "push-token", "push-credential");
        var nativeId = await WatchAsync(repository, principal, "task", "native-failed");
        using var sender = new HttpPushNotification();
        var ingestion = new NativeResultIngestion(repository, new TaskEventNotifier(), TimeProvider.System, sender);

        await ingestion.IngestAsync(nativeId, Diff("Update"), CancellationToken.None);

        var posted = await listener.ReceiveAsync();
        Assert.Equal("push-token", posted.Token);
        Assert.Contains("native_query_diff", posted.Body, StringComparison.Ordinal);
        Assert.Single(
            await repository.ReadEventsAsync(principal.Owner, new("task"), new(0), 10, CancellationToken.None),
            item => item.Kind == ConversationEventKind.Observation);
    }

    [Fact]
    public void Artifact_update_carries_the_journaled_diff()
    {
        var json = HttpPushNotification.ArtifactUpdateJson(new(
            new("task"),
            new(4),
            ConversationEventKind.Observation,
            null,
            """{"type":"native_query_diff","diffType":"Delete"}""",
            DateTimeOffset.UtcNow));

        using var document = JsonDocument.Parse(json);
        var update = document.RootElement.GetProperty("artifactUpdate");
        Assert.Equal("task", update.GetProperty("taskId").GetString());
        Assert.Equal("4", update.GetProperty("artifact").GetProperty("artifactId").GetString());
        Assert.Equal("Delete", update.GetProperty("artifact").GetProperty("parts")[0].GetProperty("data").GetProperty("diffType").GetString());
    }

    private static async Task RegisterAsync(
        IConversationRepository repository,
        string taskId,
        string url,
        string? token,
        string? credentials)
    {
        await repository.GetOrCreateTaskAsync(Principal(), new(taskId), CancellationToken.None);
        var parameters = new JsonObject
        {
            ["taskId"] = taskId,
            ["url"] = url,
        };
        if (token is not null)
            parameters["token"] = token;
        if (credentials is not null)
            parameters["authentication"] = new JsonObject { ["scheme"] = "Bearer", ["credentials"] = credentials };
        using var created = await PostAsync(repository, JsonSerializer.Serialize(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "1",
            ["method"] = "tasks/pushNotificationConfig/set",
            ["params"] = parameters,
        }));
        Assert.Equal(url, created.RootElement.GetProperty("result").GetProperty("url").GetString());
    }

    private static async Task<NativeSubscriptionId> WatchAsync(
        IConversationRepository repository,
        Principal principal,
        string taskId,
        string nativeId)
    {
        var subscription = await repository.GetOrCreateSubscriptionAsync(
            new(
                new(Guid.NewGuid()),
                new(taskId),
                principal.Tenant,
                principal.Owner,
                new(new("query"), new("reaction"), new(nativeId)),
                SubscriptionState.Active,
                new(nativeId),
                DateTimeOffset.UtcNow,
                new Unbounded()),
            CancellationToken.None);
        return subscription.NativeSubscriptionId;
    }

    private static NativeQueryDiff Diff(string type) => new(
        type,
        new JsonObject { ["entity_id"] = "entity-1" },
        null,
        null,
        null,
        null);

    private static string DiffType(JsonDocument body, string taskId)
    {
        var update = body.RootElement.GetProperty("artifactUpdate");
        Assert.Equal(taskId, update.GetProperty("taskId").GetString());
        Assert.False(update.GetProperty("append").GetBoolean());
        Assert.True(update.GetProperty("lastChunk").GetBoolean());
        var data = update.GetProperty("artifact").GetProperty("parts")[0].GetProperty("data");
        Assert.Equal("native_query_diff", data.GetProperty("type").GetString());
        return data.GetProperty("diffType").GetString()!;
    }

    private static async Task<JsonDocument> PostAsync(IConversationRepository repository, string body)
    {
        var context = Request(body);
        var result = await A2aEndpoint.SendAsync(
            context,
            repository,
            Subscriptions(),
            new TaskEventNotifier(),
            new QuietResponder(),
            new A2aAnonymousManifest());
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        return await JsonDocument.ParseAsync(context.Response.Body);
    }

    private static HttpContext Request(string body)
    {
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static Principal Principal() =>
        A2aPrincipalFactory.Create(new ClaimsPrincipal(), new A2aAnonymousManifest());

    private static IConversationSubscriptions Subscriptions() =>
        new ConversationSubscriptions(
            new SafeCatalog(new Dictionary<QueryId, QueryCatalogItem>(), new Dictionary<ReactionId, ReactionCatalogItem>()),
            new FakeNativeEngine(),
            new InMemorySentinelRepository(),
            new TaskEventNotifier(),
            TimeProvider.System);

    private sealed class CaptureListener : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly TimeSpan _timeout = TimeSpan.FromSeconds(5);

        private CaptureListener(int statusCode)
        {
            StatusCode = statusCode;
            var port = FreePort();
            Url = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
        }

        public string Url { get; }
        public int StatusCode { get; }

        public static CaptureListener Start(int statusCode = 204) => new(statusCode);

        public async Task<CapturedPush> ReceiveAsync()
        {
            var received = _listener.GetContextAsync();
            var completed = await Task.WhenAny(received, Task.Delay(_timeout));
            if (!ReferenceEquals(completed, received))
                throw new TimeoutException($"No push arrived at {Url}.");
            var context = await received;
            var length = checked((int)context.Request.ContentLength64);
            var buffer = new byte[length];
            await context.Request.InputStream.ReadExactlyAsync(buffer);
            var captured = new CapturedPush(
                context.Request.Headers["X-A2A-Notification-Token"],
                context.Request.Headers["Authorization"],
                context.Request.ContentType,
                Encoding.UTF8.GetString(buffer));
            context.Response.StatusCode = StatusCode;
            context.Response.Close();
            return captured;
        }

        public async Task AssertNoFurtherPostAsync()
        {
            var extra = _listener.GetContextAsync();
            var completed = await Task.WhenAny(extra, Task.Delay(TimeSpan.FromMilliseconds(200)));
            Assert.NotSame(extra, completed);
            _ = extra.ContinueWith(
                task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        public void Dispose()
        {
            _listener.Close();
        }

        private static int FreePort()
        {
            var tcp = new TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            tcp.Stop();
            return port;
        }
    }

    private sealed record CapturedPush(string? Token, string? Authorization, string? ContentType, string Body);

    private sealed class QuietResponder : IAgentResponder
    {
        public Task<string> RespondAsync(
            Principal principal,
            TaskTurnId turn,
            string input,
            IReadOnlyList<AgentMessage> priorTurns,
            CancellationToken cancellationToken) =>
            Task.FromResult("ok");
    }
}
