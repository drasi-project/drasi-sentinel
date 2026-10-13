using System.Security.Claims;
using System.Text;
using System.Text.Json;
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

    private sealed class QuietResponder : IAgentResponder
    {
        public Task<string> RespondAsync(
            Principal principal,
            TaskTurnId turn,
            string input,
            IReadOnlyList<string> priorAssistantTexts,
            CancellationToken cancellationToken) =>
            Task.FromResult("ok");
    }
}
