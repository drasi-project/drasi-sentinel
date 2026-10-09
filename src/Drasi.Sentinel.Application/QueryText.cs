namespace Drasi.Sentinel.Application;

public sealed record GraphProperty(string Name, string? Type);

public sealed record GraphNode(string Label, IReadOnlyList<GraphProperty> Properties, IReadOnlyList<string> Sources);

public sealed record GraphRelation(string Label, string? To, IReadOnlyList<GraphProperty> Properties, IReadOnlyList<string> Sources);

public sealed record SourceGraph(
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphRelation> Relations,
    IReadOnlyList<string> SourcesWithoutSchema);

public sealed record QueryAnalysis(
    IReadOnlyList<string> Blocking,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> ReferencedLabels,
    IReadOnlyList<string> UnknownLabels,
    IReadOnlyList<string> UnknownProperties,
    IReadOnlyDictionary<string, string> Suggestions);

public static partial class QueryText
{
    public static QueryAnalysis Examine(string query, SourceGraph schema, IReadOnlyList<string> joinIds)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(joinIds);
        var blocking = new List<string>();
        var warnings = new List<string>();
        if (SqlSyntax(query) is { } sql)
            blocking.Add(sql);
        if (UnsupportedClause(query) is { } unsupported)
            blocking.Add(unsupported);
        if (MissingJoin(query, joinIds.Count) is { } join)
            warnings.Add(join);

        var knownLabels = schema.Nodes.Select(node => node.Label)
            .Concat(schema.Relations.Select(relation => relation.Label))
            .ToArray();
        var known = new HashSet<string>(knownLabels.Select(label => label.ToLowerInvariant()));
        var declaredJoins = new HashSet<string>(joinIds.Select(id => id.ToLowerInvariant()));
        var referenced = ReferencedLabels(query);
        var unknownLabels = referenced
            .Where(label => !known.Contains(label.ToLowerInvariant()) && !declaredJoins.Contains(label.ToLowerInvariant()))
            .ToArray();
        var suggestions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var unknown in unknownLabels)
        {
            if (Closest(unknown, knownLabels) is { } suggestion)
                suggestions[unknown] = suggestion;
        }

        var knownProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var label in referenced)
        {
            var node = schema.Nodes.FirstOrDefault(item => item.Label.Equals(label, StringComparison.OrdinalIgnoreCase));
            var relation = schema.Relations.FirstOrDefault(item => item.Label.Equals(label, StringComparison.OrdinalIgnoreCase));
            foreach (var property in node?.Properties ?? [])
                knownProperties.Add(property.Name);
            foreach (var property in relation?.Properties ?? [])
                knownProperties.Add(property.Name);
        }

        var unknownProperties = knownProperties.Count == 0
            ? []
            : AccessedProperties(query).Where(property => !knownProperties.Contains(property)).ToArray();
        return new QueryAnalysis(blocking, warnings, referenced, unknownLabels, unknownProperties, suggestions);
    }

    public static string? Scaffold(SourceGraph schema, string? label)
    {
        ArgumentNullException.ThrowIfNull(schema);
        GraphNode? target = null;
        if (!string.IsNullOrWhiteSpace(label))
            target = schema.Nodes.FirstOrDefault(node => node.Label.Equals(label, StringComparison.OrdinalIgnoreCase));
        else if (schema.Nodes.Count == 1)
            target = schema.Nodes[0];
        if (target is null)
            return null;

        var properties = target.Properties.Take(6).ToArray();
        if (properties.Length == 0)
            return $"MATCH (n:{target.Label}) RETURN n";
        var projection = string.Join(", ", properties.Select(property => $"n.{property.Name} AS {property.Name}"));
        return $"MATCH (n:{target.Label}) RETURN {projection}";
    }

    public static IReadOnlyList<string> NodeLabels(string query)
    {
        var labels = new List<string>();
        foreach (var match in NodeLabelPattern().Matches(query).Cast<System.Text.RegularExpressions.Match>())
        {
            var label = match.Groups[1].Value;
            if (!labels.Contains(label, StringComparer.Ordinal))
                labels.Add(label);
        }

        return labels;
    }

    private static string? SqlSyntax(string query)
    {
        if (HasNodePattern().IsMatch(query))
            return null;
        var looksSql = StartsWithSelect().IsMatch(query)
            || (SelectKeyword().IsMatch(query) && FromKeyword().IsMatch(query))
            || GroupBy().IsMatch(query)
            || JoinOn().IsMatch(query);
        return looksSql
            ? "This query looks like SQL. Continuous queries are Cypher: MATCH (n:Label) WHERE ... RETURN .... There is no SELECT, FROM, GROUP BY, or JOIN ... ON. Aggregations use count, sum, avg, min, max, or collect in RETURN and group by the other returned keys."
            : null;
    }

    private static string? UnsupportedClause(string query) =>
        OrderBy().IsMatch(query)
            ? "Continuous queries do not support ORDER BY. The result set is unordered and emits add, update, and delete diffs. Remove ORDER BY and sort in the consumer."
            : null;

    private static string? MissingJoin(string query, int joinsCount)
    {
        if (joinsCount > 0)
            return null;
        var labels = NodeLabels(query);
        return labels.Count < 2
            ? null
            : $"This query references {string.Join(", ", labels)} but declares no joins. A relational source has no relationship between labels, so a comma or multi-MATCH returns no rows. Declare joins and MATCH the synthetic relationship.";
    }

    private static IReadOnlyList<string> ReferencedLabels(string query) =>
        Distinct(ColonLabel().Matches(query));

    private static IReadOnlyList<string> AccessedProperties(string query) =>
        Distinct(PropertyAccess().Matches(query));

    private static IReadOnlyList<string> Distinct(System.Text.RegularExpressions.MatchCollection matches)
    {
        var values = new List<string>();
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var value = match.Groups[1].Value;
            if (!values.Contains(value, StringComparer.Ordinal))
                values.Add(value);
        }

        return values;
    }

    private static string? Closest(string name, IReadOnlyList<string> known)
    {
        var exact = known.FirstOrDefault(item => item.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (exact is not null && !exact.Equals(name, StringComparison.Ordinal))
            return exact;
        string? best = null;
        var bestDistance = 3;
        foreach (var candidate in known)
        {
            var distance = EditDistance(name.ToLowerInvariant(), candidate.ToLowerInvariant());
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return best;
    }

    private static int EditDistance(string left, string right)
    {
        if (left.Length == 0)
            return right.Length;
        if (right.Length == 0)
            return left.Length;
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var column = 0; column <= right.Length; column++)
            previous[column] = column;
        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var cost = left[row - 1] == right[column - 1] ? 0 : 1;
                current[column] = Math.Min(Math.Min(previous[column] + 1, current[column - 1] + 1), previous[column - 1] + cost);
            }

            for (var column = 0; column <= right.Length; column++)
                previous[column] = current[column];
        }

        return previous[right.Length];
    }

    [System.Text.RegularExpressions.GeneratedRegex(@":\s*([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial System.Text.RegularExpressions.Regex ColonLabel();

    [System.Text.RegularExpressions.GeneratedRegex(@"\(\s*(?:[A-Za-z_][A-Za-z0-9_]*)?\s*:\s*([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial System.Text.RegularExpressions.Regex NodeLabelPattern();

    [System.Text.RegularExpressions.GeneratedRegex(@"\b[A-Za-z_][A-Za-z0-9_]*\.([A-Za-z_][A-Za-z0-9_]*)\b(?!\s*\()")]
    private static partial System.Text.RegularExpressions.Regex PropertyAccess();

    [System.Text.RegularExpressions.GeneratedRegex(@"\bMATCH\s*\(", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex HasNodePattern();

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*SELECT\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex StartsWithSelect();

    [System.Text.RegularExpressions.GeneratedRegex(@"\bSELECT\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SelectKeyword();

    [System.Text.RegularExpressions.GeneratedRegex(@"\bFROM\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex FromKeyword();

    [System.Text.RegularExpressions.GeneratedRegex(@"\bGROUP\s+BY\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex GroupBy();

    [System.Text.RegularExpressions.GeneratedRegex(@"\bJOIN\b[\s\S]*\bON\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex JoinOn();

    [System.Text.RegularExpressions.GeneratedRegex(@"\bORDER\s+BY\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex OrderBy();
}
