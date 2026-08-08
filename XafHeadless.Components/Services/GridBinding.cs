using System.Dynamic;
using System.Globalization;
using System.Text.Json;
using DevExpress.Blazor;
using DevExpress.Data.Filtering;
using XafHeadless.Components.Contracts;

namespace XafHeadless.Components.Services;

// Pure translation between ViewMetadata/ColumnMetadata (the model-driven contract from Tasks 3-6)
// and what DxGrid + OData need: flattened field names for ExpandoObject rows, $expand/$orderby
// strings, row materialization, and row-key extraction. No DevExpress.Blazor UI/component types
// here -- kept unit-testable without a Blazor test host (see GridBindingTests). The one exception,
// GridPersistentLayout (SanitizeLayoutForServerMode below), is a plain serializable data holder, not
// a rendered component, so it needs no test host either. DxGrid rendering itself is only verified
// via the browser smoke.
public static class GridBinding {
    // Columns actually rendered as grid columns -- "collection" columns are unsupported here (a
    // to-many nav property can't be a flat grid cell) and are always skipped, per the brief.
    public static IEnumerable<ColumnMetadata> VisibleColumns(IEnumerable<ColumnMetadata> columns) =>
        columns.Where(c => c.DataType != "collection" && c.DataType != "image");   // BUG-001: byte[] blobs aren't grid cells

    // THE seam for nested members. A column reaches a value across a nav property in one of two ways,
    // and every wire form below is derived from these segments so the two cannot drift apart:
    //  - the projector classified it as a lookup: Member + Lookup.DisplayMember
    //  - the model member is ITSELF a dotted path ("Customer.Name" -- XAF's own convention), which
    //    arrives UNclassified (Lookup == null, DataType "string"). Passing that dot through broke the
    //    column three ways: $orderby/$filter earned a 400 ("The child type 'Customer.Name' in a cast
    //    was not an entity type"), $expand never fetched the nav property, and the cell read
    //    row["Customer.Name"] -- a key no OData payload has -- so it rendered permanently blank.
    // A flat column yields a single segment, which keeps every function below byte-identical for it.
    // BUG-008: Lookup.DisplayMember is itself a PATH, not a single member name. XAF's
    // [XafDefaultProperty] often points at another reference (CustomerStore's is Emblem, an entity), so
    // the projector walks to a primitive and sends e.g. "Emblem.CityName". Splitting it here means the
    // field name, order path, expand and row materialization all follow the extra hop from one change.
    static string[] PathSegments(ColumnMetadata c) =>
        c.Lookup is { } lk ? [.. c.Member.Split('.'), .. lk.DisplayMember.Split('.')] : c.Member.Split('.');

    // ExpandoObject/dictionary key the grid column binds to. Nested columns are flattened to
    // "Member_DisplayMember" (e.g. "Customer_Name") rather than using DX's documented POCO nested
    // dot-path syntax ("Customer.Name") -- that syntax is only demonstrated for reflected POCOs in
    // dxdocs, not verified for ExpandoObject/IDictionary-backed rows, so this avoids relying on
    // unconfirmed nested-dictionary traversal.
    public static string FieldFor(ColumnMetadata c) => string.Join('_', PathSegments(c));

    // The real OData path for $orderby / $filter purposes (nested nav property), as opposed to the
    // flattened grid FieldName above. OData separates path segments with '/', never '.'.
    public static string OrderPathFor(ColumnMetadata c) => string.Join('/', PathSegments(c));

    // $expand covering every nested column, e.g. "Customer($select=Name),Project($select=Name)".
    // Returns null when there are no nested columns (OData query then omits $expand entirely).
    public static string? BuildExpand(IEnumerable<ColumnMetadata> columns) {
        var parts = VisibleColumns(columns).Select(PathSegments).Where(s => s.Length > 1)
            .Select(ExpandClause).ToList();
        return parts.Count == 0 ? null : string.Join(",", parts);
    }

    // "Customer","Name" -> Customer($select=Name); deeper paths nest, so a hop of any depth is
    // fetched rather than silently missing from the row.
    static string ExpandClause(string[] segments) => segments.Length == 2
        ? $"{segments[0]}($select={segments[1]})"
        : $"{segments[0]}($expand={ExpandClause(segments[1..])})";

    // Default $orderby from the view's own model-configured SortIndex/SortOrder columns.
    public static string? BuildDefaultOrder(IEnumerable<ColumnMetadata> columns) {
        var ordered = VisibleColumns(columns).Where(c => c.SortIndex is not null)
            .OrderBy(c => c.SortIndex)
            .Select(c => $"{OrderPathFor(c)} {(c.SortOrder == "descending" ? "desc" : "asc")}").ToList();
        return ordered.Count == 0 ? null : string.Join(",", ordered);
    }

    // Translates the grid's user-driven sort (flattened FieldName + direction, in the priority
    // order the grid reports) back to real OData paths. Falls back to the view's default order when
    // the user hasn't sorted anything.
    public static string? BuildOrderBy(IReadOnlyList<(string FieldName, bool Descending)> sortInfo,
            IReadOnlyDictionary<string, string> orderPathByField, string? defaultOrder) {
        if (sortInfo.Count == 0) return defaultOrder;
        return string.Join(",", sortInfo.Select(s =>
            $"{(orderPathByField.TryGetValue(s.FieldName, out var path) ? path : s.FieldName)} {(s.Descending ? "desc" : "asc")}"));
    }

    // Task 4.2 (GRID-001 hybrid binding): the routing decision. A view whose total server row count
    // exceeds the in-memory cap binds through the server-paged path (ODataGridDataSource); at or
    // below the cap it keeps the capped client-side load (zero behavior change for small views).
    public static bool UseServerMode(long totalCount, int rowCap) => totalCount > rowCap;

    // Task 4.3 rework of the Task-4.2 review fix: a GAP-008 layout persisted under DIFFERENT grouping
    // rules can carry GroupIndex on columns server mode can't group (enums, dates -- see
    // IsServerGroupable). Grouping itself is supported in server mode now ($apply=groupby via
    // ODataGridDataSource.GetGroupInfoAsync), so only the un-groupable columns get their GroupIndex
    // stripped before an old layout is applied. GridPersistentLayout and GridPersistentLayoutColumn
    // are both records with INIT-only properties (VERIFIED against the installed 26.1 assembly --
    // dxdocs' published "{ get; set; }" for Columns/GroupIndex is stale), so this returns a rebuilt
    // layout via `with` rather than mutating in place. Called from OnLayoutAutoLoading (server mode
    // only) and defensively from OnLayoutAutoSaving.
    public static GridPersistentLayout SanitizeLayoutForServerMode(GridPersistentLayout layout,
            IReadOnlySet<string> serverGroupableFields) =>
        // Columns can legally be null: a persisted blob round-trips through System.Text.Json with
        // WhenWritingDefault trimming, so a column-less layout deserializes to Columns == null.
        // Sanitizing it is a no-op -- crashing here killed the whole circuit inside
        // OnLayoutAutoLoading (live repro: XafHeadless backport E2E, 2026-07-14).
        layout.Columns is null ? layout : layout with {
            Columns = new GridPersistentLayoutCollection<GridPersistentLayoutColumn>(
                layout.Columns.Select(c => c.FieldName is { } f && serverGroupableFields.Contains(f)
                    ? c : c with { GroupIndex = null }))
        };

    // Drops SHAPING (sort + group) state, keeping column order/width and PageSize. Shaping the server
    // cannot honour is a visible, recoverable ceiling -- until LayoutAutoSaving persists it, at which
    // point the view reproduces the failure on EVERY subsequent load. Both kinds were hit live on
    // Order_ListView:
    //   sort  -- by Store, whose lookup display member Emblem is Edm.Binary: "The $orderby expression
    //            must evaluate to a single value of primitive type" (400)
    //   group -- by InvoiceNumber (55k distinct): EnforceGroupCeiling's NotSupportedException
    // Neither is predictable here: LookupMetadata projects no type for a display member, and cardinality
    // is a runtime property IsServerGroupable cannot see. So the ceiling is enforced after the fact --
    // the failing shaping does not outlive the click. Upgrade path: project the display member's data
    // type and refuse such a sort up front, the way the filter-row ceiling refuses enum/lookup columns.
    public static GridPersistentLayout StripShaping(GridPersistentLayout layout) =>
        layout.Columns is null ? layout : layout with {
            Columns = new GridPersistentLayoutCollection<GridPersistentLayoutColumn>(
                layout.Columns.Select(c => c with { SortIndex = null, SortOrder = null, GroupIndex = null }))
        };

    // GridPersistentLayout.PageSize is `int?` carrying [JsonIgnore(WhenWritingDefault)] (dxdocs, 26.1),
    // so a null PageSize is omitted from the persisted blob and deserializes back as null. Applying
    // THAT layout resets DxGrid.PageSize to its documented default of 10, silently overriding the value
    // the markup set -- live repro: Order_ListView (the only view with persisted prefs) served 10 rows
    // per page while every unpersisted view served 25. Refill the markup value when the blob carries
    // none; a persisted user choice still wins.
    public static GridPersistentLayout RestorePageSize(GridPersistentLayout layout, int markupPageSize) =>
        layout.PageSize is null ? layout with { PageSize = markupPageSize } : layout;

    // FieldName -> real OData path for every rendered column, for $orderby translation (sorting a
    // lookup column sorts by its expanded display member, e.g. Customer_Name -> Customer/Name).
    public static Dictionary<string, string> BuildOrderPathMap(IEnumerable<ColumnMetadata> columns) =>
        VisibleColumns(columns).ToDictionary(FieldFor, OrderPathFor);

    // Server-mode filter-ROW ceiling (GAP-005 decision, carried into Task 4.2): SCALAR columns only.
    // An enum cell holds its caption and a lookup cell its flattened display string -- neither matches
    // the OData property's literal representation, so translating their filter-row input would
    // silently emit a WRONG filter. XafListView disables those columns' filter cells in server mode.
    // GRID-004 (companion-headless backport, review I1 there): "dateonly" (DateOnly member -> Edm.Date on
    // the wire) joins the ceiling -- the translator only emits DateTimeOffset instant literals,
    // which Edm.Date rejects (400) or day-shifts; excluded here rather than guessed at, until
    // bare-date literals are a real ask.
    public static bool IsServerFilterable(ColumnMetadata c) =>
        c.Lookup is null && c.DataType is not ("enum" or "dateonly");

    // Task 4.3 (GRID-001): the server-GROUPING ceiling -- what $apply=groupby provably round-trips
    // (live probes in the companion headless implementation): non-date scalars (string/int/decimal/bool buckets come back
    // as matching JSON literals) and lookups (nav display path, e.g. groupby((Customer/Name)) -- the
    // bucket value IS the cell's display string, and the child $filter Customer/Name eq '...' returns
    // exactly the bucket count). Excluded: dates (bucket = ISO-offset STRING; quoting it back at
    // Edm.DateTimeOffset would be a wrong literal, and raw-timestamp buckets are useless without
    // interval grouping, which remote sources don't support) and enums (group headers would show raw
    // values while cells show captions; the origin repo had no enum column in its OData set to probe).
    // GRID-005: the server-SORT ceiling. A lookup column sorts and groups by its display PATH
    // (Store/Emblem), so it is the DISPLAY member -- not the column -- that decides whether OData can
    // order by it. $orderby must land on a PRIMITIVE; a display member that is itself an entity
    // reference, a blob or a collection is not one, and earns "The $orderby expression must evaluate to
    // a single value of primitive type" (400). That type now reaches us as Lookup.DisplayDataType, so
    // the column simply never offers the sort (XafListView binds AllowSort to this in server mode), the
    // way the filter row already refuses enum/lookup columns.
    //
    // The live case is a lookup-of-a-lookup, NOT a blob (BUG-006 recorded this wrongly): CustomerStore
    // carries [XafDefaultProperty(nameof(Emblem))] and Emblem is a reference to the Emblem ENTITY
    // (HasOne/WithMany), so Store's display path resolves to a navigation property. Any entity whose
    // default property is a reference hits this, which makes it a good deal more common than a blob.
    //
    // A NULL DisplayDataType means the host predates the field -- unknown, not unsortable -- so sorting
    // every lookup against an older Api keeps working. BUG-005's StripShaping stays the backstop either
    // way: AllowSort=false stops the header CLICK, but SortIndex/SortBy still sort in code (dxdocs,
    // DxGridDataColumn.AllowSort), so a layout persisted before this ceiling existed can still re-apply
    // a sort we cannot serve.
    public static bool IsServerSortable(ColumnMetadata c) =>
        c.Lookup?.DisplayDataType is not ("lookup" or "image" or "collection");

    public static bool IsServerGroupable(ColumnMetadata c) =>
        IsServerSortable(c)
        && (c.Lookup is not null || (IsServerFilterable(c) && c.DataType is not ("date" or "collection" or "image")));
    public static Dictionary<string, string> BuildGroupPathMap(IEnumerable<ColumnMetadata> columns) =>
        VisibleColumns(columns).Where(IsServerGroupable).ToDictionary(FieldFor, OrderPathFor);

    // The translate map for EVERY CriteriaOperator reaching ODataGridDataSource. Criteria arrive from
    // two sources: the filter row (IsServerFilterable columns -- others render disabled cells) and
    // group-expand criteria the grid AND-bakes into options.FilterCriteria (IsServerGroupable columns,
    // verified against installed 26.1 source: GridCustomDataSourceOptions..ctor). A FieldName absent
    // from this map makes ODataFilterTranslator skip the clause entirely rather than guess.
    public static Dictionary<string, string> BuildCriteriaPathMap(IEnumerable<ColumnMetadata> columns) =>
        VisibleColumns(columns).Where(c => IsServerFilterable(c) || IsServerGroupable(c))
            .ToDictionary(FieldFor, OrderPathFor);

    // GRID-004 (companion-headless backport): the filter-row DATE editor's criteria. DxGrid's own filter
    // row types a column by SNIFFING loaded row values (verified 26.1 source: DataItemReader.
    // UpdateDescriptor -> ExpandoPropertyDescriptor), and date cells used to materialize as ISO
    // STRINGS -- so a date column was ALWAYS a text column, whose string criteria
    // (contains/eq 'text') the server 400s against Edm.DateTimeOffset, and the unhandled 400 killed
    // the whole Blazor circuit (live repro in the origin repo). XafListView therefore renders its
    // OWN DxDateEdit for date columns (ViewMetadata DECLARES the type; nothing is sniffed) and sets
    // the column's FilterCriteria directly to the same [day, next-day) rounded range DxGrid builds
    // for real DateTime columns (verified 26.1 source: GridFilterUtils.
    // CreateDateOrTimeRoundingRangeFilterCriteria).
    public static CriteriaOperator? BuildDayRangeCriteria(string fieldName, DateTime? day) =>
        day is { } d
            ? new GroupOperator(GroupOperatorType.And,
                new BinaryOperator(fieldName, d.Date, BinaryOperatorType.GreaterOrEqual),
                new BinaryOperator(fieldName, d.Date.AddDays(1), BinaryOperatorType.Less))
            : null;

    // Inverse of BuildDayRangeCriteria: the editor re-renders its current value from the grid's
    // active criteria for the column (any other shape -- e.g. cleared, or a future programmatic
    // filter that merely STARTS with a ge-DateTime operand -- renders empty rather than claiming a
    // day filter it doesn't represent). Validates the full shape BuildDayRangeCriteria emits:
    // ge day AND lt next-day, both on the same field.
    public static DateTime? ExtractDayFromCriteria(CriteriaOperator? criteria) {
        if (criteria is not GroupOperator { OperatorType: GroupOperatorType.And } g || g.Operands.Count != 2) return null;
        if (g.Operands[0] is not BinaryOperator {
                OperatorType: BinaryOperatorType.GreaterOrEqual,
                LeftOperand: OperandProperty { PropertyName: { } field },
                RightOperand: OperandValue { Value: DateTime d }
            }) return null;
        return g.Operands[1] is BinaryOperator {
            OperatorType: BinaryOperatorType.Less,
            LeftOperand: OperandProperty { PropertyName: { } field2 },
            RightOperand: OperandValue { Value: DateTime next }
        } && field2 == field && next == d.Date.AddDays(1) ? d.Date : null;
    }

    // The $apply transformation chain for one grouping level: optional filter() (master filter +
    // filter row + PARENT group criteria, all pre-translated) then groupby with a $count aggregate.
    // Exact shape proven live in the companion headless implementation (sub-second at Batch = 571k rows).
    public static string BuildGroupApply(string groupPath, string? filter) {
        var groupby = $"groupby(({groupPath}),aggregate($count as Count))";
        return filter is null ? groupby : $"filter({filter})/{groupby}";
    }

    // GRID-004 (companion-headless backport): a groupable HIGH-CARDINALITY scalar column would return an
    // unusable number of server-side buckets. IsServerGroupable can't tell cardinality ahead of
    // time, so ODataGridDataSource.GetGroupInfoAsync bounds the FETCH itself ($top=MaxServerGroups+1
    // after $apply -- $top applies to the post-apply set, proven live in the origin repo at 571k
    // distinct buckets: 129 bytes / 21 ms) and fails loud here on the one extra bucket
    // (NotSupportedException -> GridCustomDataSource.ExceptionHandler) rather than rendering
    // thousands of group headers. The true count is unknowable by design -- the message says
    // "more than", not an exact number. ponytail: fixed ceiling; make per-view configurable if a
    // legitimate >MaxServerGroups grouping ever appears.
    public const int MaxServerGroups = 500;
    public static void EnforceGroupCeiling(int bucketCount, string fieldName) {
        if (bucketCount > MaxServerGroups)
            throw new NotSupportedException(
                $"Grouping by '{fieldName}' produced more than {MaxServerGroups} groups. " +
                "Refine the filter or group by a lower-cardinality column.");
    }

    // One $apply=groupby response row -> (group value, group row count). Scalar paths are flat
    // ({"BatchStatus":"P","Count":9120}); lookup nav paths come back NESTED
    // ({"CatalogueNavigation":{"Name":"COM"},"Count":16192}) -- walk the segments. A null/missing
    // nav object is the null-value bucket.
    public static (object? Value, int Count) ParseGroupBucket(JsonElement bucket, string groupPath) {
        var count = bucket.GetProperty("Count").GetInt32();
        var el = bucket;
        var segments = groupPath.Split('/');
        for (var i = 0; i < segments.Length - 1; i++) {
            if (!el.TryGetProperty(segments[i], out var next) || next.ValueKind != JsonValueKind.Object)
                return (null, count);
            el = next;
        }
        return (RawValue(el, segments[^1]), count);
    }

    // Master-detail filter: "{MasterKeyMember}/{keyOfMaster} eq {MasterKey}", filtering the child on
    // the related master's key. keyOfMaster is the MASTER type's own key member. It is resolved in
    // priority order: (1) masterKeyName, when the parent DetailView supplies it explicitly (its
    // ViewMetadata.KeyMember -- the authoritative source, needed for Order->OrderItem where the master
    // key "ID" is a Guid that never appears as a child column); (2) the child view's own Lookup
    // metadata for the master-nav column, when present (holds for Child->Parent, both "ParentKey");
    // (3) same-name fallback. MasterKey is emitted unquoted -- valid for numeric keys AND OData v4 Guid
    // literals (e.g. "Order/ID eq 771e968e-..."); a string-typed master key would need quoting.
    public static string? BuildMasterFilter(IEnumerable<ColumnMetadata>? columns, string? masterKeyMember,
            string? masterKey, string? masterKeyName = null) {
        if (masterKeyMember is null || masterKey is null) return null;
        var keyOfMaster = masterKeyName
            ?? columns?.FirstOrDefault(c => c.Member == masterKeyMember)?.Lookup?.KeyMember
            ?? masterKeyMember;
        return $"{masterKeyMember}/{keyOfMaster} eq {masterKey}";
    }

    // PH2-001: the view's own key member is now carried explicitly by ViewMetadata.KeyMember
    // (projected server-side from TypeInfo.KeyMember). The former InferKeyMember convention-guess
    // ("{ObjectType}Number" / "Oid") is gone -- consumers read meta.KeyMember directly.

    // Row-key extraction for OnRowSelected: pulls keyMember's value out of the materialized row
    // dictionary and stringifies it. Returns null (caller should no-op) if the key can't be resolved.
    public static string? ExtractRowKey(IDictionary<string, object?> row, string? keyMember) =>
        keyMember is not null && row.TryGetValue(keyMember, out var v) && v is not null
            ? v.ToString() : null;

    // JsonElement row -> flat ExpandoObject the grid can bind to (one dictionary key per visible
    // column, keyed by FieldFor(c)). Enum columns resolve to their caption (client-side dictionary
    // from metadata); lookup columns flatten to the expanded DisplayMember; everything else copies
    // the raw scalar. Missing JSON properties (a ListView column that isn't a real OData EDM
    // property -- e.g. a model-only member, verified via $metadata) become null
    // instead of throwing, since XafListView does not restrict $select (see ODataGridDataSource).
    public static IDictionary<string, object?> MaterializeRow(JsonElement row,
            IEnumerable<ColumnMetadata> columns, string? keyMember = null,
            IEnumerable<string>? appearanceMembers = null) {
        IDictionary<string, object?> result = new ExpandoObject();
        foreach (var c in VisibleColumns(columns)) {
            // Walk any nav hops first (a lookup or a dotted member -- see PathSegments); the readers
            // below then work on the owning object exactly as they always did for a flat column.
            if (!TryWalk(row, PathSegments(c), out var owner, out var member)) {
                result[FieldFor(c)] = null;
                continue;
            }
            result[FieldFor(c)] = c.DataType == "enum" ? EnumCaption(owner, member, c.Enum)
                : c.DataType == "date" ? DateValue(owner, member)
                : RawValue(owner, member);
        }
        // Ensure the grid's key member is present even when it isn't a displayed column -- e.g. Order's
        // Guid "ID" is the key but not a Order_ListView column. DxGrid.KeyFieldName and OnRowSelected
        // (ExtractRowKey) both need it; OData returns it in the raw row (no $select), it's just not
        // among the visible columns. (For the POC entity the key WAS a visible column, so this was a no-op.)
        if (keyMember is not null && !result.ContainsKey(keyMember))
            result[keyMember] = RawValue(row, keyMember);
        // GAP-002: carry members that appearance-rule criteria reference but that aren't displayed columns
        // (e.g. Evaluation.Rating -- the green Rating='Good' rule colors the whole row but Rating is not a
        // Employee_Evaluations_ListView column). OData returns full entities (no $select), so the raw row
        // has them. Enum members arrive as their NAME -- the exact form the criteria literal compares to.
        // Adding the key (even null when absent) also keeps AppearanceEvaluator's Fit from throwing.
        if (appearanceMembers is not null)
            foreach (var m in appearanceMembers)
                if (!result.ContainsKey(m)) result[m] = RawValue(row, m);
        return result;
    }

    // GRID-004 (companion-headless backport): date columns materialize as typed DateTime (the wire's own
    // wall time, offset dropped) instead of the raw ISO string -- cells render formatted dates, and
    // in-memory mode evaluates the date filter cell's DateTime range criteria against real DateTime
    // values. "dateonly" columns deliberately do NOT come here (they keep the bare wire string via
    // RawValue -- no spurious midnight time). Unparseable/absent values fall back to the raw string.
    static object? DateValue(JsonElement row, string member) {
        if (!row.TryGetProperty(member, out var el) || el.ValueKind != JsonValueKind.String) return null;
        return DateTimeOffset.TryParse(el.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto)
            ? dto.DateTime : el.GetString();
    }

    // Follows every segment but the last, leaving the object that OWNS the value and the member to read
    // from it. A single-segment (flat) path returns the row itself unchanged. A missing or non-object
    // hop returns false, so the cell renders empty instead of throwing -- the same contract absent
    // members have always had here. (This replaced LookupDisplay: a lookup is just a one-hop walk.)
    static bool TryWalk(JsonElement row, string[] segments, out JsonElement owner, out string member) {
        owner = row;
        member = segments[^1];
        foreach (var hop in segments[..^1]) {
            if (!owner.TryGetProperty(hop, out var next) || next.ValueKind != JsonValueKind.Object) return false;
            owner = next;
        }
        return true;
    }

    static object? EnumCaption(JsonElement owner, string member, IReadOnlyList<EnumValueMetadata>? values) {
        var raw = RawValue(owner, member);
        if (raw is null || values is null) return raw;
        var text = EnumValueCanon.Canonicalize(raw);
        return values.FirstOrDefault(e => EnumValueCanon.Canonicalize(e.Value) == text)?.Caption ?? raw;
    }

    static object? RawValue(JsonElement row, string member) {
        if (!row.TryGetProperty(member, out var el) || el.ValueKind == JsonValueKind.Null) return null;
        return el.ValueKind switch {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.GetDecimal(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => el.GetRawText()
        };
    }
}
