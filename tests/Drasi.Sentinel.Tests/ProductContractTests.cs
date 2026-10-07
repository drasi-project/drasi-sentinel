using System.Text.Json;
using Drasi.Sentinel.Agent.Maf;
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
        var names = MafGovernedToolSet.Names;

        Assert.Contains("subscribe_to_changes", names);
        Assert.Contains("get_current_task_results", names);
        Assert.DoesNotContain(names, name =>
            name.Contains("filter", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("confirm", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("destination", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(typeof(GetCurrentTaskResults).GetProperties());
    }

    [Fact]
    public void Agent_card_advertises_only_standard_streaming_a2a()
    {
        using var card = JsonDocument.Parse(JsonSerializer.Serialize(
            AgentCard.Create(
                ServiceProfile.Demo,
                new A2aAnonymousManifest(),
                new Uri("http://127.0.0.1:8080/"))));

        var root = card.RootElement;
        Assert.True(root.GetProperty("capabilities").GetProperty("streaming").GetBoolean());
        Assert.False(root.GetProperty("capabilities").GetProperty("pushNotifications").GetBoolean());
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
