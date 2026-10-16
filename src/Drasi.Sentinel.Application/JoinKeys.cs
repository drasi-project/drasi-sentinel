using System.Text.RegularExpressions;
using Drasi.Sentinel.Domain;

namespace Drasi.Sentinel.Application;

public readonly record struct JoinFollowUp(
    string? Key,
    IReadOnlyList<string> Holders,
    IReadOnlyList<string> Choices,
    string? Instruction,
    string? Question);

public static class JoinKeys
{
    public static JoinFollowUp FollowUp(
        string? callerText,
        IReadOnlyList<string> complements,
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyDictionary<string, IReadOnlyList<string>> stored)
    {
        var none = new JoinFollowUp(null, [], [], null, null);
        if (string.IsNullOrWhiteSpace(callerText) || complements.Count == 0 || nodes.Count == 0)
            return none;
        if (!Mapped(callerText, complements, stored))
            return none;

        var mentioned = nodes
            .Select(node => node.Label)
            .Where(label => Mentions(callerText, label))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (mentioned.Length < 2)
            return none;

        var shared = Shared(nodes, mentioned);
        if (shared.Count == 0)
            return none;

        var named = shared.Where(property => Names(callerText, property)).ToArray();
        if (named.Length == 1 || (named.Length == 0 && shared.Count == 1))
        {
            var key = named.Length == 1 ? named[0] : shared[0];
            var holders = Holders(nodes, mentioned, key);
            if (holders.Count < 2)
                return none;
            var instruction =
                $"The join key is {key} between {string.Join(" and ", holders)}. " +
                $"Pass joins as {Json(key, holders)} and MATCH that relationship. " +
                $"Do not filter {key} to a stored value. Do not ask which stored value. Commit and subscribe in this turn.";
            return new JoinFollowUp(
                key,
                holders,
                [],
                instruction,
                $"{key} joins {string.Join(" and ", holders)}. Do not pick one stored value of {key}.");
        }

        var choices = named.Length > 1 ? named : shared;
        return new JoinFollowUp(
            null,
            [],
            choices,
            $"Ask which of these property names joins the labels: {string.Join(", ", choices)}. " +
            "Do not ask for a declared relationship. Do not ask which stored value. Do not commit until the caller names one.",
            $"Which shared property joins them: {string.Join(", ", choices)}?");
    }

    public static string? CommitSkip(
        string? callerText,
        string query,
        IReadOnlyList<QueryJoinSpec> joins,
        IReadOnlyList<string> complements,
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyDictionary<string, IReadOnlyList<string>> stored)
    {
        var follow = FollowUp(callerText, complements, nodes, stored);
        if (follow.Instruction is null)
            return null;
        if (follow.Key is null)
            return follow.Instruction;
        if (!string.IsNullOrWhiteSpace(callerText))
        {
            var filter = Regex.Match(
                query,
                $@"\b{Regex.Escape(follow.Key)}\s*=\s*'([^']*)'",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (filter.Success && !Whole(callerText, filter.Groups[1].Value))
                return $"'{follow.Key}' is the join key. Do not filter it to a stored value. {follow.Instruction}";
        }

        return Uses(query, joins, follow.Key, follow.Holders) ? null : follow.Instruction;
    }

    public static bool Names(string text, string property)
    {
        if (Regex.IsMatch(text, $@"\b{Regex.Escape(property)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return true;
        var tokens = property.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length < 2)
            return false;
        var index = 0;
        foreach (var token in tokens)
        {
            if (token.Length < 2)
                return false;
            var match = Regex.Match(
                text[index..],
                $@"\b{Regex.Escape(token)}\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success)
                return false;
            index += match.Index + match.Length;
        }

        return true;
    }

    private static bool Mapped(
        string callerText,
        IReadOnlyList<string> complements,
        IReadOnlyDictionary<string, IReadOnlyList<string>> stored)
    {
        foreach (var complement in complements)
        {
            if (stored.Values.SelectMany(values => values).Any(value => value.Equals(complement, StringComparison.OrdinalIgnoreCase)))
                continue;
            var chosen = stored.Any(pair =>
                Names(callerText, pair.Key) &&
                pair.Value.Any(value => Whole(callerText, value)));
            if (!chosen)
                return false;
        }

        return true;
    }

    private static IReadOnlyList<string> Shared(IReadOnlyList<GraphNode> nodes, IReadOnlyList<string> mentioned)
    {
        var labels = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            if (!mentioned.Contains(node.Label, StringComparer.OrdinalIgnoreCase))
                continue;
            foreach (var property in node.Properties)
            {
                if (!labels.TryGetValue(property.Name, out var holders))
                    labels[property.Name] = holders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                holders.Add(node.Label);
            }
        }

        return labels
            .Where(pair => pair.Value.Count >= 2)
            .Select(pair => pair.Key)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> Holders(IReadOnlyList<GraphNode> nodes, IReadOnlyList<string> mentioned, string key) =>
        nodes
            .Where(node =>
                mentioned.Contains(node.Label, StringComparer.OrdinalIgnoreCase) &&
                node.Properties.Any(property => property.Name.Equals(key, StringComparison.OrdinalIgnoreCase)))
            .Select(node => node.Label)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(label => label, StringComparer.Ordinal)
            .ToArray();

    private static bool Uses(string query, IReadOnlyList<QueryJoinSpec> joins, string key, IReadOnlyList<string> holders) =>
        holders.All(label => Regex.IsMatch(query, $@"\b{Regex.Escape(label)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) &&
        joins.Any(join => holders.All(label => join.Keys.Any(item =>
            item.Label.Equals(label, StringComparison.OrdinalIgnoreCase) &&
            item.Property.Equals(key, StringComparison.OrdinalIgnoreCase))));

    private static string Json(string key, IReadOnlyList<string> holders)
    {
        var keys = string.Join(",", holders.Select(label => $"{{\"label\":\"{label}\",\"property\":\"{key}\"}}"));
        return $"[{{\"id\":\"LINK\",\"keys\":[{keys}]}}]";
    }

    private static bool Mentions(string text, string label)
    {
        if (Regex.IsMatch(text, $@"\b{Regex.Escape(label)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return true;
        return label.Length > 1 &&
            label.EndsWith('s') &&
            Regex.IsMatch(text, $@"\b{Regex.Escape(label[..^1])}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool Whole(string text, string value) =>
        Regex.IsMatch(text, $@"\b{Regex.Escape(value)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
