using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Drasi.Sentinel.Application;
using Drasi.Sentinel.Domain;
using Drasi.Sentinel.Host;
using Microsoft.AspNetCore.Http;

namespace Drasi.Sentinel.Tests;

public sealed class ModelTurnHistoryTests
{
    [Fact]
    public async Task History_skips_observations_and_reads_past_a_full_page()
    {
        var repository = new InMemorySentinelRepository();
        var principal = Principal();
        var taskId = new ConversationTaskId("task");
        await repository.GetOrCreateTaskAsync(principal, taskId, CancellationToken.None);
        await Append(repository, principal, taskId, ConversationEventKind.AssistantArtifact, "Monitoring is set up.");
        await Append(repository, principal, taskId, ConversationEventKind.TaskWorking, "Task is working.");
        await Append(repository, principal, taskId, ConversationEventKind.Observation, "observation-0");
        await Append(repository, principal, taskId, ConversationEventKind.Observation, "observation-1");
        await Append(repository, principal, taskId, ConversationEventKind.AssistantArtifact, "I can check current results.");

        var history = await ConversationTurnHistory.LoadAssistantTextsAsync(
            repository, principal.Owner, taskId, CancellationToken.None);

        Assert.Equal(["Monitoring is set up.", "I can check current results."], history);
    }

    [Fact]
    public async Task History_stops_at_the_sequence_captured_before_the_read()
    {
        var calls = 0;
        var history = await ConversationTurnHistory.LoadAssistantTextsAsync(
            (after, _, _) =>
            {
                calls++;
                IReadOnlyList<ConversationEvent> page = after.Value switch
                {
                    0 => [Event(1, ConversationEventKind.Observation, "old"), Event(2, ConversationEventKind.Observation, "old")],
                    _ => [Event(after.Value + 1, ConversationEventKind.AssistantArtifact, "later"), Event(after.Value + 2, ConversationEventKind.Observation, "live")],
                };
                return Task.FromResult(page);
            },
            CancellationToken.None,
            pageSize: 2,
            inclusiveUpperBound: new(3));

        Assert.Equal(2, calls);
        Assert.Equal(["later"], history);
    }

    [Fact]
    public void Turn_messages_keep_prior_assistant_text_and_the_current_user_text()
    {
        var messages = AgentMessages.Create(["I can check current results."], "yes");

        Assert.Equal(2, messages.Count);
        Assert.Equal(AgentRole.Assistant, messages[0].Role);
        Assert.Equal("I can check current results.", messages[0].Text);
        Assert.Equal(AgentRole.User, messages[1].Role);
        Assert.Equal("yes", messages[1].Text);
    }

    [Fact]
    public async Task Runner_passes_assistant_history_and_the_current_user_text_to_the_model()
    {
        IReadOnlyList<AgentMessage>? seen = null;
        var responder = new CatalogAgentResponder(Catalog(), UnusedSubscriptions(), new RecordingModel(messages =>
        {
            seen = messages;
            return "reported-rows";
        }));

        var response = await responder.RespondAsync(
            Principal(),
            new("task", "message"),
            "yes",
            ["I can check current results."],
            CancellationToken.None);

        Assert.Equal("reported-rows", response);
        Assert.NotNull(seen);
        Assert.Equal(["I can check current results.", "yes"], seen.Select(message => message.Text));
        Assert.Equal([AgentRole.Assistant, AgentRole.User], seen.Select(message => message.Role));
    }

    [Fact]
    public void Instructions_are_the_only_copy_of_the_turn_procedure()
    {
        var text = AgentTurn.Render(Catalog());
        foreach (var phrase in new[]
        {
            "list, show, or return the current rows",
            "not a watch-only request",
            "get_current_task_results",
            "subscribe_to_changes",
            "same turn",
            "already has a subscription",
            "Do not call subscribe_to_changes again",
            "short affirmative",
            "check current results",
            "tool_rejected",
            "follow Next",
            "The host binds the reaction",
        })
        {
            Assert.Contains(phrase, text);
        }

        Assert.Contains("The host binds the reaction", AgentTurn.SubscribeToolDescription);
        Assert.DoesNotContain("same turn", AgentTurn.SubscribeToolDescription, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("same turn", AgentTurn.CurrentResultsToolDescription, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("invoice", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("paid", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("overdue", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("acct-100", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Governed_tools_publish_the_current_results_contract()
    {
        var tools = AgentToolSession.Create(UnusedSubscriptions(), Principal(), new("task", "message"), CancellationToken.None);

        Assert.Equal(
            AgentTurn.SubscribeToolDescription,
            tools.Tools.Single(tool => tool.Name == "subscribe_to_changes").Description);
        Assert.Equal(
            AgentTurn.CurrentResultsToolDescription,
            tools.Tools.Single(tool => tool.Name == "get_current_task_results").Description);
    }

    [Fact]
    public async Task Missing_subscription_can_subscribe_and_fetch_results_without_failing_the_task()
    {
        var (repository, service, principal) = await StartedServiceAsync();
        var tools = Tools(service, principal, "message-1");

        var missing = await InvokeAsync(tools, "get_current_task_results");
        Assert.Contains("subscription_not_found", missing);
        Assert.Contains(AgentTurn.ToolRejectedKind, missing);
        Assert.Contains("get_current_task_results", missing);
        Assert.Contains("Next", missing);

        var created = await InvokeAsync(tools, "subscribe_to_changes", Arguments("query"));
        Assert.Contains("subscription_created", created);
        Assert.DoesNotContain("tenant", created, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reaction-", created, StringComparison.Ordinal);

        var rows = await InvokeAsync(tools, "get_current_task_results");
        Assert.Contains("current_results", rows);
        Assert.Contains("row-1", rows);
        Assert.Single(await repository.ListSubscriptionsAsync(principal.Owner, new("task"), CancellationToken.None));
        Assert.DoesNotContain(
            await repository.ReadEventsAsync(principal.Owner, new("task"), new(0), 20, CancellationToken.None),
            item => item.Kind == ConversationEventKind.TaskFailed);
    }

    [Fact]
    public async Task Existing_subscription_is_not_replaced_when_subscribe_is_called_again()
    {
        var (repository, service, principal) = await StartedServiceAsync();
        var created = await service.InvokeAsync(
            principal,
            new("task", "message-1"),
            new SubscribeToQuery(new("query"), new("first"), new Unbounded()),
            CancellationToken.None);
        var tools = Tools(service, principal, "message-2");

        var rejected = await InvokeAsync(tools, "subscribe_to_changes", Arguments("query"));
        Assert.Contains("task_already_has_subscription", rejected);
        Assert.Contains(AgentTurn.ToolRejectedKind, rejected);
        Assert.Contains("Do not call subscribe_to_changes again", rejected);

        var rows = await InvokeAsync(tools, "get_current_task_results");
        Assert.Contains("current_results", rows);
        var subscriptions = await repository.ListSubscriptionsAsync(principal.Owner, new("task"), CancellationToken.None);
        var subscription = Assert.Single(subscriptions);
        Assert.Equal(created.Subscription!.Id, subscription.Id);
        Assert.DoesNotContain(
            await repository.ReadEventsAsync(principal.Owner, new("task"), new(0), 20, CancellationToken.None),
            item => item.Kind == ConversationEventKind.TaskFailed);
    }

    [Fact]
    public async Task Unknown_query_returns_a_tool_result_and_does_not_fail_the_task()
    {
        var (repository, service, principal) = await StartedServiceAsync();
        var tools = Tools(service, principal, "message-1");

        var rejected = await InvokeAsync(tools, "subscribe_to_changes", Arguments("unknown"));

        Assert.Contains("query_unknown", rejected);
        Assert.Contains(AgentTurn.ToolRejectedKind, rejected);
        await repository.GetOrCreateTaskAsync(principal, new("task"), CancellationToken.None);
        Assert.DoesNotContain(
            await repository.ReadEventsAsync(principal.Owner, new("task"), new(0), 20, CancellationToken.None),
            item => item.Kind == ConversationEventKind.TaskFailed);
    }

    [Fact]
    public async Task Invalid_cancel_id_returns_a_tool_result()
    {
        var tools = Tools(UnusedSubscriptions(), Principal(), "message-1");

        var rejected = await InvokeAsync(tools, "cancel_task_subscription", Arguments("not-a-guid", "id"));

        Assert.Contains("invalid_subscription_id", rejected);
        Assert.Contains(AgentTurn.ToolRejectedKind, rejected);
    }

    [Fact]
    public async Task Missing_tool_argument_returns_a_tool_result()
    {
        var tools = Tools(UnusedSubscriptions(), Principal(), "message-1");

        var rejected = await InvokeAsync(tools, "subscribe_to_changes");

        Assert.Contains("invalid_tool_arguments", rejected);
        Assert.Contains(AgentTurn.ToolRejectedKind, rejected);
        Assert.Contains("Pass the required tool argument", rejected);
    }

    [Fact]
    public async Task Tool_call_observes_the_turn_token()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var tools = AgentToolSession.Create(UnusedSubscriptions(), Principal(), new("task", "message"), source.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tools.InvokeAsync("get_current_task_results"));
    }

    [Theory]
    [InlineData("message/send")]
    [InlineData("message/stream")]
    public async Task Both_delivery_paths_pass_prior_assistant_text_and_do_not_store_the_user_text(string method)
    {
        var repository = new InMemorySentinelRepository();
        var principal = A2aPrincipalFactory.Create(new ClaimsPrincipal(), new A2aAnonymousManifest());
        var taskId = new ConversationTaskId("task");
        await repository.GetOrCreateTaskAsync(principal, taskId, CancellationToken.None);
        for (var i = 0; i < ConversationTurnHistory.PageSize; i++)
            await Append(repository, principal, taskId, ConversationEventKind.Observation, $"observation-{i}");
        await Append(repository, principal, taskId, ConversationEventKind.AssistantArtifact, "I can check current results.");
        var responder = new RecordingResponder();

        await A2aEndpoint.SendAsync(
            Request(method, "yes"),
            repository,
            UnusedSubscriptions(),
            new TaskEventNotifier(),
            responder,
            new A2aAnonymousManifest());
        if (method == "message/stream")
        {
            var completed = await Task.WhenAny(responder.Completed.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(responder.Completed.Task, completed);
            await WaitForArtifactAsync(repository, principal, taskId);
        }

        Assert.Equal("yes", responder.Input);
        Assert.Equal(["I can check current results."], responder.PriorAssistantTexts);
        var events = await repository.ReadEventsAsync(principal.Owner, taskId, new(0), 200, CancellationToken.None);
        Assert.DoesNotContain(events, item => item.Content.Contains("yes", StringComparison.Ordinal));
        Assert.Contains(events, item => item.Kind == ConversationEventKind.AssistantArtifact && item.Content == "reported-rows");
    }

    private static async Task WaitForArtifactAsync(
        IConversationRepository repository,
        Principal principal,
        ConversationTaskId taskId)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var events = await repository.ReadEventsAsync(principal.Owner, taskId, new(0), 200, CancellationToken.None);
            if (events.Any(item => item.Kind == ConversationEventKind.AssistantArtifact && item.Content == "reported-rows"))
                return;
            await Task.Delay(20);
        }

        throw new TimeoutException("The streaming turn did not store the assistant reply.");
    }

    private static ConversationEvent Event(long sequence, ConversationEventKind kind, string content) =>
        new(new("task"), new(sequence), kind, null, content, DateTimeOffset.UtcNow);

    private static async Task Append(
        IConversationRepository repository,
        Principal principal,
        ConversationTaskId taskId,
        ConversationEventKind kind,
        string content) =>
        await repository.AppendEventAsync(
            principal.Owner,
            new(taskId, default, kind, null, content, DateTimeOffset.UtcNow),
            CancellationToken.None);

    private static AgentToolSession Tools(IConversationSubscriptions service, Principal principal, string messageId) =>
        AgentToolSession.Create(service, principal, new("task", messageId), CancellationToken.None);

    private static Task<string> InvokeAsync(
        AgentToolSession tools,
        string name,
        IReadOnlyDictionary<string, string?>? arguments = null) =>
        tools.InvokeAsync(name, arguments);

    private static IReadOnlyDictionary<string, string?> Arguments(string value, string name = "query") =>
        new Dictionary<string, string?> { [name] = value };

    private static async Task<(InMemorySentinelRepository Repository, ConversationSubscriptions Service, Principal Principal)> StartedServiceAsync()
    {
        var repository = new InMemorySentinelRepository();
        var engine = new FakeNativeEngine();
        await engine.StartAsync(CancellationToken.None);
        engine.SetQueryResults(new("query"), new JsonObject { ["id"] = "row-1" });
        var service = new ConversationSubscriptions(Catalog(), engine, repository, new TaskEventNotifier(), TimeProvider.System);
        return (repository, service, Principal());
    }

    private static IConversationSubscriptions UnusedSubscriptions() =>
        new ConversationSubscriptions(
            Catalog(),
            new FakeNativeEngine(),
            new InMemorySentinelRepository(),
            new TaskEventNotifier(),
            TimeProvider.System);

    private static HttpContext Request(string method, string text)
    {
        var body =
            "{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"method\":\"" + method +
            "\",\"params\":{\"message\":{\"role\":\"user\",\"taskId\":\"task\",\"messageId\":\"message-2\",\"parts\":[{\"kind\":\"text\",\"text\":\"" + text +
            "\",\"mediaType\":\"text/plain\"}]}}}";
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static Principal Principal() => new(new("tenant"), new("owner"));

    private static SafeCatalog Catalog() => new(
        new Dictionary<QueryId, QueryCatalogItem>
        {
            [new("query")] = new(new("query"), "Reports configured changes.", new HashSet<ReactionId> { new("reaction") }, ["Tell me when a row changes."]),
        },
        new Dictionary<ReactionId, ReactionCatalogItem> { [new("reaction")] = new(new("reaction")) });

    private sealed class RecordingModel(Func<IReadOnlyList<AgentMessage>, string> complete) : ILanguageModel
    {
        public Task<string> CompleteAsync(
            string instructions,
            IAgentTools tools,
            IReadOnlyList<AgentMessage> messages,
            CancellationToken cancellationToken) =>
            Task.FromResult(complete(messages));
    }

    private sealed class RecordingResponder : IAgentResponder
    {
        public string? Input { get; private set; }
        public IReadOnlyList<string>? PriorAssistantTexts { get; private set; }
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string> RespondAsync(
            Principal principal,
            TaskTurnId turn,
            string input,
            IReadOnlyList<string> priorAssistantTexts,
            CancellationToken cancellationToken)
        {
            Input = input;
            PriorAssistantTexts = priorAssistantTexts;
            Completed.TrySetResult();
            return Task.FromResult("reported-rows");
        }
    }
}
