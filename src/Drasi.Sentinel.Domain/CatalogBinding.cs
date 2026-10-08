namespace Drasi.Sentinel.Domain;

public enum UnboundQueryReason { NoReaction, ManyReactions }

public abstract record QueryBinding(QueryId Query);
public sealed record BoundQuery(QueryId Query, ReactionId Reaction) : QueryBinding(Query);
public sealed record UnboundQuery(QueryId Query, UnboundQueryReason Reason) : QueryBinding(Query);

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
            _ => throw new SentinelException("query_ambiguous", "The query has more than one reaction."),
        };
    }

    private static QueryBinding Bind(SafeCatalog catalog, QueryCatalogItem item)
    {
        var reactions = item.AllowedReactions
            .Where(catalog.Reactions.ContainsKey)
            .OrderBy(reaction => reaction.Value, StringComparer.Ordinal)
            .ToArray();
        return reactions.Length switch
        {
            1 => new BoundQuery(item.Id, reactions[0]),
            0 => new UnboundQuery(item.Id, UnboundQueryReason.NoReaction),
            _ => new UnboundQuery(item.Id, UnboundQueryReason.ManyReactions),
        };
    }
}
