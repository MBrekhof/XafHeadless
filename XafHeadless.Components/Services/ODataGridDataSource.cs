using System.Collections;
using System.Dynamic;
using DevExpress.Blazor;
using XafHeadless.Components.Contracts;

namespace XafHeadless.Components.Services;

// Task 4.2 (GRID-001 hybrid binding): the LIVE server-mode data source. XafListView routes here for
// every view whose $count probe exceeds RowCap (Order = 55k here; proven at Batch = 571k in the
// companion headless implementation) -- DxGrid pulls
// pages on demand through GetItemsAsync/GetItemCountAsync ($top/$skip/$orderby; filter row ->
// ODataFilterTranslator -> $filter, scalar columns only, GridBinding.IsServerFilterable). Task 4.3
// added server-side GROUPING: GetGroupInfoAsync answers each grouping level with one
// $apply=[filter(...)/]groupby((path),aggregate($count as Count)) fetch (task-4.1/4.3 probe
// evidence: sub-second at 571k rows, buckets sum exactly to the filtered total), restricted to
// GridBinding.IsServerGroupable columns (XafListView sets DxGridDataColumn.AllowGroup to the same
// ceiling, so the grid can't request anything else). Views at or below RowCap instead take the
// unchanged capped in-memory bind, where DxGrid groups/sorts/filters client-side.
//
// GridCustomDataSource is the documented, WASM-compatible path for binding DxGrid to a
// server-paged/sorted data source (dxdocs docs.devexpress.com/Blazor/DevExpress.Blazor.GridCustomDataSource,
// which links an explicit "Bind the Grid to OData Data Source" example built for a Blazor
// WebAssembly app). Server Mode data sources (ODataServerModeSource, XPServerModeView,
// GridDevExtremeDataSource<T>) are documented as NOT working in Blazor WebAssembly (per the full
// dxdocs findings); this is the primary path, not the manual-paging fallback the brief allowed.
//
// GAP-005: options.FilterCriteria (the DxGrid filter row's active filter, a CriteriaOperator) is
// translated via ODataFilterTranslator and AND-combined with the master-detail filter baked in at
// construction (GridBinding.BuildMasterFilter), in BOTH GetItemsAsync and GetItemCountAsync -- the
// count must reflect the same filter as the rows, or paging breaks. Task 4.3: options.FilterCriteria
// ALSO carries the group-expand criteria (the grid AND-bakes ParentGroupInfo into it as
// field-eq-value operators -- verified against installed 26.1 source, GridCustomDataSourceOptions
// ..ctor), which is why the translate map is criteriaPathByField (filterable + groupable columns,
// GridBinding.BuildCriteriaPathMap): expanding a group pages its children through the SAME
// GetItemsAsync path with zero extra plumbing, at any grouping depth.
public class ODataGridDataSource(ApiClient api, string entity, string? filter, string? expand,
        string? defaultOrder, IReadOnlyDictionary<string, string> orderPathByField,
        IReadOnlyDictionary<string, string> criteriaPathByField,
        IReadOnlyDictionary<string, string> groupPathByField,
        List<ColumnMetadata> columns, string? keyMember,
        IReadOnlyCollection<string>? appearanceMembers = null) : GridCustomDataSource {
    // ExpandoObject is what MaterializeRow produces; declaring this upfront avoids the extra
    // "probe" GetItemsAsync call the docs say GridCustomDataSource otherwise issues to sniff the
    // item type during initialization.
    protected override Type DataItemType => typeof(ExpandoObject);

    public override async Task<int> GetItemCountAsync(GridCustomDataSourceCountOptions options, CancellationToken cancellationToken) {
        var combinedFilter = CombinedFilter(options.FilterCriteria);
        // Select is always null here -- $select is never sent at all, see GridBinding.MaterializeRow's
        // comment for why (ListView columns can name properties that don't exist in the OData EDM).
        var page = await api.GetPageAsync(entity, new ODataQuery(0, 1, null, combinedFilter, null, null), cancellationToken);
        return (int)page.Total;
    }

    public override async Task<IList> GetItemsAsync(GridCustomDataSourceItemsOptions options, CancellationToken cancellationToken) {
        var sortInfo = options.SortInfo?.Select(s => (s.FieldName, s.DescendingSortOrder)).ToList()
            ?? new List<(string FieldName, bool DescendingSortOrder)>();
        var orderBy = GridBinding.BuildOrderBy(sortInfo, orderPathByField, defaultOrder);
        var combinedFilter = CombinedFilter(options.FilterCriteria);
        // Select is always null here -- see GetItemCountAsync above / GridBinding.MaterializeRow.
        var page = await api.GetPageAsync(entity,
            new ODataQuery(options.StartIndex, options.Count, orderBy, combinedFilter, null, expand), cancellationToken);
        return page.Rows.Select(r => GridBinding.MaterializeRow(r, columns, keyMember, appearanceMembers)).ToList();
    }

    // Task 4.3 (GRID-001): one grouping level = one $apply fetch. The grid calls this once for the
    // root level and once per EXPANDED parent group at deeper levels (its ParentGroupInfo criteria
    // arrive pre-baked into options.FilterCriteria, see class remarks), so multi-level grouping is
    // level-wise on demand -- never a cross-join of all levels.
    public override async Task<IList<GridCustomDataSourceGroupInfo>> GetGroupInfoAsync(
            GridCustomDataSourceGroupingOptions options, CancellationToken cancellationToken) {
        // AllowGroup restricts the UI to groupable columns; anything else here is a bug -- surface it
        // (lands in GridCustomDataSource.ExceptionHandler) instead of serving wrong buckets.
        if (!groupPathByField.TryGetValue(options.FieldName, out var path))
            throw new NotSupportedException($"Column '{options.FieldName}' is not server-groupable.");
        var apply = GridBinding.BuildGroupApply(path, CombinedFilter(options.FilterCriteria));
        var orderBy = $"{path} {(options.DescendingSortOrder ? "desc" : "asc")}";
        // GRID-004 (companion-headless backport): $top bounds the FETCH itself ($top applies after $apply --
        // proven live in the origin repo), so a high-cardinality grouping transfers at most ceiling+1
        // buckets instead of all of them; one extra bucket is the over-the-ceiling signal
        // EnforceGroupCeiling fails loud on.
        var buckets = await api.GetGroupsAsync(entity, apply, orderBy,
            GridBinding.MaxServerGroups + 1, cancellationToken);
        GridBinding.EnforceGroupCeiling(buckets.Length, options.FieldName);
        var groups = new List<GridCustomDataSourceGroupInfo>(buckets.Length);
        foreach (var bucket in buckets) {
            var (value, count) = GridBinding.ParseGroupBucket(bucket, path);
            groups.Add(new GridCustomDataSourceGroupInfo {
                Value = value, DataItemCount = count,
                SummaryValues = BuildSummaryValues(options.SummaryInfo, count),
            });
        }
        return groups;
    }

    // XafListView declares exactly one group summary (Count) -- served from the SAME bucket count,
    // index-aligned with options.SummaryInfo as the grid expects (verified 26.1 source:
    // GroupNodeOperations.CreateNodeSummary zips the two by position). Any other summary type would
    // need its own aggregate() clause -- not built until asked for; throw rather than serve nulls.
    static IList? BuildSummaryValues(IReadOnlyList<GridCustomDataSourceSummaryInfo>? summaryInfo, int count) {
        if (summaryInfo is not { Count: > 0 }) return null;
        var values = new List<object>(summaryInfo.Count);
        foreach (var info in summaryInfo)
            values.Add(info.SummaryType == GridSummaryItemType.Count ? count
                : throw new NotSupportedException($"Group summary '{info.SummaryType}' is not supported in server mode."));
        return values;
    }

    string? CombinedFilter(DevExpress.Data.Filtering.CriteriaOperator? filterCriteria) =>
        ODataFilterTranslator.Combine(filter, ODataFilterTranslator.Translate(filterCriteria, criteriaPathByField));
}
