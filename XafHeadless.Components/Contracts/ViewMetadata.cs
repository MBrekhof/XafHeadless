namespace XafHeadless.Components.Contracts;

// Mirror of XafHeadless.Api/Metadata/ViewMetadataDtos.cs -- same property names, do not rename.
// KeyMember (PH2-001): the object's own key member name, projected server-side from
// TypeInfo.KeyMember. The client binds to it directly instead of guessing (the old
// GridBinding.InferKeyMember convention is gone).
// GAP-002: Appearance mirrors the server's projected conditional-appearance rules (last, nullable).
// GAP-002 enum-fix: AppearanceEnums mirrors the server's enum metadata for appearance-rule members that
// aren't already a displayed column/layout item (e.g. Rating on Employee_Evaluations_ListView).
public record ViewMetadata(string Id, string Type, string ObjectType, string KeyMember, string Caption,
    AllowSet Allow, List<ColumnMetadata>? Columns, LayoutNode? Layout, List<ActionMetadata> Actions,
    IReadOnlyList<AppearanceRuleDto>? Appearance = null,
    IReadOnlyDictionary<string, IReadOnlyList<EnumValueMetadata>>? AppearanceEnums = null);
public record AllowSet(bool Edit, bool New, bool Delete);
public record ColumnMetadata(string Member, string Caption, string DataType, int? SortIndex,
    string? SortOrder, LookupMetadata? Lookup, List<EnumValueMetadata>? Enum);
// GRID-005: mirror of the API DTO -- the display member's ClassifyDataType hint. Null means the host
// predates the field (unknown), which stays sortable; see GridBinding.IsServerSortable.
public record LookupMetadata(string ObjectType, string KeyMember, string DisplayMember,
    string? DisplayDataType = null);
// GAP-002: Name is the enum member's CLR name (the form criteria literals use); mirror of the API DTO.
public record EnumValueMetadata(object Value, string Caption, string? Name = null);
// GAP-002: mirror of XafHeadless.Api/Metadata/ViewMetadataDtos.cs's AppearanceRuleDto.
public record AppearanceRuleDto(string Criteria, IReadOnlyList<string> TargetItems,
    string? FontColor, string? BackColor, string? FontStyle);
// GAP-010: Aggregated (nestedList only) mirrors the server's IMemberInfo.IsAggregated -- aggregated
// (composite, owned) collections use New/Delete, non-aggregated (shared) ones use Link/Unlink.
public record LayoutNode(string Kind, string? Caption, string? Member, string? Editor,
    bool? AllowWrite, bool? Required, int? MaxLength, string? ViewId, string? MasterKeyMember,
    LookupMetadata? Lookup, List<EnumValueMetadata>? Enum, List<LayoutNode>? Children, bool? Aggregated = null);
public record ActionMetadata(string Id, string Caption, bool SelectionRequired);

// GAP-004: mirror of XafHeadless.Api/Metadata/ViewMetadataDtos.cs's NavigationItemDto.
public record NavigationItemDto(string Caption, string ViewId);
