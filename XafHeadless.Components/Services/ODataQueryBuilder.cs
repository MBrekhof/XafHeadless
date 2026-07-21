namespace XafHeadless.Components.Services;

public record ODataQuery(int Skip, int Top, string? OrderBy, string? Filter, string? Select, string? Expand);
public record ODataPage(System.Text.Json.JsonElement[] Rows, long Total);

// Builds the $count/$skip/$top/$filter/$orderby/$select/$expand query string for GET api/odata/{entitySet}.
public static class ODataQueryBuilder {
    public static string Build(ODataQuery q) {
        var parts = new List<string> { "$count=true", $"$skip={q.Skip}", $"$top={q.Top}" };
        if (q.Filter is not null) parts.Add($"$filter={Escape(q.Filter)}");
        if (q.OrderBy is not null) parts.Add($"$orderby={Escape(q.OrderBy)}");
        if (q.Select is not null) parts.Add($"$select={Escape(q.Select)}");
        if (q.Expand is not null) parts.Add($"$expand={Escape(q.Expand)}");
        return "?" + string.Join("&", parts);
    }

    // Task 4.3 (GRID-001 server-side grouping): the $apply=groupby query string for one grouping
    // level -- ODataGridDataSource.GetGroupInfoAsync. $orderby is legal alongside $apply (evaluated
    // after it, over the grouped set -- proven live, probe A) and keeps bucket ordering server-side.
    // GRID-004 (companion-headless backport): $top is likewise applied AFTER $apply (proven live in the
    // origin repo at 571k distinct buckets: $top=5 -> 5 buckets, 129 bytes, 21 ms), so a bounded top
    // caps the wire transfer and client materialization of a high-cardinality grouping -- the
    // ceiling check never has to fetch every bucket before rejecting.
    public static string BuildGroups(string apply, string? orderBy, int? top = null) =>
        $"?$apply={Escape(apply)}" + (orderBy is null ? "" : $"&$orderby={Escape(orderBy)}")
            + (top is null ? "" : $"&$top={top}");

    // Uri.EscapeDataString (RFC 3986) over-escapes '(' and ')' -- OData nested-query syntax
    // (e.g. $expand=Customer($select=Name)) needs them literal. Verified against .NET 8's actual
    // output (not assumed) before writing this: parens come back as %28/%29, so unescape them back.
    static string Escape(string s) => Uri.EscapeDataString(s).Replace("%28", "(").Replace("%29", ")");
}
