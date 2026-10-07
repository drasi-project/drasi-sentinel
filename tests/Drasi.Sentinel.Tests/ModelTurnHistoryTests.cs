using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Drasi.Sentinel.Agent.Maf;
using Drasi.Sentinel.Application;
using Drasi.Sentinel.Domain;
using Drasi.Sentinel.Host;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;

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
        var messages = MafTurnMessages.Create(["I can check current results."], "yes");

        Assert.Equal(2, messages.Count);
        Assert.Equal(ChatRole.Assistant, messages[0].Role);
        Assert.Equal("I can check current results.", messages[0].Text);
        Assert.Equal(ChatRole.User, messages[1].Role);
        Assert.Equal("yes", messages[1].Text);
    }

    [Fact]
    public async Task Runner_passes_assistant_history_and_the_current_user_text_to_the_model()
    {
        var agent = new RecordingAgent();
        var response = await MafTurnRunner.RunAsync(
            agent,
            ["I can check current results."],
            "yes",
            CancellationToken.None);

        Assert.Equal("reported-rows", response);
        Assert.NotNull(agent.Messages);
        Assert.Equal(["I can check current results.", "yes"], agent.Messages.Select(message => message.Text));
        Assert.Equal([ChatRole.Assistant, ChatRole.User], agent.Messages.Select(message => message.Role));
    }

    [Fact]
    public void Instructions_require_current_results_in_the_same_turn()
    {
        var text = SentinelAgentInstructions.Create(AgentCapabilityProjection.Create(Catalog()));
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
            "subscription_not_found",
            "task_already_has_subscription",
            SentinelAgentInstructions.ToolRejectedKind,
            "Do not stop after subscribing",
            "even when it names the same subject as a catalog example",
            "follow Next",
        })
        {
            Assert.Contains(phrase, text);
        }

        Assert.Contains("get_current_task_results", SentinelAgentInstructions.SubscribeToolDescription);
        Assert.Contains("subscription_not_found", SentinelAgentInstructions.CurrentResultsToolDescription);
        Assert.Contains("task_already_has_subscription", SentinelAgentInstructions.SubscribeToolDescription);
        Assert.DoesNotContain("invoice", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("paid", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("overdue", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("acct-100", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Governed_tools_publish_the_current_results_contract()
    {
        var tools = new MafGovernedToolFactory(UnusedSubscriptions()).Create(
            Principal(), new("task", "message"), AgentCapabilityProjection.Create(Catalog()));

        Assert.Equal(
            SentinelAgentInstructions.SubscribeToolDescription,
            tools.Functions.Single(tool => tool.Name == "subscribe_to_changes").Description);
        Assert.Equal(
            SentinelAgentInstructions.CurrentResultsToolDescription,
            tools.Functions.Single(tool => tool.Name == "get_current_task_results").Description);
    }

    [Fact]
    public async Task Missing_subscription_can_subscribe_and_fetch_results_without_failing_the_task()
    {
        var (repository, service, principal) = await StartedServiceAsync();
        var tools = Tools(service, principal, "message-1");

        var missing = await InvokeAsync(tools, "get_current_task_results");
        Assert.Contains("subscription_not_found", missing);
        Assert.Contains(SentinelAgentInstructions.ToolRejectedKind, missing);
        Assert.Contains("get_current_task_results", missing);
        Assert.Contains("Next", missing);

        var created = await InvokeAsync(tools, "subscribe_to_changes", Arguments("query", "reaction"));
        Assert.Contains("subscription_created", created);

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
            new SubscribeToChanges(new(new("query"), new("reaction"), new("first"))),
            CancellationToken.None);
        var tools = Tools(service, principal, "message-2");

        var rejected = await InvokeAsync(tools, "subscribe_to_changes", Arguments("query", "reaction"));
        Assert.Contains("task_already_has_subscription", rejected);
        Assert.Contains(SentinelAgentInstructions.ToolRejectedKind, rejected);
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
    public async Task Unapproved_catalog_selection_still_escapes_the_tool()
    {
        var (_, service, principal) = await StartedServiceAsync();
        var tools = Tools(service, principal, "message-1");

        var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            InvokeAsync(tools, "subscribe_to_changes", Arguments("unknown", "reaction")));

        Assert.Equal("catalog_rejected", RequireSentinel(exception).Code);
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

    [Fact]
    public async Task Responder_forwards_prior_assistant_text_to_the_inner_agent()
    {
        var inner = new RecordingInnerAgent();
        var responder = new MafA2aAgentResponder(
            new AgentManifest(new Uri("https://example.invalid"), "model"),
            Catalog(),
            inner);

        await responder.RespondAsync(
            Principal(),
            new("task", "message"),
            "yes",
            ["I can check current results."],
            CancellationToken.None);

        Assert.Equal("yes", inner.Input);
        Assert.Equal(["I can check current results."], inner.PriorAssistantTexts);
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

    private static MafGovernedToolSet Tools(IConversationSubscriptions service, Principal principal, string messageId) =>
        new MafGovernedToolFactory(service).Create(
            principal,
            new("task", messageId),
            AgentCapabilityProjection.Create(Catalog()));

    private static async Task<string> InvokeAsync(
        MafGovernedToolSet tools,
        string name,
        AIFunctionArguments? arguments = null)
    {
        var function = tools.Functions.Single(tool => tool.Name == name);
        var result = await function.InvokeAsync(arguments ?? new AIFunctionArguments(), CancellationToken.None);
        return result?.ToString() ?? "";
    }

    private static AIFunctionArguments Arguments(string query, string reaction)
    {
        var arguments = new AIFunctionArguments { ["query"] = query, ["reaction"] = reaction };
        return arguments;
    }

    private static SentinelException RequireSentinel(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SentinelException sentinel)
                return sentinel;
        }

        throw new InvalidOperationException(exception.ToString(), exception);
    }

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

    private sealed class RecordingResponder : IA2aAgentResponder
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

    private sealed class RecordingInnerAgent : IMafInnerAgent
    {
        public string? Input { get; private set; }
        public IReadOnlyList<string>? PriorAssistantTexts { get; private set; }

        public Task<string> RunAsync(
            MafAgentSettings settings,
            Principal principal,
            TaskTurnId turn,
            AgentCapabilityProjection capabilities,
            IReadOnlyList<string> priorAssistantTexts,
            string input,
            CancellationToken cancellationToken)
        {
            Input = input;
            PriorAssistantTexts = priorAssistantTexts;
            return Task.FromResult("reported-rows");
        }
    }

    private sealed class RecordingAgent : AIAgent
    {
        public IReadOnlyList<ChatMessage>? Messages { get; private set; }

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken)
        {
            Messages = messages.ToArray();
            return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, "reported-rows")));
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            new(new RecordingSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? options,
            CancellationToken cancellationToken) =>
            new(JsonDocument.Parse("{}").RootElement.Clone());

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState,
            JsonSerializerOptions? options,
            CancellationToken cancellationToken) =>
            new(new RecordingSession());

        private sealed class RecordingSession : AgentSession;
    }
}
