namespace Drasi.Sentinel.Domain;

public enum UnboundQueryReason { NoReaction, ManyReactions, UnknownReaction }

public abstract record QueryBinding(QueryId Query);
public sealed record BoundQuery(QueryId Query, ReactionId Reaction) : QueryBinding(Query);
public sealed record UnboundQuery(QueryId Query, UnboundQueryReason Reason, ReactionId? Missing = null) : QueryBinding(Query);

public static class CatalogBinding
{
    public static IReadOnlyList<QueryBinding> From(SafeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Queries.Values
            .OrderBy(query => query.Id.Value, StringComparer.Ordinal)
            .Select(query => Bind(catalog, query))
            .ToArray();
    }

    public static BoundQuery Require(SafeCatalog catalog, QueryId query)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!catalog.Queries.TryGetValue(query, out var item))
            throw new SentinelException("query_unknown", "The query is not in the catalog.");
        return Bind(catalog, item) switch
        {
            BoundQuery bound => bound,
            UnboundQuery { Reason: UnboundQueryReason.NoReaction } =>
                throw new SentinelException("reaction_unbound", "The query has no reaction to bind."),
            UnboundQuery { Reason: UnboundQueryReason.UnknownReaction, Missing: var missing } =>
                throw new SentinelException("reaction_unknown", $"The query allows unknown reaction '{missing?.Value}'."),
            _ => throw new SentinelException("query_ambiguous", "The query has more than one reaction."),
        };
    }

    private static QueryBinding Bind(SafeCatalog catalog, QueryCatalogItem item)
    {
        var reactions = item.AllowedReactions
            .OrderBy(reaction => reaction.Value, StringComparer.Ordinal)
            .ToArray();
        var missing = reactions.FirstOrDefault(reaction => !catalog.Reactions.ContainsKey(reaction));
        if (missing != default)
            return new UnboundQuery(item.Id, UnboundQueryReason.UnknownReaction, missing);
        return reactions.Length switch
        {
            1 => new BoundQuery(item.Id, reactions[0]),
            0 => new UnboundQuery(item.Id, UnboundQueryReason.NoReaction),
            _ => new UnboundQuery(item.Id, UnboundQueryReason.ManyReactions),
        };
    }
}
