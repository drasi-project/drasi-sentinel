using System.Text.Json;
using Drasi.Sentinel.Application;
using Drasi.Sentinel.Domain;
using Drasi.Sentinel.Host;

namespace Drasi.Sentinel.Tests;

public sealed class ProductContractTests
{
    [Fact]
    public void Subscription_has_only_curated_selection_and_request_identity()
    {
        var properties = typeof(SubscriptionRequest)
            .GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name)
            .ToArray();

        Assert.Equal(["QueryId", "ReactionId", "RequestKey"], properties);
        Assert.Equal(["Id"], typeof(ReactionCatalogItem)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray());
    }

    [Fact]
    public void Agent_tools_do_not_expose_filters_confirmations_or_destinations()
    {
        var names = AgentTurn.ToolNames;

        Assert.Contains("subscribe_to_changes", names);
        Assert.Contains("get_current_task_results", names);
        Assert.DoesNotContain(names, name =>
            name.Contains("filter", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("confirm", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("destination", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(typeof(GetCurrentTaskResults).GetProperties());
        var host = Path.Combine(FindRepositoryRoot(), "src", "Drasi.Sentinel.Host");
        var hostSource = string.Join('\n', Directory.EnumerateFiles(host, "*.cs").Select(File.ReadAllText));
        Assert.DoesNotContain("AuthenticationFailedException", hostSource);
        Assert.DoesNotContain("ClientResultException", hostSource);
        Assert.DoesNotContain("AzureCliCredential", hostSource);
        var endpoint = File.ReadAllText(Path.Combine(host, "A2aEndpoint.cs"));
        Assert.DoesNotContain("Drasi.Sentinel.Agent.Maf", endpoint);
        Assert.DoesNotContain("ModelAdapterException", endpoint);
        var application = string.Join(
            '\n',
            Directory.EnumerateFiles(Path.Combine(FindRepositoryRoot(), "src", "Drasi.Sentinel.Application"), "*.cs")
                .Select(File.ReadAllText));
        Assert.DoesNotContain("Microsoft.Agents", application);
        Assert.DoesNotContain("AIFunction", application);
        Assert.DoesNotContain("Azure.", application);
    }

    [Fact]
    public void Agent_card_advertises_only_standard_streaming_a2a()
    {
        using var card = JsonDocument.Parse(JsonSerializer.Serialize(
            AgentCard.Create(
                ServiceProfile.Demo,
                new A2aAnonymousManifest(),
                new Uri("http://127.0.0.1:8080/"),
                EmptyCatalog())));

        var root = card.RootElement;
        Assert.True(root.GetProperty("capabilities").GetProperty("streaming").GetBoolean());
        Assert.True(root.GetProperty("capabilities").GetProperty("pushNotifications").GetBoolean());
        var binding = Assert.Single(root.GetProperty("supportedInterfaces").EnumerateArray());
        Assert.Equal("JSONRPC", binding.GetProperty("protocolBinding").GetString());
        Assert.False(binding.TryGetProperty("extensions", out _));
    }

    [Fact]
    public void Generic_runtime_does_not_contain_example_domain_vocabulary()
    {
        var runtimeDirectory = Path.Combine(FindRepositoryRoot(), "src");
        var forbidden = new[] { "acct-100", "invoice", "paid", "overdue" };
        var violations = Directory.EnumerateFiles(runtimeDirectory, "*.cs", SearchOption.AllDirectories)
            .SelectMany(path => forbidden
                .Where(word => File.ReadAllText(path).Contains(word, StringComparison.OrdinalIgnoreCase))
                .Select(word => $"{Path.GetRelativePath(runtimeDirectory, path)}: {word}"))
            .ToArray();

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Agent_card_skills_are_curated_queries()
    {
        var queryId = new QueryId("open-orders");
        var catalog = new SafeCatalog(
            new Dictionary<QueryId, QueryCatalogItem>
            {
                [queryId] = new(queryId, "Reports open orders.", new HashSet<ReactionId>(), ["Tell me when an order is open."]),
            },
            new Dictionary<ReactionId, ReactionCatalogItem>());

        using var card = JsonDocument.Parse(JsonSerializer.Serialize(
            AgentCard.Create(ServiceProfile.Demo, new A2aAnonymousManifest(), new Uri("http://127.0.0.1:8080/"), catalog)));
        var skills = card.RootElement.GetProperty("skills").EnumerateArray().ToArray();

        Assert.Equal(2, skills.Length);
        Assert.Equal("open-orders", skills[0].GetProperty("id").GetString());
        Assert.Equal("Open Orders", skills[0].GetProperty("name").GetString());
        Assert.Equal("Reports open orders. This is a change watch, not a one-shot answer. Sending an example opens one task. Later changes arrive on that task as JSON artifacts. Follow the card description.", skills[0].GetProperty("description").GetString());
        var description = card.RootElement.GetProperty("description").GetString();
        Assert.Contains("message/stream", description);
        Assert.Contains("tasks/subscribe", description);
        Assert.Contains("tasks/pushNotificationConfig/set", description);
        Assert.Contains("native_query_diff", description);
        Assert.DoesNotContain("message/stream", skills[0].GetProperty("description").GetString());
        Assert.Contains("not a second watch", skills[1].GetProperty("description").GetString());
        Assert.Contains("continuous-query", skills[0].GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()));
        Assert.Equal("Tell me when an order is open.", skills[0].GetProperty("examples")[0].GetString());
        Assert.Equal("current-task-results", skills[1].GetProperty("id").GetString());
        Assert.Contains("task", skills[1].GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()));
        Assert.DoesNotContain(skills, skill => skill.GetProperty("id").GetString() == "governed-change-subscriptions");
    }

    private static SafeCatalog EmptyCatalog() => new(
        new Dictionary<QueryId, QueryCatalogItem>(),
        new Dictionary<ReactionId, ReactionCatalogItem>());

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Drasi.Sentinel.slnx")))
                return directory.FullName;
        }

        throw new InvalidOperationException("Could not locate the Sentinel .NET repository root.");
    }
}
