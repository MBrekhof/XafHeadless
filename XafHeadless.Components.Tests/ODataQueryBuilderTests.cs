using XafHeadless.Components.Services;

namespace XafHeadless.Components.Tests;

[TestClass]
public class ODataQueryBuilderTests {
    [TestMethod]
    public void Builds_paging_sorting_and_count() {
        var q = new ODataQuery(Skip: 20, Top: 10, OrderBy: "Name desc", Filter: null, Select: null, Expand: null);
        Assert.AreEqual("?$count=true&$skip=20&$top=10&$orderby=Name%20desc", ODataQueryBuilder.Build(q));
    }
    [TestMethod]
    public void Master_filter_and_expand_compose() {
        var q = new ODataQuery(0, 25, null, "Parent/ID eq 42", null, "Customer($select=Name)");
        Assert.AreEqual("?$count=true&$skip=0&$top=25&$filter=Parent%2FID%20eq%2042&$expand=Customer(%24select%3DName)",
            ODataQueryBuilder.Build(q));
    }

    // ---- Task 4.2 (GRID-001 hybrid binding): the two wire shapes the routing depends on ----

    [TestMethod]
    public void Count_probe_is_count_true_top_zero() {
        // XafListView's routing probe: total count only, zero rows (probe B).
        Assert.AreEqual("?$count=true&$skip=0&$top=0",
            ODataQueryBuilder.Build(new ODataQuery(0, 0, null, null, null, null)));
    }

    [TestMethod]
    public void Server_page_composes_paging_sort_and_filter() {
        // One DxGrid server-mode page fetch: page 3 (skip 50, top 25), user sort, filter-row filter.
        var q = new ODataQuery(50, 25, "BatchStatus desc", "contains(Name,'0001')", null, null);
        Assert.AreEqual("?$count=true&$skip=50&$top=25&$filter=contains(Name%2C%270001%27)&$orderby=BatchStatus%20desc",
            ODataQueryBuilder.Build(q));
    }

    // ---- Task 4.3 (GRID-001 server-side grouping): the $apply wire shape ----

    [TestMethod]
    public void Groups_query_composes_apply_and_orderby() {
        // One GetGroupInfoAsync fetch: filtered groupby + bucket ordering (probe A/B).
        Assert.AreEqual(
            "?$apply=filter(Closed%20eq%20%27F%27)%2Fgroupby((BatchStatus)%2Caggregate(%24count%20as%20Count))&$orderby=BatchStatus%20asc",
            ODataQueryBuilder.BuildGroups("filter(Closed eq 'F')/groupby((BatchStatus),aggregate($count as Count))", "BatchStatus asc"));
        Assert.AreEqual("?$apply=groupby((BatchStatus)%2Caggregate(%24count%20as%20Count))",
            ODataQueryBuilder.BuildGroups("groupby((BatchStatus),aggregate($count as Count))", null));
        // GRID-004 (companion-headless backport): $top bounds the bucket fetch ($top applies after $apply
        // -- proven live in the origin repo); appended last, omitted entirely when null (above).
        Assert.AreEqual("?$apply=groupby((BatchStatus)%2Caggregate(%24count%20as%20Count))&$orderby=BatchStatus%20asc&$top=501",
            ODataQueryBuilder.BuildGroups("groupby((BatchStatus),aggregate($count as Count))", "BatchStatus asc", 501));
    }
}
