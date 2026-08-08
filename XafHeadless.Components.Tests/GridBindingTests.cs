using System.Text.Json;
using DevExpress.Blazor;
using DevExpress.Data.Filtering;
using XafHeadless.Components.Contracts;
using XafHeadless.Components.Services;

namespace XafHeadless.Components.Tests;

[TestClass]
public class GridBindingTests {
    static ColumnMetadata Plain(string member, string dataType = "string", int? sortIndex = null, string? sortOrder = null) =>
        new(member, member, dataType, sortIndex, sortOrder, null, null);
    static ColumnMetadata Lookup(string member, string objectType, string keyMember, string displayMember) =>
        new(member, member, "lookup", null, null, new LookupMetadata(objectType, keyMember, displayMember), null);
    static ColumnMetadata Enum(string member, params (long Value, string Caption)[] values) =>
        new(member, member, "enum", null, null, null, values.Select(v => new EnumValueMetadata((object)v.Value, v.Caption)).ToList());

    [TestMethod]
    public void VisibleColumns_skips_collection() {
        var columns = new[] { Plain("Name"), Plain("Children", "collection") };
        CollectionAssert.AreEqual(new[] { "Name" }, GridBinding.VisibleColumns(columns).Select(c => c.Member).ToList());
    }

    [TestMethod]
    public void FieldFor_flattens_lookup_columns() {
        Assert.AreEqual("ItemNumber", GridBinding.FieldFor(Plain("ItemNumber", "int")));
        Assert.AreEqual("Customer_Name", GridBinding.FieldFor(Lookup("Customer", "Customer", "Name", "Name")));
        Assert.AreEqual("Contract_ContractQuoteId", GridBinding.FieldFor(Lookup("Contract", "ContractQuote", "ContractQuoteNo", "ContractQuoteId")));
    }

    [TestMethod]
    public void OrderPathFor_uses_real_nav_path_for_lookups() {
        Assert.AreEqual("Status", GridBinding.OrderPathFor(Plain("Status")));
        Assert.AreEqual("Customer/Name", GridBinding.OrderPathFor(Lookup("Customer", "Customer", "Name", "Name")));
    }

    [TestMethod]
    public void BuildExpand_joins_lookup_columns_and_skips_collections_and_plain() {
        var columns = new[] { Plain("Status"), Lookup("Customer", "Customer", "Name", "Name"),
            Lookup("Project", "Project", "Name", "Name"), Plain("Children", "collection") };
        Assert.AreEqual("Customer($select=Name),Project($select=Name)", GridBinding.BuildExpand(columns));
    }

    [TestMethod]
    public void BuildExpand_returns_null_when_no_lookups() =>
        Assert.IsNull(GridBinding.BuildExpand(new[] { Plain("Status") }));

    [TestMethod]
    public void BuildDefaultOrder_orders_by_SortIndex_and_maps_direction() {
        var columns = new[] {
            Plain("City", sortIndex: 1, sortOrder: "descending"),
            Plain("Country", sortIndex: 0, sortOrder: "ascending"),
            Plain("Untouched"),
        };
        Assert.AreEqual("Country asc,City desc", GridBinding.BuildDefaultOrder(columns));
    }

    [TestMethod]
    public void BuildDefaultOrder_returns_null_when_no_column_has_SortIndex() =>
        Assert.IsNull(GridBinding.BuildDefaultOrder(new[] { Plain("Status") }));

    [TestMethod]
    public void BuildOrderBy_translates_flattened_field_names_to_real_paths_preserving_priority() {
        var map = new Dictionary<string, string> { ["Customer_Name"] = "Customer/Name", ["Status"] = "Status" };
        var sortInfo = new List<(string, bool)> { ("Status", false), ("Customer_Name", true) };
        Assert.AreEqual("Status asc,Customer/Name desc", GridBinding.BuildOrderBy(sortInfo, map, "Fallback asc"));
    }

    [TestMethod]
    public void BuildOrderBy_falls_back_to_default_when_grid_has_no_sort() =>
        Assert.AreEqual("Fallback asc", GridBinding.BuildOrderBy(new List<(string, bool)>(), new Dictionary<string, string>(), "Fallback asc"));

    [TestMethod]
    public void BuildMasterFilter_derives_key_of_master_from_child_view_lookup_metadata() {
        // Result_ListView's own "ParentKey" column, verified live against the API.
        var resultColumns = new[] { Lookup("ParentKey", "Parent", "ParentKey", "ParentKey"), Plain("Status") };
        Assert.AreEqual("ParentKey/ParentKey eq 1665361", GridBinding.BuildMasterFilter(resultColumns, "ParentKey", "1665361"));
    }

    [TestMethod]
    public void BuildMasterFilter_falls_back_to_same_name_when_column_not_present() =>
        Assert.AreEqual("ParentKey/ParentKey eq 42", GridBinding.BuildMasterFilter(null, "ParentKey", "42"));

    [TestMethod]
    public void BuildMasterFilter_uses_explicit_master_key_name_over_heuristic() {
        // Order->OrderItem: the master (Order) key "ID" is a Guid that never appears as a child column,
        // so the DetailView supplies it explicitly. Emitted unquoted (valid OData v4 Guid literal).
        var guid = "771e968e-556e-48b7-955b-fe8cf6176477";
        Assert.AreEqual($"Order/ID eq {guid}",
            GridBinding.BuildMasterFilter(null, "Order", guid, masterKeyName: "ID"));
    }

    [TestMethod]
    public void BuildMasterFilter_returns_null_when_no_master_specified() =>
        Assert.IsNull(GridBinding.BuildMasterFilter(new[] { Plain("Status") }, null, null));

    // PH2-001: GridBinding.InferKeyMember was deleted -- the view's key member now comes from the
    // server-projected ViewMetadata.KeyMember, so there is no client-side guessing logic left to test.
    // (Two tests removed here; the KeyMember projection itself is covered by the API metadata tests.)

    // ---- Task 4.2 (GRID-001 hybrid binding): routing decision + server-mode path maps ----

    [TestMethod]
    public void UseServerMode_routes_to_server_only_above_the_row_cap() {
        Assert.IsFalse(GridBinding.UseServerMode(0, 5000));
        Assert.IsFalse(GridBinding.UseServerMode(4999, 5000));
        Assert.IsFalse(GridBinding.UseServerMode(5000, 5000));   // at the cap -> in-memory, unchanged
        Assert.IsTrue(GridBinding.UseServerMode(5001, 5000));
        Assert.IsTrue(GridBinding.UseServerMode(571_146, 5000)); // Batch
    }

    [TestMethod]
    public void SanitizeLayoutForServerMode_strips_GroupIndex_only_for_non_groupable_columns() {
        // Task 4.3 semantics: grouping is no longer forbidden in server mode (GetGroupInfoAsync serves
        // it via $apply=groupby) -- a persisted layout may keep GroupIndex on server-groupable columns;
        // only columns OUTSIDE the groupable ceiling (enums, dates, unknown/null fields) get stripped.
        var layout = new GridPersistentLayout {
            Columns = new GridPersistentLayoutCollection<GridPersistentLayoutColumn>([
                new GridPersistentLayoutColumn { FieldName = "Status", GroupIndex = 0 },        // enum -> strip
                new GridPersistentLayoutColumn { FieldName = "Customer_Name", GroupIndex = 1 }, // lookup -> keep
                new GridPersistentLayoutColumn { FieldName = "Amount", GroupIndex = null },     // ungrouped stays
                new GridPersistentLayoutColumn { FieldName = "Name", GroupIndex = 2 },          // scalar -> keep
                new GridPersistentLayoutColumn { GroupIndex = 3 },                              // no field -> strip
            ]),
        };
        var groupable = new HashSet<string> { "Customer_Name", "Name", "Amount" };

        var sanitized = GridBinding.SanitizeLayoutForServerMode(layout, groupable);

        Assert.IsNull(sanitized.Columns[0].GroupIndex);
        Assert.AreEqual(1, sanitized.Columns[1].GroupIndex);
        Assert.IsNull(sanitized.Columns[2].GroupIndex);
        Assert.AreEqual(2, sanitized.Columns[3].GroupIndex);
        Assert.IsNull(sanitized.Columns[4].GroupIndex);
    }

    [TestMethod]
    public void SanitizeLayoutForServerMode_tolerates_a_layout_without_columns() {
        // Live crash (XafHeadless backport E2E, 2026-07-14): a persisted pref blob can legally
        // deserialize with Columns == null (System.Text.Json + the record's WhenWritingDefault
        // trimming) -- sanitizing it must be a no-op, not an ArgumentNullException that kills the
        // whole Blazor circuit inside OnLayoutAutoLoading.
        var layout = new GridPersistentLayout { PageIndex = 1 };

        var sanitized = GridBinding.SanitizeLayoutForServerMode(layout, new HashSet<string> { "Name" });

        Assert.AreEqual(1, sanitized.PageIndex);
        Assert.IsNull(sanitized.Columns);
    }

    [TestMethod]
    public void BuildOrderPathMap_covers_every_rendered_column_with_its_odata_path() {
        var columns = new[] { Plain("Name"), Lookup("Customer", "Customer", "Name", "Name"),
            Enum("Status", (1, "Draft")), Plain("Results", "collection") };
        var map = GridBinding.BuildOrderPathMap(columns);
        Assert.HasCount(3, map);   // the collection column is never rendered, so never sorted
        Assert.AreEqual("Name", map["Name"]);
        Assert.AreEqual("Customer/Name", map["Customer_Name"]);   // sort a lookup by its display member
        Assert.AreEqual("Status", map["Status"]);
    }

    [TestMethod]
    public void BuildCriteriaPathMap_covers_filter_row_scalars_plus_groupable_lookups() {
        // Task 4.3: criteria reaching ODataGridDataSource come from TWO sources now -- the filter row
        // (scalar columns only; enum/lookup cells stay disabled) and group-expand criteria
        // (server-groupable columns, incl. lookups by nav display path). One map serves both.
        var columns = new[] { Plain("Name"), Plain("Amount", "decimal"),
            Lookup("Customer", "Customer", "Name", "Name"), Enum("Status", (1, "Draft")),
            Plain("Results", "collection"), Plain("Photo", "image") };
        var map = GridBinding.BuildCriteriaPathMap(columns);
        Assert.HasCount(3, map);
        Assert.AreEqual("Name", map["Name"]);
        Assert.AreEqual("Amount", map["Amount"]);
        Assert.AreEqual("Customer/Name", map["Customer_Name"]);   // group-expand: Customer/Name eq '...'
        // Enum cells hold captions -- translating them would silently emit a WRONG $filter, and they
        // are not server-groupable either, so they stay absent (the translator skips the clause).
        Assert.IsFalse(map.ContainsKey("Status"));
    }

    [TestMethod]
    public void CriteriaPathMap_enforces_the_translator_ceiling_end_to_end() {
        var map = GridBinding.BuildCriteriaPathMap(new[] { Plain("Name"), Enum("Status", (1, "Draft")),
            Lookup("Customer", "Customer", "Name", "Name") });
        var scalar = new FunctionOperator(FunctionOperatorType.Contains, new OperandProperty("Name"), "abc");
        var viaEnum = new BinaryOperator("Status", "Draft", BinaryOperatorType.Equal);
        // What the grid bakes into options.FilterCriteria when a Customer_Name group row is expanded
        // (GridCustomDataSourceOptions AND-combines ParentGroupInfo criteria -- verified 26.1 source).
        var groupExpand = new GroupOperator(GroupOperatorType.And,
            new BinaryOperator("Customer_Name", "Acme", BinaryOperatorType.Equal), scalar);
        Assert.AreEqual("contains(Name,'abc')", ODataFilterTranslator.Translate(scalar, map));
        Assert.IsNull(ODataFilterTranslator.Translate(viaEnum, map));
        Assert.AreEqual("(Customer/Name eq 'Acme' and contains(Name,'abc'))",
            ODataFilterTranslator.Translate(groupExpand, map));
    }

    // ---- Task 4.3 (GRID-001 server-side grouping): groupable ceiling + $apply translation ----

    [TestMethod]
    public void IsServerGroupable_allows_non_date_scalars_and_lookups_only() {
        Assert.IsTrue(GridBinding.IsServerGroupable(Plain("Name")));                    // string (probe A/B/F)
        Assert.IsTrue(GridBinding.IsServerGroupable(Plain("TemplateVersion", "int"))); // numeric (probe C)
        Assert.IsTrue(GridBinding.IsServerGroupable(Plain("Flag", "bool")));
        Assert.IsTrue(GridBinding.IsServerGroupable(Lookup("Customer", "Customer", "Name", "Name"))); // nav path (probes D/H/J)
        // Date buckets arrive as ISO-offset strings; a quoted string literal can't honestly round-trip
        // into the child $filter against Edm.DateTimeOffset, and raw-timestamp buckets are useless
        // without interval grouping (unsupported on remote sources) -- excluded.
        Assert.IsFalse(GridBinding.IsServerGroupable(Plain("DateCreated", "date")));
        // Enum group headers would show raw values while cells show captions; literal round-trip
        // unproven (no enum column in the OData set to probe) -- excluded.
        Assert.IsFalse(GridBinding.IsServerGroupable(Enum("Status", (1, "Draft"))));
    }

    [TestMethod]
    public void BuildGroupPathMap_maps_groupable_rendered_columns_to_odata_paths() {
        var columns = new[] { Plain("Name"), Lookup("Customer", "Customer", "Name", "Name"),
            Enum("Status", (1, "Draft")), Plain("DateCreated", "date"), Plain("Results", "collection") };
        var map = GridBinding.BuildGroupPathMap(columns);
        Assert.HasCount(2, map);
        Assert.AreEqual("Name", map["Name"]);
        Assert.AreEqual("Customer/Name", map["Customer_Name"]);
    }

    [TestMethod]
    public void BuildGroupApply_emits_groupby_count_aggregate_with_optional_filter_transform() {
        // Exact wire shapes proven live (task-4.3 probes A and B/J).
        Assert.AreEqual("groupby((BatchStatus),aggregate($count as Count))",
            GridBinding.BuildGroupApply("BatchStatus", null));
        Assert.AreEqual("filter(Closed eq 'F')/groupby((BatchStatus),aggregate($count as Count))",
            GridBinding.BuildGroupApply("BatchStatus", "Closed eq 'F'"));
    }

    [TestMethod]
    public void ParseGroupBucket_reads_scalar_nested_and_null_group_values() {
        // Bucket shapes verified live: scalar {"BatchStatus":"P","Count":9120}; lookup nav paths come
        // back NESTED: {"CatalogueNavigation":{"Name":"COM"},"Count":16192} (probes A/C/D).
        Assert.AreEqual(((object?)"P", 9120),
            GridBinding.ParseGroupBucket(Row("""{"BatchStatus":"P","Count":9120}"""), "BatchStatus"));
        Assert.AreEqual(((object?)"COM", 16192),
            GridBinding.ParseGroupBucket(Row("""{"CatalogueNavigation":{"Name":"COM"},"Count":16192}"""), "CatalogueNavigation/Name"));
        Assert.AreEqual(((object?)1m, 571146),
            GridBinding.ParseGroupBucket(Row("""{"TemplateVersion":1,"Count":571146}"""), "TemplateVersion"));
        Assert.AreEqual(((object?)null, 3),
            GridBinding.ParseGroupBucket(Row("""{"BatchStatus":null,"Count":3}"""), "BatchStatus"));
        Assert.AreEqual(((object?)null, 2),
            GridBinding.ParseGroupBucket(Row("""{"CatalogueNavigation":null,"Count":2}"""), "CatalogueNavigation/Name"));
    }

    [TestMethod]
    public void ExtractRowKey_stringifies_value_or_returns_null() {
        var row = new Dictionary<string, object?> { ["ItemNumber"] = 1665361m, ["Status"] = null };
        Assert.AreEqual("1665361", GridBinding.ExtractRowKey(row, "ItemNumber"));
        Assert.IsNull(GridBinding.ExtractRowKey(row, "Status"));       // present but null
        Assert.IsNull(GridBinding.ExtractRowKey(row, "Missing"));      // not present
        Assert.IsNull(GridBinding.ExtractRowKey(row, null));
    }

    static JsonElement Row(string json) => JsonDocument.Parse(json).RootElement;

    [TestMethod]
    public void MaterializeRow_flattens_lookup_and_skips_collection_and_passes_through_scalars() {
        var columns = new[] {
            Plain("ItemNumber", "int"), Plain("Status"),
            Lookup("Customer", "Customer", "Name", "Name"),
            Plain("Results", "collection"),
        };
        var row = Row("""{"ItemNumber":1665361,"Status":"A","Customer":{"Name":"WBG"},"Results":[1,2]}""");

        var result = GridBinding.MaterializeRow(row, columns);

        Assert.AreEqual(1665361m, result["ItemNumber"]);
        Assert.AreEqual("A", result["Status"]);
        Assert.AreEqual("WBG", result["Customer_Name"]);
        Assert.IsFalse(result.ContainsKey("Results"));
    }

    [TestMethod]
    public void MaterializeRow_returns_null_for_a_column_missing_from_the_json_row() {
        // Mirrors a model-only member -- present in ViewMetadata.Columns but not in the OData EDM,
        // so $select can't be trusted and the row simply won't have the property.
        var columns = new[] { Plain("ModelOnlyFlag", "bool"), Plain("Status") };
        var row = Row("""{"Status":"A"}""");

        var result = GridBinding.MaterializeRow(row, columns);

        Assert.IsNull(result["ModelOnlyFlag"]);
        Assert.AreEqual("A", result["Status"]);
    }

    [TestMethod]
    public void MaterializeRow_resolves_enum_caption_from_numeric_value() {
        var columns = new[] { Enum("Status", (1, "Draft"), (2, "Approved")) };
        var row = Row("""{"Status":2}""");

        Assert.AreEqual("Approved", GridBinding.MaterializeRow(row, columns)["Status"]);
    }

    [TestMethod]
    public void MaterializeRow_falls_back_to_raw_value_when_enum_value_has_no_matching_caption() {
        var columns = new[] { Enum("Status", (1, "Draft")) };
        var row = Row("""{"Status":99}""");

        Assert.AreEqual(99m, GridBinding.MaterializeRow(row, columns)["Status"]);
    }

    [TestMethod]
    public void MaterializeRow_lookup_column_is_null_when_nav_property_absent() {
        var columns = new[] { Lookup("Customer", "Customer", "Name", "Name") };
        var row = Row("""{"ItemNumber":1}""");

        Assert.IsNull(GridBinding.MaterializeRow(row, columns)["Customer_Name"]);
    }

    [TestMethod]
    public void EnforceGroupCeiling_passes_at_or_below_and_fails_loud_above() {
        // GRID-004 (companion-headless backport): at or below the ceiling no throw; above it fail loud
        // (surfaces via GridCustomDataSource.ExceptionHandler), naming the column and the ceiling.
        // The fetch is bounded to ceiling+1 buckets, so the exact count is unknowable -- the
        // message says "more than {limit}".
        GridBinding.EnforceGroupCeiling(0, "ShipmentCourier");
        GridBinding.EnforceGroupCeiling(GridBinding.MaxServerGroups, "ShipmentCourier");
        var ex = Assert.ThrowsExactly<NotSupportedException>(() =>
            GridBinding.EnforceGroupCeiling(GridBinding.MaxServerGroups + 1, "InvoiceNumber"));
        StringAssert.Contains(ex.Message, "InvoiceNumber");
        StringAssert.Contains(ex.Message, $"more than {GridBinding.MaxServerGroups}");
    }

    [TestMethod]
    public void Dateonly_columns_stay_out_of_the_server_filter_and_keep_their_raw_string() {
        // GRID-004 (companion-headless backport, review I1 there): DateOnly members are Edm.Date on the
        // wire -- the translator's instant literals would 400 or day-shift there, so "dateonly"
        // joins the enum/lookup server-filter ceiling (disabled cell + absent from the criteria map)
        // and its cells keep the bare wire string ("2026-03-19") instead of a spurious-midnight
        // DateTime.
        var dateonly = Plain("ValidFrom", "dateonly");
        Assert.IsFalse(GridBinding.IsServerFilterable(dateonly));
        Assert.IsFalse(GridBinding.IsServerGroupable(dateonly));
        Assert.IsFalse(GridBinding.BuildCriteriaPathMap(new[] { dateonly }).ContainsKey("ValidFrom"));
        var row = Row("""{"ValidFrom":"2026-03-19"}""");
        Assert.AreEqual("2026-03-19", GridBinding.MaterializeRow(row, new[] { dateonly })["ValidFrom"]);
    }

    [TestMethod]
    public void ExtractDayFromCriteria_rejects_shapes_BuildDayRangeCriteria_did_not_emit() {
        // A criteria that merely STARTS with a ge-DateTime operand must not render as an active day
        // filter -- the full ge-day/lt-next-day same-field shape is required.
        var day = new DateTime(2026, 3, 19);
        var wrongUpper = GroupOperator.And(
            new BinaryOperator("OrderDate", day, BinaryOperatorType.GreaterOrEqual),
            new BinaryOperator("OrderDate", day.AddDays(7), BinaryOperatorType.Less));
        Assert.IsNull(GridBinding.ExtractDayFromCriteria(wrongUpper));
        var wrongField = GroupOperator.And(
            new BinaryOperator("OrderDate", day, BinaryOperatorType.GreaterOrEqual),
            new BinaryOperator("ShippedDate", day.AddDays(1), BinaryOperatorType.Less));
        Assert.IsNull(GridBinding.ExtractDayFromCriteria(wrongField));
    }

    [TestMethod]
    public void BuildDayRangeCriteria_round_trips_and_translates_to_a_date_range() {
        // The explicit date filter cell's criteria -- the same [day, next-day) shape the built-in
        // filter row produces for real DateTime columns. Time-of-day normalizes to the day.
        var criteria = GridBinding.BuildDayRangeCriteria("OrderDate", new DateTime(2026, 3, 19, 14, 0, 0));
        Assert.AreEqual(new DateTime(2026, 3, 19), GridBinding.ExtractDayFromCriteria(criteria));
        Assert.IsNull(GridBinding.BuildDayRangeCriteria("OrderDate", null));
        Assert.IsNull(GridBinding.ExtractDayFromCriteria(null));
        // The translator renders it over date(), NOT as an instant range -- see
        // ODataFilterTranslatorTests.DateTime_comparisons_translate_over_date_not_instant_literals
        // for the 400 that instant literals earn from this host. Exact string, no zone dependence.
        var translated = ODataFilterTranslator.Translate(criteria,
            new Dictionary<string, string> { ["OrderDate"] = "OrderDate" });
        Assert.AreEqual("(date(OrderDate) ge 2026-03-19 and date(OrderDate) lt 2026-03-20)", translated);
    }

    [TestMethod]
    public void Dotted_model_member_takes_the_same_nav_hop_treatment_as_a_lookup() {
        // An XAF ListView column can name a dotted MODEL path -- Order_ListView really has
        // "Customer.Name" -- and the projector classifies it as a plain string, Lookup == null. Every
        // wire form used to pass the dot straight through, which broke the column three ways at once:
        //   $orderby=Customer.Name -> 400 "The child type 'Customer.Name' in a cast was not an entity
        //     type. Casts can only be performed on entity types." (live evidence; Customer/Name is 200)
        //   $expand never covered Customer, so the wire row had no Customer at all
        //   the cell read row["Customer.Name"], which no OData payload contains -> permanently BLANK
        var dotted = Plain("Customer.Name");

        // Grid FieldName stays flat: a dot in a FieldName is DxGrid's POCO nested-path syntax, which
        // this codebase deliberately avoids for ExpandoObject rows (see FieldFor's remarks).
        Assert.AreEqual("Customer_Name", GridBinding.FieldFor(dotted));
        Assert.AreEqual("Customer/Name", GridBinding.OrderPathFor(dotted));
        Assert.AreEqual("Customer($select=Name)", GridBinding.BuildExpand(new[] { dotted }));
        Assert.AreEqual("Customer/Name", GridBinding.BuildCriteriaPathMap(new[] { dotted })["Customer_Name"]);

        var row = Row("""{"Customer":{"Name":"Sheffield Hardware"}}""");
        Assert.AreEqual("Sheffield Hardware", GridBinding.MaterializeRow(row, new[] { dotted })["Customer_Name"]);
        // A missing or null hop renders empty rather than throwing -- same contract as every other
        // absent member here.
        Assert.IsNull(GridBinding.MaterializeRow(Row("""{"Other":1}"""), new[] { dotted })["Customer_Name"]);
        Assert.IsNull(GridBinding.MaterializeRow(Row("""{"Customer":null}"""), new[] { dotted })["Customer_Name"]);
    }

    [TestMethod]
    public void StripShaping_clears_sort_AND_group_state_but_keeps_the_rest_of_the_layout() {
        // Shaping the server cannot honour is a visible, recoverable ceiling -- until LayoutAutoSaving
        // PERSISTS it, at which point the view fails on every subsequent load. Both kinds were hit live
        // on Order_ListView:
        //   sort  -- by Store, whose lookup display member Emblem is Edm.Binary: "The $orderby
        //            expression must evaluate to a single value of primitive type" (400)
        //   group -- by InvoiceNumber, 55k distinct values: EnforceGroupCeiling's NotSupportedException
        //            ("produced more than 500 groups"), which no static ceiling can predict because
        //            cardinality is a runtime property
        // Each saved layout then reproduced its failure on load until the prefs were cleared by hand.
        // Dropping the shaping recovers the view while keeping the column order/width the user arranged.
        var layout = new GridPersistentLayout {
            PageSize = 25,
            Columns = new GridPersistentLayoutCollection<GridPersistentLayoutColumn>([
                new GridPersistentLayoutColumn { FieldName = "InvoiceNumber", Width = "120px", GroupIndex = 0 },
                new GridPersistentLayoutColumn { FieldName = "Store_Emblem", SortIndex = 0, SortOrder = GridColumnSortOrder.Ascending },
            ])
        };

        var stripped = GridBinding.StripShaping(layout);

        Assert.IsTrue(stripped.Columns!.All(c => c.SortIndex is null && c.SortOrder is null),
            "every column's sort state must be gone");
        Assert.IsTrue(stripped.Columns!.All(c => c.GroupIndex is null),
            "every column's group state must be gone");
        Assert.AreEqual(25, stripped.PageSize, "PageSize must survive");
        Assert.AreEqual("120px", stripped.Columns!.First(c => c.FieldName == "InvoiceNumber").Width,
            "column widths must survive");
        Assert.AreEqual(2, stripped.Columns!.Count, "no column may be dropped");
        // A column-less layout is legal (WhenWritingDefault trimming) and must not throw.
        Assert.IsNotNull(GridBinding.StripShaping(new GridPersistentLayout()));
    }

    [TestMethod]
    public void RestorePageSize_refills_a_PageSize_that_serialization_trimmed_away() {
        // GridPersistentLayout.PageSize is int? carrying [JsonIgnore(WhenWritingDefault)] (verified in
        // dxdocs, 26.1), so a null PageSize is OMITTED from the persisted blob and deserializes back as
        // null. Handing DxGrid that layout resets its PageSize to the documented default of 10, silently
        // overriding the markup value -- live repro: Order_ListView (the one view with persisted prefs)
        // served 10 rows per page while every unpersisted view served 25.
        Assert.AreEqual(25, GridBinding.RestorePageSize(new GridPersistentLayout(), 25).PageSize);
        // A persisted user choice still wins -- that is the point of persisting the layout.
        Assert.AreEqual(50, GridBinding.RestorePageSize(new GridPersistentLayout { PageSize = 50 }, 25).PageSize);
    }

    [TestMethod]
    public void MaterializeRow_materializes_date_columns_as_wall_time_DateTime() {
        // GRID-004 (companion-headless backport): the wire sends ISO-offset strings; date columns must land
        // as typed DateTime (wall time, offset dropped) so DxGrid renders formatted dates and the
        // in-memory evaluator compares real DateTime values against the date filter cell's range
        // criteria. Null stays null; an unparseable value falls back to the raw string.
        var columns = new[] { Plain("OrderDate", "date"), Plain("ShippedDate", "date"), Plain("Broken", "date") };
        var row = Row("""{"OrderDate":"2026-03-19T19:18:13+01:00","ShippedDate":null,"Broken":"not-a-date"}""");

        var result = GridBinding.MaterializeRow(row, columns);

        Assert.AreEqual(new DateTime(2026, 3, 19, 19, 18, 13), result["OrderDate"]);
        Assert.AreEqual(DateTimeKind.Unspecified, ((DateTime)result["OrderDate"]!).Kind);
        Assert.IsNull(result["ShippedDate"]);
        Assert.AreEqual("not-a-date", result["Broken"]);
    }

    [TestMethod]
    public void MaterializeRow_injects_key_member_even_when_not_a_visible_column() {
        // Order's Guid "ID" is the key but not a displayed column -- it must still land in the row so
        // DxGrid.KeyFieldName / ExtractRowKey can resolve the row on selection.
        var columns = new[] { Plain("InvoiceNumber"), Plain("ShippingType") };
        var row = Row("""{"ID":"3f2504e0-4f89-11d3-9a0c-0305e82c3301","InvoiceNumber":"0000001"}""");

        var result = GridBinding.MaterializeRow(row, columns, keyMember: "ID");

        Assert.AreEqual("3f2504e0-4f89-11d3-9a0c-0305e82c3301", result["ID"]);
        Assert.AreEqual("0000001", result["InvoiceNumber"]);
        Assert.AreEqual("3f2504e0-4f89-11d3-9a0c-0305e82c3301", GridBinding.ExtractRowKey(result, "ID"));
    }
}
