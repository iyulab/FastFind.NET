using FastFind.Models;

namespace FastFind.Indexing;

/// <summary>
/// What <see cref="SearchQuery.MaxResults"/> means to an engine: the cap on what it returns, and a way to know
/// whether anything lay beyond it.
/// </summary>
/// <remarks>
/// <para>
/// An engine that asks an index for exactly <c>MaxResults</c> matches cannot tell "there were exactly that
/// many" from "there were more", and an engine that collects every match to count them pays for the whole
/// corpus on every capped query (a query for 100 results over a million entries built a list of up to 99,706).
/// Both engines ask for <b>one more than the cap</b>: one past it proves there is more, and nothing is
/// collected beyond that.
/// </para>
/// <para>
/// So <see cref="SearchResult.TotalMatches"/> is the number of matches found before the search stopped — the
/// exact count when <see cref="SearchResult.HasMoreResults"/> is false, and at least <c>MaxResults + 1</c>
/// when it is true.
/// </para>
/// </remarks>
internal static class ResultCap
{
    /// <summary>The query as the index should see it: one match past the cap, so that "more" is observed.</summary>
    public static SearchQuery Probe(SearchQuery query)
    {
        if (query.MaxResults is not { } cap || cap == int.MaxValue) return query;

        var probe = query.Clone();
        probe.MaxResults = cap + 1;
        return probe;
    }

    /// <summary>Whether <paramref name="found"/> matches is past the cap, which proves there are more.</summary>
    public static bool IsPastCap(SearchQuery query, long found) => query.MaxResults is { } cap && found > cap;
}
