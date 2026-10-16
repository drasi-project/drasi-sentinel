using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Drasi.Sentinel.Domain;

namespace Drasi.Sentinel.Application;

public interface IQueryWorkbench
{
    IReadOnlyList<AgentTool> Tools { get; }

    Task<string> InvokeAsync(
        string name,
        IReadOnlyDictionary<string, string?>? arguments,
        CancellationToken cancellationToken,
        DateTimeOffset? grantedAt = null);

    Task<string?> CoverageGapAsync(string queryId, string callerText, CancellationToken cancellationToken);

    Task<string> IncludeOmittedLabelsAsync(string callerText, string reply, CancellationToken cancellationToken);

    Task<bool> MappingReadyAsync(string callerText, CancellationToken cancellationToken);

    Task<bool> CommitDueAsync(string callerText, CancellationToken cancellationToken);

    Task<string?> ContinuationAsync(string callerText, CancellationToken cancellationToken);

    Task<string> RepairJoinReplyAsync(string callerText, string reply, CancellationToken cancellationToken);
}

public sealed class QueryWorkbench(
    IQueryAuthoring authoring,
    INativeQueryRuntime runtime,
    IAuthoredQueryStore store,
    LiveCatalog catalog,
    IReadOnlySet<string> sourceIds,
    Func<QueryId, CancellationToken, Task<bool>>? queryIsWatched = null) : IQueryWorkbench
{
    public const string GetSourceSchemaTool = "get_source_schema";
    public const string GetGraphSchemaTool = "get_graph_schema";
    public const string ProfileSourceSchemaTool = "profile_source_schema";
    public const string TestQueryTool = "test_query";
    public const string GenerateQueryTool = "generate_query";
    public const string AddQueryTool = "add_query";
    public const string UpdateQueryTool = "update_query";
    public const string ListQueriesTool = "list_queries";
    public const string GetQueryResultsTool = "get_query_results";
    public const string RemoveQueryTool = "remove_query";
    public const string DiagnoseQueryTool = "diagnose_query";
    public const string DiagnoseSourceTool = "diagnose_source";
    public const string WatchQueryTool = "watch_query";
    public const string TailLogsTool = "tail_logs";
    public const string GetQueryMetricsTool = "get_query_metrics";
    public const string ListSourcesTool = "list_sources";

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
    private static readonly Regex QuotedLiteral = new(@"'([^']+)'", RegexOptions.CultureInvariant);
    private const string JoinArgument = "If a schema relation connects the labels, MATCH it and do not pass joins for that hop. Otherwise pass one joins entry for that hop: [{\"id\":\"LINK\",\"keys\":[{\"label\":\"LabelA\",\"property\":\"key_a\"},{\"label\":\"LabelB\",\"property\":\"key_b\"}]}]. Then MATCH (a:LabelA)-[:LINK]->(b:LabelB). A comma does not join. The id does not have to be LINK. A property that appears on more than one label is a join key. If the caller names it, pass joins on it and do not ask which stored value it has. If several properties are shared and the caller has not named one, ask which property name.";

    public IReadOnlyList<AgentTool> Tools { get; } =
    [
        new(GetSourceSchemaTool, "Get labels and properties for one configured source. Pass id. If the schema API returns nothing, this samples an unlabeled match and reads labels from the returned nodes. Use those labels. Do not invent labels.", [new AgentToolArgument("id", true)]),
        new(GetGraphSchemaTool, "Get the merged schema. A source that omits a schema is sampled with an unlabeled match. Use every returned label. Do not copy a label onto a source that did not return it.", (string?)null),
        new(ProfileSourceSchemaTool, "Sample a configured source and return property names plus stored example values from current rows, including a value that appears on only one row. Pass id. sampleLabels is an optional comma-separated label list. Use a stored example's exact spelling. A unique token or a unique abbreviation of one stored value is that example. A literal that matches no sample can be a future value. String comparison is case-sensitive.", [new AgentToolArgument("id", true), new AgentToolArgument("sampleLabels", false), new AgentToolArgument("limit", false)]),
        new(TestQueryTool, "Add the query under a temporary id, sample its rows, then remove it. Pass query and sources. This does not save the query. If it returns no rows, do not report that nothing was found. A future value the caller asked to watch can still be committed. " + JoinArgument, [new AgentToolArgument("query", true), new AgentToolArgument("sources", true), new AgentToolArgument("joins", false)]),
        new(GenerateQueryTool, "Validate a draft, or scaffold one when query is omitted. Tests it unless it is not Cypher. Pass commit_as, description, and ttl to save a query that tested without error, including a future value that is not present yet. commit_as is a lowercase id of letters, digits, and hyphens. ttl is none or a positive integer plus s, m, h, or d. sources is required. " + JoinArgument, [new AgentToolArgument("query", false), new AgentToolArgument("sources", true), new AgentToolArgument("joins", false), new AgentToolArgument("label", false), new AgentToolArgument("commit_as", false), new AgentToolArgument("description", false), new AgentToolArgument("ttl", false)]),
        new(AddQueryTool, "Save a tested Cypher query and register it. Pass id, query, sources, description, and ttl. id is a lowercase id of letters, digits, and hyphens. ttl is none or a positive integer plus s, m, h, or d. A taken id is not replaced; the result Committed.Id may differ. The host binds the reaction. Then call subscribe_to_changes with Committed.Id and the same ttl in this turn. Do not ask whether to start. " + JoinArgument, [new AgentToolArgument("id", true), new AgentToolArgument("query", true), new AgentToolArgument("sources", true), new AgentToolArgument("description", true), new AgentToolArgument("joins", false), new AgentToolArgument("ttl", true)]),
        new(UpdateQueryTool, "Replace the text of an authored query that no watch is using. A watched query is not replaced. Pass id, query, and sources. Curated queries cannot be updated.", [new AgentToolArgument("id", true), new AgentToolArgument("query", true), new AgentToolArgument("sources", true), new AgentToolArgument("joins", false), new AgentToolArgument("description", false)]),
        new(ListQueriesTool, "List queries and whether each is running.", (string?)null),
        new(GetQueryResultsTool, "Return the current rows of a query. Pass id.", [new AgentToolArgument("id", true)]),
        new(RemoveQueryTool, "Remove an authored query. Curated queries cannot be removed. Pass id.", [new AgentToolArgument("id", true)]),
        new(DiagnoseQueryTool, "Status, result metrics, and a short live log window for a query that returns nothing or is not running. Pass id.", [new AgentToolArgument("id", true)]),
        new(DiagnoseSourceTool, "Status, labels, and a short live log window for a configured source. Pass id. expectLabels is optional and is compared with labels from the source schema API.", [new AgentToolArgument("id", true), new AgentToolArgument("expectLabels", false)]),
        new(WatchQueryTool, "Capture add, update, and delete diffs a query emits during a short live window. Pass queryId. This observes the source; it cannot insert rows.", [new AgentToolArgument("queryId", true), new AgentToolArgument("durationMs", false)]),
        new(TailLogsTool, "Read a short live log window for a source or query. Pass target as source or query, and id. level and contains are optional.", [new AgentToolArgument("target", true), new AgentToolArgument("id", true), new AgentToolArgument("level", false), new AgentToolArgument("contains", false)]),
        new(GetQueryMetricsTool, "Return live result count and whether the query is emitting. Pass id. A count of 0 means no row matches yet.", [new AgentToolArgument("id", true)]),
        new(ListSourcesTool, "List configured source ids and status. Connection settings are not included.", (string?)null),
    ];

    public async Task<string?> CoverageGapAsync(string queryId, string callerText, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(callerText))
            return null;
        var authored = await store.FindAsync(new QueryId(queryId), cancellationToken);
        catalog.Catalog.Queries.TryGetValue(new QueryId(queryId), out var item);
        var covered = authored?.Cypher
            ?? (item is null ? null : item.Description + " " + string.Join(" ", item.Examples));
        if (covered is null)
            return null;
        if (authored is null && item is not null && !CatalogTextMatches(callerText, item))
            return $"Query '{queryId}' does not match this request. These are uncovered words. Do not subscribe to it. Ask which labels, properties, or stored values correspond to the request.";
        var labels = (await SchemaLabelsAsync(cancellationToken))
            .Where(label => Mentions(callerText, label))
            .ToArray();
        var missing = labels.Where(label => !covered.Contains(label, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (missing.Length > 0)
            return $"Query '{queryId}' does not cover {string.Join(", ", missing)}. It is only part of the request. Build a new query that includes those labels, commit it, then subscribe to the new id. Do not reuse the narrower query.";
        if (CallerDuration.Condition(callerText) is not null && !covered.Contains("trueFor", StringComparison.OrdinalIgnoreCase))
            return $"Query '{queryId}' does not wait for the duration in this request. Build a new query and subscribe to the new id. Do not replace '{queryId}'.";
        var literals = QuotedLiteral.Matches(covered)
            .Select(match => match.Groups[1].Value)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (literals.Length > 0 &&
            item?.Examples.Any(example =>
                callerText.Contains(example, StringComparison.OrdinalIgnoreCase) ||
                (callerText.Length >= 12 && example.Contains(callerText, StringComparison.OrdinalIgnoreCase))) != true)
        {
            var unnamed = literals.Where(literal => !CallerNamedLiteral(callerText, literal)).ToArray();
            if (unnamed.Length > 0)
                return $"Query '{queryId}' filters on {string.Join(", ", unnamed.Select(literal => "'" + literal + "'"))} and the caller did not name those values. It is narrower than this request. Build a new query and subscribe to the new id. Do not replace '{queryId}'.";
        }
        var described = string.Join(
            " ",
            new[] { covered, authored?.Description, item?.Description, item is null ? null : string.Join(" ", item.Examples) }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
        var stored = await StoredByPropertyAsync(cancellationToken);
        var uncovered = UncoveredConditions(callerText, covered);
        if (uncovered.Length == 0 &&
            !GuessedComplementLiteral(callerText, covered, stored) &&
            (authored is null || await RequestNamesSchemaAsync(callerText, stored, cancellationToken)))
            return null;
        var words = uncovered.Length == 0 ? "that literal" : string.Join(", ", uncovered);
        return $"Query '{queryId}' does not cover {words}. These are uncovered words. Do not subscribe and do not guess a mapping. Available fields: {await AvailableFieldsAsync(cancellationToken)}. Ask which of those labels, properties, or stored values correspond to the uncovered words. Name every label, including labels the caller did not use.";
    }

    public async Task<string> IncludeOmittedLabelsAsync(string callerText, string reply, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reply) || string.IsNullOrWhiteSpace(callerText))
            return reply;
        var labels = await SchemaLabelsAsync(cancellationToken);
        if (labels.Count < 2)
            return reply;
        var omitted = labels.Where(label => !Mentions(reply, label)).ToArray();
        if (omitted.Length == 0)
            return reply;
        var complements = IsComplements(callerText);
        if (complements.Length == 0)
            return reply;
        var properties = await PropertyNamesAsync(cancellationToken);
        var stored = await StoredValuesAsync(labels, cancellationToken);
        var unmapped = complements.Where(word =>
            !labels.Any(label => word.Equals(label, StringComparison.OrdinalIgnoreCase) || Mentions(word, label)) &&
            !properties.Contains(word, StringComparer.OrdinalIgnoreCase) &&
            !MatchesStored(word, stored)).ToArray();
        if (unmapped.Length == 0)
            return reply;
        var fields = await AvailableFieldsAsync(cancellationToken);
        if (!reply.Contains('?'))
            return reply.TrimEnd() + " Available labels and properties: " + fields + ". Which of those corresponds to the unmapped condition?";
        return reply.TrimEnd() + " Available labels and properties: " + fields + ".";
    }

    public async Task<bool> MappingReadyAsync(string callerText, CancellationToken cancellationToken)
    {
        var lines = callerText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2)
            return false;
        var latest = lines[^1];
        if (latest.Length > 40 || Regex.IsMatch(latest, @"\b(when|whenever|tell|watch|notify|keep)\b", RegexOptions.IgnoreCase))
            return false;
        var properties = await PropertyNamesAsync(cancellationToken);
        var stored = await StoredByPropertyAsync(cancellationToken);
        var earlier = string.Join('\n', lines[..^1]);
        return properties.Any(property =>
            Regex.IsMatch(earlier, $@"\b{Regex.Escape(property)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
            stored.TryGetValue(property, out var values) &&
            values.Any(value => WholeWord(latest, value)));
    }

    public async Task<string?> ContinuationAsync(string callerText, CancellationToken cancellationToken)
    {
        var follow = await FollowUpAsync(callerText, cancellationToken);
        if (follow.Instruction is not null)
            return follow.Instruction;
        if (HasNumericComparison(callerText) && IsComplements(callerText).Length == 0)
            return "Commit and subscribe in this turn. A comparison word before a number is a numeric comparison. Write the operator and that number. Do not ask which stored value that word is.";
        if (!await CommitDueAsync(callerText, cancellationToken))
            return null;
        var named = UniqueNamedFilters(callerText, await StoredByPropertyAsync(cancellationToken));
        if (named.Count == 0)
            return "Commit and subscribe in this turn. A stored value already in the caller message is chosen. Do not ask the caller to repeat it or to confirm which property holds it. Do not ask what an earlier word means.";
        var filters = string.Join(" and ", named.Select(item => item.Property + " = '" + item.Value + "'"));
        return "Commit and subscribe in this turn. The caller already named " + filters + ". Filter on only those exact stored values. Do not add a stored value the caller did not name. Do not ask which field another noun corresponds to. Other nouns are not uncovered words.";
    }

    public async Task<string> RepairJoinReplyAsync(string callerText, string reply, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reply) || string.IsNullOrWhiteSpace(callerText))
            return reply;
        var follow = await FollowUpAsync(callerText, cancellationToken);
        if (follow.Question is null)
            return reply;
        if (follow.Key is not null)
        {
            var stored = await StoredByPropertyAsync(cancellationToken);
            if (stored.TryGetValue(follow.Key, out var values) &&
                values.Any(value => reply.Contains(value, StringComparison.Ordinal) && !WholeWord(callerText, value)) &&
                (reply.Contains('?') || reply.Contains("which", StringComparison.OrdinalIgnoreCase)))
                return reply.TrimEnd() + " " + follow.Question;
            return reply;
        }

        if (follow.Choices.Count == 0 || follow.Choices.All(property => reply.Contains(property, StringComparison.OrdinalIgnoreCase)))
            return reply;
        if (reply.Contains('?') || Regex.IsMatch(reply, @"\b(relationship|join|cannot|can't|unable)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return reply.TrimEnd() + " " + follow.Question;
        return reply;
    }

    public static IReadOnlyList<string> ComplementWords(string callerText) => IsComplements(callerText);

    private async Task<JoinFollowUp> FollowUpAsync(string callerText, CancellationToken cancellationToken)
    {
        var graph = await runtime.DescribeAsync(cancellationToken);
        var stored = await StoredByPropertyAsync(cancellationToken);
        return JoinKeys.FollowUp(callerText, ComplementWords(callerText), graph.Nodes, stored);
    }

    public async Task<bool> CommitDueAsync(string callerText, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(callerText))
            return false;
        var lines = callerText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0 || IsManagement(lines[^1]))
            return false;
        if (await MappingReadyAsync(callerText, cancellationToken))
            return true;
        var stored = await StoredByPropertyAsync(cancellationToken);
        var values = stored.Values.SelectMany(item => item).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (values.Length == 0)
            return false;
        var latest = lines[^1];
        var earlier = string.Join('\n', lines[..^1]);
        if (lines.Length >= 2 &&
            latest.Length <= 40 &&
            values.Count(value => WholeWord(latest, value)) == 1 &&
            IsComplements(earlier).Any(word => !MatchesStored(word, values)))
            return true;
        if (IsComplements(callerText).Any(word => !MatchesStored(word, values)))
            return false;
        if (values.Any(value => WholeWord(callerText, value)))
            return true;
        return UniqueNamedFilters(callerText, stored).Count > 0;
    }

    internal static bool UnboundComplementLiteral(string callerText, string property, string literal) =>
        !string.IsNullOrWhiteSpace(callerText) &&
        IsComplements(callerText).Any(word => word.Equals(literal, StringComparison.OrdinalIgnoreCase)) &&
        !Regex.IsMatch(callerText, $@"\b{Regex.Escape(property)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool GuessedComplementLiteral(
        string callerText,
        string query,
        IReadOnlyDictionary<string, IReadOnlyList<string>> storedByProperty)
    {
        if (string.IsNullOrWhiteSpace(callerText))
            return false;
        if (IsComplements(callerText).Length == 0)
            return false;
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match node in Regex.Matches(
            query,
            @"\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*:\s*([A-Za-z_][A-Za-z0-9_]*)",
            RegexOptions.CultureInvariant))
            labels[node.Groups[1].Value] = node.Groups[2].Value;
        foreach (Match match in Regex.Matches(
            query,
            @"\b([A-Za-z_][A-Za-z0-9_]*)\.([A-Za-z_][A-Za-z0-9_]*)\s*=\s*'([^']*)'",
            RegexOptions.CultureInvariant))
        {
            var variable = match.Groups[1].Value;
            var property = match.Groups[2].Value;
            var literal = match.Groups[3].Value;
            if (!labels.TryGetValue(variable, out var label) || Mentions(callerText, label))
                continue;
            if (!UnboundComplementLiteral(callerText, property, literal))
                continue;
            if (storedByProperty.TryGetValue(property, out var examples) &&
                examples.Contains(literal, StringComparer.Ordinal))
                continue;
            if (storedByProperty.Any(pair =>
                pair.Value.Count > 0 &&
                !pair.Key.Equals(property, StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }

    private async Task<bool> RequestNamesSchemaAsync(
        string callerText,
        IReadOnlyDictionary<string, IReadOnlyList<string>> stored,
        CancellationToken cancellationToken)
    {
        if ((await SchemaLabelsAsync(cancellationToken)).Any(label => Mentions(callerText, label)))
            return true;
        if ((await PropertyNamesAsync(cancellationToken)).Any(property => WholeWord(callerText, property)))
            return true;
        return stored.Values.SelectMany(values => values).Any(value => WholeWord(callerText, value));
    }

    private static bool CallerNamedLiteral(string callerText, string literal)
    {
        if (callerText.Contains(literal, StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (Match word in Regex.Matches(callerText, @"\b[A-Za-z][A-Za-z0-9]{3,}\b"))
        {
            if (literal.StartsWith(word.Value, StringComparison.OrdinalIgnoreCase) && literal.Length > word.Value.Length)
                return true;
        }

        return false;
    }

    private static bool WholeWord(string text, string value) =>
        Regex.IsMatch(text, $@"\b{Regex.Escape(value)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsManagement(string text) =>
        Regex.IsMatch(
            text,
            @"\b(?:cancel|stop|pause|resume|list|show|rows|results|subscription|subscriptions|tasks|watches)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
        !Regex.IsMatch(
            text,
            @"\b(?:when|whenever|once|until|while|if)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool CatalogTextMatches(string callerText, QueryCatalogItem item)
    {
        if (item.Examples.Any(example =>
            callerText.Contains(example, StringComparison.OrdinalIgnoreCase) ||
            (callerText.Length >= 12 && example.Contains(callerText, StringComparison.OrdinalIgnoreCase))))
            return true;
        var callerTokens = ContentTokens(callerText);
        var catalogTokens = ContentTokens(item.Description + " " + string.Join(" ", item.Examples));
        return catalogTokens.Overlaps(callerTokens);
    }

    private static HashSet<string> ContentTokens(string text) =>
        Regex.Matches(text, "[A-Za-z]{4,}")
            .Select(match => match.Value.ToLowerInvariant())
            .Where(token => !Stopwords.Contains(token) && token is not ("show" or "tell" or "notify" or "alert"))
            .ToHashSet(StringComparer.Ordinal);

    private static string[] IsComplements(string callerText)
    {
        var words = new List<string>();
        foreach (Match match in Regex.Matches(
            callerText,
            @"\b([A-Za-z]{4,})\s+is\s+(?:still\s+)?(?:being\s+)?([A-Za-z]{4,})\b|\b(?:been|being)\s+([A-Za-z]{4,})\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var complement = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
            if (!Stopwords.Contains(complement) && !IsNumericComparison(callerText, match.Index, complement))
                words.Add(complement);
        }

        return words.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static IReadOnlyList<(string Property, string Value)> UniqueNamedFilters(
        string callerText,
        IReadOnlyDictionary<string, IReadOnlyList<string>> stored)
    {
        if (string.IsNullOrWhiteSpace(callerText) || stored.Count == 0)
            return [];
        var chosen = new List<(string Property, string Value)>();
        foreach (Match word in Regex.Matches(callerText, @"\b[A-Za-z][A-Za-z0-9]{3,}\b"))
        {
            if (Stopwords.Contains(word.Value))
                continue;
            var hits = new List<(string Property, string Value)>();
            foreach (var (property, values) in stored)
            {
                foreach (var value in values)
                {
                    if (value.Equals(word.Value, StringComparison.OrdinalIgnoreCase) ||
                        (value.StartsWith(word.Value, StringComparison.OrdinalIgnoreCase) && value.Length > word.Value.Length))
                        hits.Add((property, value));
                }
            }

            var distinct = hits
                .GroupBy(hit => hit.Property + "\0" + hit.Value, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            if (distinct.Length != 1)
                continue;
            if (chosen.Any(item =>
                item.Property.Equals(distinct[0].Property, StringComparison.OrdinalIgnoreCase) &&
                item.Value.Equals(distinct[0].Value, StringComparison.OrdinalIgnoreCase)))
                continue;
            chosen.Add(distinct[0]);
        }

        return chosen;
    }

    internal static bool IsNumericComparison(string callerText, int at, string complement) =>
        ComparisonWords.Contains(complement) &&
        Regex.IsMatch(callerText[at..], @"\d", RegexOptions.CultureInvariant);

    private static bool HasNumericComparison(string callerText) =>
        Regex.Matches(
            callerText,
            @"\b([A-Za-z]{4,})\s+is\s+(?:still\s+)?(?:being\s+)?([A-Za-z]{4,})\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Any(match => IsNumericComparison(callerText, match.Index, match.Groups[2].Value));

    private static readonly HashSet<string> ComparisonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "greater", "less", "under", "over", "above", "below", "more", "fewer", "higher", "lower",
    };

    private static bool MatchesStored(string word, IReadOnlyList<string> values) =>
        values.Any(value => value.Equals(word, StringComparison.OrdinalIgnoreCase)) ||
        (word.Length >= 4 && values.Count(value => value.Contains(word, StringComparison.OrdinalIgnoreCase)) == 1);

    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "at", "be", "been", "by", "for", "from", "has", "have",
        "how", "in", "into", "is", "it", "its", "keep", "long", "me", "my", "not", "of",
        "on", "or", "our", "please", "same", "tell", "that", "the", "their", "then",
        "these", "this", "those", "to", "watch", "watching", "when", "with", "you", "your",
        "day", "days", "hour", "hours", "minute", "minutes", "second", "seconds", "none",
        "now", "will", "let", "know", "about", "just", "only", "also",
    };

    internal static string[] UncoveredConditions(string callerText, string covered)
    {
        var tokens = Regex.Matches(covered, "[A-Za-z]{4,}")
            .Select(match => match.Value.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var pending = new List<(string? Subject, string Complement)>();
        foreach (Match match in Regex.Matches(
            callerText,
            @"\b([A-Za-z]{4,})\s+is\s+(?:still\s+)?(?:being\s+)?([A-Za-z]{4,})\b|\b(?:been|being)\s+([A-Za-z]{4,})\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var subject = match.Groups[1].Success ? match.Groups[1].Value : null;
            var complement = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
            if (Stopwords.Contains(complement) || Covers(tokens, complement) || IsNumericComparison(callerText, match.Index, complement))
                continue;
            var place = Regex.Match(
                callerText[match.Index..],
                @"\b(?:at|in)\s+the\s+([A-Za-z]{4,})",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (place.Success && Covers(tokens, place.Groups[1].Value))
                continue;
            pending.Add((subject, complement));
        }

        if (pending.Count == 1 && NamedEqualityCovers(callerText, covered, pending[0].Complement))
            pending.Clear();
        else if (pending.Count > 0 && UnmappedMatches(callerText, covered) == 1 && ExtraCallerLiteral(callerText, covered))
            pending.Clear();

        var gaps = new List<string>();
        foreach (var (subject, complement) in pending)
        {
            if (subject is not null && !Stopwords.Contains(subject) && !Covers(tokens, subject))
                gaps.Add(subject);
            gaps.Add(complement);
        }

        return gaps.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool NamedEqualityCovers(string callerText, string covered, string complement)
    {
        foreach (Match filter in Regex.Matches(
            covered,
            @"\b([A-Za-z_][A-Za-z0-9_]*)\s*=\s*'([^']+)'",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var property = filter.Groups[1].Value;
            var literal = filter.Groups[2].Value;
            if (literal.Equals(complement, StringComparison.OrdinalIgnoreCase))
                continue;
            if (Regex.IsMatch(callerText, $@"\b{Regex.Escape(property)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
                Regex.IsMatch(callerText, $@"\b{Regex.Escape(literal)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return true;
        }

        return false;
    }

    private static int UnmappedMatches(string callerText, string covered)
    {
        var tokens = Regex.Matches(covered, "[A-Za-z]{4,}")
            .Select(match => match.Value.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var count = 0;
        foreach (Match match in Regex.Matches(
            callerText,
            @"\b([A-Za-z]{4,})\s+is\s+(?:still\s+)?(?:being\s+)?([A-Za-z]{4,})\b|\b(?:been|being)\s+([A-Za-z]{4,})\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var complement = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
            if (!Stopwords.Contains(complement) && !Covers(tokens, complement))
                count++;
        }

        return count;
    }

    private static bool ExtraCallerLiteral(string callerText, string covered)
    {
        var tokens = Regex.Matches(covered, "[A-Za-z]{4,}")
            .Select(match => match.Value.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (Match literal in Regex.Matches(covered, "'([^']+)'"))
        {
            var value = literal.Groups[1].Value;
            if (!callerText.Contains(value, StringComparison.OrdinalIgnoreCase))
                continue;
            if (Covers(tokens, value) && !IsComplementOfCoveredMatch(callerText, tokens, value))
                return true;
        }

        return false;
    }

    private static bool IsComplementOfCoveredMatch(string callerText, IReadOnlyList<string> tokens, string value)
    {
        foreach (Match match in Regex.Matches(
            callerText,
            @"\b([A-Za-z]{4,})\s+is\s+(?:still\s+)?(?:being\s+)?([A-Za-z]{4,})\b|\b(?:been|being)\s+([A-Za-z]{4,})\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var complement = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
            if (complement.Equals(value, StringComparison.OrdinalIgnoreCase) && Covers(tokens, complement))
                return true;
        }

        return false;
    }

    private static bool Covers(IReadOnlyList<string> tokens, string word)
    {
        var lower = word.ToLowerInvariant();
        foreach (var token in tokens)
        {
            if (token.Contains(lower, StringComparison.Ordinal) ||
                lower.Contains(token, StringComparison.Ordinal) ||
                token.StartsWith(lower, StringComparison.Ordinal) ||
                lower.StartsWith(token, StringComparison.Ordinal) ||
                (lower.Length >= 5 && token.Length >= 5 && token.StartsWith(lower[..5], StringComparison.Ordinal)))
                return true;
        }

        return false;
    }

    private async Task<string> AvailableFieldsAsync(CancellationToken cancellationToken)
    {
        var fields = new List<string>();
        foreach (var id in sourceIds.OrderBy(item => item, StringComparer.Ordinal))
        {
            var schema = await runtime.SourceSchemaAsync(id, cancellationToken);
            if (schema?["nodes"] is not JsonArray nodes)
                continue;
            foreach (var node in nodes)
            {
                var label = node?["label"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(label))
                    continue;
                var names = PropertyNames(node?["properties"] as JsonArray);
                fields.Add(names.Length == 0 ? label : $"{label} ({string.Join(", ", names)})");
            }
        }

        return fields.Count == 0
            ? "no labels were reported"
            : string.Join("; ", fields.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string[] PropertyNames(JsonArray? properties)
    {
        if (properties is null)
            return [];
        return properties
            .Select(property => property switch
            {
                JsonObject obj => obj["name"]?.GetValue<string>(),
                JsonValue value when value.TryGetValue<string>(out var name) => name,
                _ => null,
            })
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool Mentions(string text, string label)
    {
        if (Regex.IsMatch(text, $@"\b{Regex.Escape(label)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return true;
        return label.Length > 4 &&
            label.EndsWith('s') &&
            Regex.IsMatch(text, $@"\b{Regex.Escape(label[..^1])}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public Task<string> InvokeAsync(
        string name,
        IReadOnlyDictionary<string, string?>? arguments,
        CancellationToken cancellationToken,
        DateTimeOffset? grantedAt = null) => name switch
    {
        GetSourceSchemaTool => SourceSchemaAsync(arguments, cancellationToken),
        GetGraphSchemaTool => GraphSchemaAsync(cancellationToken),
        ProfileSourceSchemaTool => ProfileAsync(arguments, cancellationToken),
        TestQueryTool => authoring.AttemptJsonAsync(WithoutCommit(arguments), cancellationToken, grantedAt),
        GenerateQueryTool => authoring.AttemptJsonAsync(arguments ?? new Dictionary<string, string?>(), cancellationToken, grantedAt),
        AddQueryTool => authoring.AttemptJsonAsync(AsCommit(arguments), cancellationToken, grantedAt),
        UpdateQueryTool => authoring.UpdateJsonAsync(arguments ?? new Dictionary<string, string?>(), cancellationToken),
        ListQueriesTool => ListQueriesAsync(cancellationToken),
        GetQueryResultsTool => ResultsAsync(arguments, cancellationToken),
        RemoveQueryTool => RemoveAsync(arguments, cancellationToken),
        DiagnoseQueryTool => DiagnoseQueryAsync(arguments, cancellationToken),
        DiagnoseSourceTool => DiagnoseSourceAsync(arguments, cancellationToken),
        WatchQueryTool => WatchAsync(arguments, cancellationToken),
        TailLogsTool => TailAsync(arguments, cancellationToken),
        GetQueryMetricsTool => MetricsAsync(arguments, cancellationToken),
        ListSourcesTool => ListSourcesAsync(cancellationToken),
        _ => throw new SentinelException("invalid_tool_arguments", "The tool name is not valid."),
    };

    private async Task<string> SourceSchemaAsync(IReadOnlyDictionary<string, string?>? arguments, CancellationToken cancellationToken)
    {
        var id = RequireSource(arguments);
        var reported = await runtime.SourceSchemaAsync(id, cancellationToken);
        return JsonSerializer.Serialize(new
        {
            Kind = "source_schema",
            Id = id,
            Described = reported is not null,
            Method = reported is not null ? "reported" : "none",
            Schema = reported,
        }, Json);
    }

    private async Task<string> GraphSchemaAsync(CancellationToken cancellationToken)
    {
        var schema = await runtime.GraphSchemaAsync(cancellationToken);
        return JsonSerializer.Serialize(new
        {
            Kind = "graph_schema",
            SourceIds = sourceIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            Schema = schema,
        }, Json);
    }

    private async Task<string> ProfileAsync(IReadOnlyDictionary<string, string?>? arguments, CancellationToken cancellationToken)
    {
        var id = RequireSource(arguments);
        var reported = await runtime.SourceSchemaAsync(id, cancellationToken);
        var labels = Labels(arguments, "sampleLabels");
        if (labels.Count == 0 && reported?["nodes"] is JsonArray nodes)
        {
            labels = nodes
                .Select(node => node?["label"]?.GetValue<string>())
                .Where(label => label is not null && Identifier.IsMatch(label))
                .Cast<string>()
                .Take(8)
                .ToArray();
        }

        if (labels.Count == 0)
        {
            return JsonSerializer.Serialize(new
            {
                Kind = "source_profile",
                Id = id,
                Method = "none",
                Schema = reported,
                Note = "The source schema did not report labels. Do not invent them.",
            }, Json);
        }

        var limit = Math.Clamp(Number(arguments, "limit") ?? 25, 1, 50);
        var sampled = new List<object>();
        foreach (var label in labels.Take(8))
        {
            if (!Identifier.IsMatch(label))
                continue;
            var probe = await runtime.ProbeAsync($"MATCH (n:{label}) RETURN n", [id], [], TimeSpan.FromSeconds(5), cancellationToken);
            var rows = probe.Ok ? probe.Rows.Take(limit).ToArray() : [];
            sampled.Add(new
            {
                Label = label,
                Properties = QuerySamples.PropertyNames(rows),
                Examples = QuerySamples.Examples(rows),
                Sampled = rows.Length,
                Error = probe.Ok ? null : probe.Error,
            });
        }

        return JsonSerializer.Serialize(new
        {
            Kind = "source_profile",
            Id = id,
            Method = reported is null ? "sampled" : "described_and_sampled",
            Schema = reported,
            Nodes = sampled,
            Hint = "Examples are stored values from current rows. Use one of those exact strings. Do not paraphrase the caller.",
        }, Json);
    }

    private async Task<string> ListQueriesAsync(CancellationToken cancellationToken)
    {
        var authored = (await store.ListAsync(cancellationToken)).Select(query => query.Id.Value).ToHashSet(StringComparer.Ordinal);
        var queries = await runtime.ListQueriesAsync(cancellationToken);
        return JsonSerializer.Serialize(new
        {
            Kind = "queries",
            Queries = queries.Select(query => new { query.Id, query.Status, Authored = authored.Contains(query.Id) }).ToArray(),
        }, Json);
    }

    private async Task<string> ResultsAsync(IReadOnlyDictionary<string, string?>? arguments, CancellationToken cancellationToken)
    {
        var id = Required(arguments, "id");
        var rows = await runtime.ResultsAsync(new QueryId(id), cancellationToken);
        return JsonSerializer.Serialize(new { Kind = "query_results", Id = id, RowCount = rows.Count, Rows = rows.Take(50).ToArray() }, Json);
    }

    private async Task<string> RemoveAsync(IReadOnlyDictionary<string, string?>? arguments, CancellationToken cancellationToken)
    {
        var id = new QueryId(Required(arguments, "id"));
        var authored = await store.FindAsync(id, cancellationToken);
        if (authored is null)
        {
            if (catalog.Catalog.Queries.ContainsKey(id))
                throw new SentinelException("query_rejected", "A curated query cannot be removed.");
            throw new SentinelException("query_unknown", $"Query '{id.Value}' does not exist.");
        }

        if (queryIsWatched is not null && await queryIsWatched(id, cancellationToken))
            throw new SentinelException("query_rejected", $"Query '{id.Value}' is already watched. Do not remove it.");

        await runtime.RemoveAsync(id, cancellationToken);
        await store.DeleteAsync(id, cancellationToken);
        catalog.Remove(id);
        return JsonSerializer.Serialize(new { Kind = "query_removed", Id = id.Value }, Json);
    }

    private async Task<string> DiagnoseQueryAsync(IReadOnlyDictionary<string, string?>? arguments, CancellationToken cancellationToken)
    {
        var id = Required(arguments, "id");
        var queries = await runtime.ListQueriesAsync(cancellationToken);
        var entry = queries.FirstOrDefault(query => query.Id == id);
        JsonObject? metrics = null;
        string? metricsError = null;
        try
        {
            metrics = await runtime.QueryMetricsAsync(new QueryId(id), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            metricsError = exception.Message;
        }

        var logs = await SafeLogs("query", id, cancellationToken);
        var running = entry?.Status.Equals("Running", StringComparison.OrdinalIgnoreCase) == true;
        return JsonSerializer.Serialize(new
        {
            Kind = "query_diagnosis",
            Id = id,
            Found = entry is not null,
            Status = entry?.Status ?? "not found",
            Metrics = metrics,
            MetricsError = metricsError,
            RecentLogs = logs,
            Hint = running
                ? "If the result count is 0, sample values with profile_source_schema and retest without the filter. Observe diffs with watch_query."
                : "The query is not running. Run its text through test_query, then update_query.",
        }, Json);
    }

    private async Task<string> DiagnoseSourceAsync(IReadOnlyDictionary<string, string?>? arguments, CancellationToken cancellationToken)
    {
        var id = RequireSource(arguments);
        var sources = await runtime.ListSourcesAsync(cancellationToken);
        var entry = sources.FirstOrDefault(source => source.Id == id);
        JsonNode? schema = null;
        string? schemaError = null;
        try
        {
            schema = await runtime.SourceSchemaAsync(id, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            schemaError = exception.Message;
        }

        var labels = schema?["nodes"] is JsonArray nodes
            ? nodes.Select(node => node?["label"]?.GetValue<string>()).Where(label => label is not null).Cast<string>().ToArray()
            : [];
        var expected = Labels(arguments, "expectLabels");
        var missing = expected.Where(label => !labels.Contains(label, StringComparer.Ordinal)).ToArray();
        var logs = await SafeLogs("source", id, cancellationToken);
        var errors = logs.Where(line => line.Level.Contains("error", StringComparison.OrdinalIgnoreCase)).Take(20).ToArray();
        var running = entry?.Status.Equals("Running", StringComparison.OrdinalIgnoreCase) == true;
        var healthy = running && errors.Length == 0 && missing.Length == 0 && schemaError is null;
        return JsonSerializer.Serialize(new
        {
            Kind = "source_diagnosis",
            Id = id,
            Found = entry is not null,
            Status = entry?.Status ?? "not found",
            Running = running,
            SchemaVisible = schema is not null,
            SchemaError = schemaError,
            Labels = labels,
            MissingLabels = missing,
            ErrorCount = errors.Length,
            Errors = errors,
            RecentLogs = logs.Take(40).ToArray(),
            Verdict = healthy ? "healthy" : "problem",
            Hint = healthy
                ? "The source is running and the expected labels are visible."
                : "A running status does not prove rows were loaded. Check missing labels from the source schema and error lines.",
        }, Json);
    }

    private async Task<string> WatchAsync(IReadOnlyDictionary<string, string?>? arguments, CancellationToken cancellationToken)
    {
        var id = Required(arguments, "queryId");
        var window = TimeSpan.FromMilliseconds(Math.Clamp(Number(arguments, "durationMs") ?? 1500, 200, 8000));
        var deltas = await runtime.WatchAsync(new QueryId(id), window, 100, cancellationToken);
        IReadOnlyList<JsonNode> snapshot;
        try
        {
            snapshot = (await runtime.ResultsAsync(new QueryId(id), cancellationToken)).Take(50).ToArray();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            snapshot = [];
        }

        return JsonSerializer.Serialize(new
        {
            Kind = "query_watch",
            QueryId = id,
            DeltaCount = deltas.Count,
            Deltas = deltas,
            ResultSnapshot = snapshot,
            Note = "This observes live diffs. It cannot insert rows into a configured source.",
        }, Json);
    }

    private async Task<string> TailAsync(IReadOnlyDictionary<string, string?>? arguments, CancellationToken cancellationToken)
    {
        var target = Required(arguments, "target");
        if (target is not ("source" or "query"))
            throw new SentinelException("invalid_tool_arguments", "target must be source or query.");
        var id = Required(arguments, "id");
        if (target == "source")
            RequireSource(new Dictionary<string, string?> { ["id"] = id });
        var logs = await runtime.TailLogsAsync(target, id, TimeSpan.FromMilliseconds(800), cancellationToken);
        var level = Optional(arguments, "level");
        var contains = Optional(arguments, "contains");
        var filtered = logs.Where(line =>
            (level is null || line.Level.Contains(level, StringComparison.OrdinalIgnoreCase)) &&
            (contains is null || line.Message.Contains(contains, StringComparison.OrdinalIgnoreCase))).Take(80).ToArray();
        return JsonSerializer.Serialize(new
        {
            Kind = "logs",
            Target = target,
            Id = id,
            Count = filtered.Length,
            Logs = filtered,
            Note = "This is a short live window, not a persisted history.",
        }, Json);
    }

    private async Task<string> MetricsAsync(IReadOnlyDictionary<string, string?>? arguments, CancellationToken cancellationToken)
    {
        var id = Required(arguments, "id");
        var metrics = await runtime.QueryMetricsAsync(new QueryId(id), cancellationToken);
        return JsonSerializer.Serialize(new { Kind = "query_metrics", Id = id, Metrics = metrics }, Json);
    }

    private async Task<string> ListSourcesAsync(CancellationToken cancellationToken)
    {
        var sources = await runtime.ListSourcesAsync(cancellationToken);
        var allowed = sources.Where(source => sourceIds.Contains(source.Id)).ToArray();
        return JsonSerializer.Serialize(new { Kind = "sources", Sources = allowed }, Json);
    }

    private async Task<IReadOnlyList<RuntimeLogLine>> SafeLogs(string target, string id, CancellationToken cancellationToken)
    {
        try
        {
            return await runtime.TailLogsAsync(target, id, TimeSpan.FromMilliseconds(800), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return [new RuntimeLogLine(DateTimeOffset.UtcNow, "error", exception.Message)];
        }
    }

    private string RequireSource(IReadOnlyDictionary<string, string?>? arguments)
    {
        var id = Required(arguments, "id");
        if (!sourceIds.Contains(id))
            throw new SentinelException("source_unknown", $"Source '{id}' is not configured.");
        return id;
    }

    private static IReadOnlyDictionary<string, string?> AsCommit(IReadOnlyDictionary<string, string?>? arguments)
    {
        var copy = new Dictionary<string, string?>(arguments ?? new Dictionary<string, string?>());
        copy["commit_as"] = Required(arguments, "id");
        return copy;
    }

    private static IReadOnlyDictionary<string, string?> WithoutCommit(IReadOnlyDictionary<string, string?>? arguments)
    {
        var copy = new Dictionary<string, string?>(arguments ?? new Dictionary<string, string?>());
        copy.Remove("commit_as");
        return copy;
    }

    private static IReadOnlyList<string> Labels(IReadOnlyDictionary<string, string?>? arguments, string name) =>
        Optional(arguments, name)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

    private static int? Number(IReadOnlyDictionary<string, string?>? arguments, string name) =>
        int.TryParse(Optional(arguments, name), out var value) ? value : null;

    private static string Required(IReadOnlyDictionary<string, string?>? arguments, string name) =>
        Optional(arguments, name) ?? throw new SentinelException("invalid_tool_arguments", $"Pass {name}.");

    private static string? Optional(IReadOnlyDictionary<string, string?>? arguments, string name) =>
        arguments is not null && arguments.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private async Task<IReadOnlyList<string>> PropertyNamesAsync(CancellationToken cancellationToken)
    {
        var names = new List<string>();
        foreach (var id in sourceIds.OrderBy(item => item, StringComparer.Ordinal))
        {
            var schema = await runtime.SourceSchemaAsync(id, cancellationToken);
            if (schema?["nodes"] is not JsonArray nodes)
                continue;
            foreach (var node in nodes)
                names.AddRange(PropertyNames(node?["properties"] as JsonArray));
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task<IReadOnlyList<string>> StoredValuesAsync(IReadOnlyList<string> labels, CancellationToken cancellationToken)
    {
        if (labels.Count == 0)
            return [];
        var stored = await StoredByPropertyAsync(cancellationToken);
        return stored.Values.SelectMany(values => values).ToArray();
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> StoredByPropertyAsync(CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in sourceIds.OrderBy(item => item, StringComparer.Ordinal))
        {
            var schema = await runtime.SourceSchemaAsync(id, cancellationToken);
            if (schema?["nodes"] is not JsonArray nodes)
                continue;
            foreach (var node in nodes)
            {
                var label = node?["label"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(label) || !Identifier.IsMatch(label))
                    continue;
                var probe = await runtime.ProbeAsync(
                    $"MATCH (n:{label}) RETURN n",
                    [id],
                    [],
                    TimeSpan.FromSeconds(5),
                    cancellationToken);
                if (!probe.Ok)
                    continue;
                foreach (var examples in QuerySamples.Examples(probe.Rows))
                {
                    if (!values.TryGetValue(examples.Key, out var list))
                        values[examples.Key] = list = [];
                    foreach (var example in examples.Value)
                    {
                        if (example is JsonValue value &&
                            value.TryGetValue<string>(out var text) &&
                            text.Length > 0 &&
                            !list.Contains(text, StringComparer.Ordinal))
                            list.Add(text);
                    }
                }
            }
        }

        return values.ToDictionary(item => item.Key, item => (IReadOnlyList<string>)item.Value, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyList<string>> SchemaLabelsAsync(CancellationToken cancellationToken)
    {
        var labels = new List<string>();
        foreach (var id in sourceIds.OrderBy(item => item, StringComparer.Ordinal))
        {
            var schema = await runtime.SourceSchemaAsync(id, cancellationToken);
            if (schema?["nodes"] is not JsonArray nodes)
                continue;
            foreach (var node in nodes)
            {
                var label = node?["label"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(label))
                    labels.Add(label);
            }
        }

        return labels.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

public static class QuerySamples
{
    private static readonly Regex ElementLabels = new(
        "labels:\\s*\\[(?<labels>[^\\]]*)\\]",
        RegexOptions.CultureInvariant);
    private static readonly Regex QuotedLabel = new(
        "\"(?<label>[A-Za-z_][A-Za-z0-9_]*)\"",
        RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> Labels(IReadOnlyList<JsonNode> rows)
    {
        var labels = new List<string>();
        foreach (var row in rows)
            CollectLabels(row, labels);
        return labels;
    }

    private static void CollectLabels(JsonNode? node, List<string> labels)
    {
        switch (node)
        {
            case JsonValue value when value.TryGetValue<string>(out var text):
                foreach (Match match in ElementLabels.Matches(text))
                {
                    foreach (Match quoted in QuotedLabel.Matches(match.Groups["labels"].Value))
                    {
                        var label = quoted.Groups["label"].Value;
                        if (!labels.Contains(label, StringComparer.Ordinal))
                            labels.Add(label);
                    }
                }
                break;
            case JsonObject obj:
                foreach (var property in obj)
                    CollectLabels(property.Value, labels);
                break;
            case JsonArray array:
                foreach (var item in array)
                    CollectLabels(item, labels);
                break;
        }
    }

    public static IReadOnlyList<string> PropertyNames(IReadOnlyList<JsonNode> rows)
    {
        var names = new List<string>();
        foreach (var row in rows)
        {
            foreach (var property in Element(row))
            {
                if (!names.Contains(property.Key, StringComparer.Ordinal))
                    names.Add(property.Key);
            }
        }

        return names;
    }

    public static IReadOnlyList<string> NodeProperties(IReadOnlyList<JsonNode> rows)
    {
        var names = new List<string>();
        foreach (var row in rows)
        {
            foreach (var property in Element(row))
            {
                if (property.Value is JsonObject or JsonArray)
                    continue;
                if (!names.Contains(property.Key, StringComparer.Ordinal))
                    names.Add(property.Key);
            }
        }

        return names;
    }

    public static IReadOnlyDictionary<string, IReadOnlyList<JsonNode>> Examples(IReadOnlyList<JsonNode> rows)
    {
        var values = new Dictionary<string, List<JsonNode>>(StringComparer.Ordinal);
        var seen = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            foreach (var property in Element(row))
            {
                if (property.Value is null || property.Value is JsonObject or JsonArray)
                    continue;
                var key = property.Value.ToJsonString();
                if (!seen.TryGetValue(property.Key, out var keys))
                    seen[property.Key] = keys = new HashSet<string>(StringComparer.Ordinal);
                if (!keys.Add(key) || values.GetValueOrDefault(property.Key)?.Count >= 5)
                    continue;
                if (!values.TryGetValue(property.Key, out var list))
                    values[property.Key] = list = [];
                list.Add(property.Value.DeepClone());
            }
        }

        return values.ToDictionary(item => item.Key, item => (IReadOnlyList<JsonNode>)item.Value, StringComparer.Ordinal);
    }

    private static IEnumerable<KeyValuePair<string, JsonNode?>> Element(JsonNode row)
    {
        if (row is not JsonObject obj)
            yield break;
        if (obj.Count == 1 && obj.First().Value is JsonObject nested)
        {
            foreach (var property in nested)
                yield return property;
            yield break;
        }

        var expanded = false;
        foreach (var property in obj)
        {
            if (property.Value is not JsonValue value ||
                !value.TryGetValue<string>(out var text) ||
                !TryElementProperties(text, out var properties))
                continue;
            expanded = true;
            foreach (var parsed in properties)
                yield return parsed;
        }

        if (expanded)
            yield break;

        foreach (var property in obj)
            yield return property;
    }

    private static bool TryElementProperties(string text, out List<KeyValuePair<string, JsonNode?>> properties)
    {
        properties = [];
        var start = text.IndexOf("values:", StringComparison.Ordinal);
        if (start < 0 || !text.Contains("ElementPropertyMap", StringComparison.Ordinal))
            return false;
        foreach (Match match in ElementProperty.Matches(text[start..]))
            properties.Add(new(match.Groups[1].Value, JsonValue.Create(match.Groups[2].Value)));
        return properties.Count > 0;
    }

    private static readonly Regex ElementProperty = new(
        "\"([A-Za-z_][A-Za-z0-9_]*)\"\\s*:\\s*String\\(\"([^\"]*)\"\\)",
        RegexOptions.CultureInvariant);
}
