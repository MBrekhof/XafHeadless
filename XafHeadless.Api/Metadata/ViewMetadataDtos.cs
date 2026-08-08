namespace XafHeadless.Api.Metadata;

// THE client contract. Tasks 7-10 deserialize exactly these property names — do not rename.
// GAP-002: Appearance carries the view's projected conditional-appearance (color/style) rules; last
// (nullable, default) so existing positional constructors/old payloads are unaffected.
// GAP-002 enum-fix: AppearanceEnums carries enum metadata for members the appearance rules can reference
// but that AREN'T already among the view's displayed members (columns/layout items) -- e.g. Evaluation.Rating
// on Employee_Evaluations_ListView. Without this, the client's caption->name rewrite has no metadata channel
// for such members. Nullable/default and only ever populated when the view has appearance rules -- additive,
// no cost/behavior change for rule-less views or existing payloads.
public record ViewMetadata(string Id, string Type, string ObjectType, string KeyMember, string Caption,
    AllowSet Allow, List<ColumnMetadata>? Columns, LayoutNode? Layout, List<ActionMetadata> Actions,
    IReadOnlyList<AppearanceRuleDto>? Appearance = null,
    IReadOnlyDictionary<string, IReadOnlyList<EnumValueMetadata>>? AppearanceEnums = null);
public record AllowSet(bool Edit, bool New, bool Delete);
public record ColumnMetadata(string Member, string Caption, string DataType, int? SortIndex,
    string? SortOrder, LookupMetadata? Lookup, List<EnumValueMetadata>? Enum);
// GRID-005: DisplayDataType is the display member's ClassifyDataType hint -- the client sorts/groups a
// lookup by its display path (Store/Emblem), so only this tells it whether that path is a primitive
// OData can order by. Added last (default null) so existing 3-arg constructions keep compiling, and an
// absent value means "unknown" rather than "unsortable".
public record LookupMetadata(string ObjectType, string KeyMember, string DisplayMember,
    string? DisplayDataType = null);
// GAP-002: Name is the enum member's CLR name (Enum.GetName) — the form XAF criteria literals use
// (nameof(Member)). Added last (default null) so 2-arg constructions keep compiling. Value stays the
// integer; Caption stays the descriptor caption.
public record EnumValueMetadata(object Value, string Caption, string? Name = null);
// GAP-002: one projected ConditionalAppearance ViewItem rule. TargetItems are the styled member names,
// or ["*"] for the whole row. Colors are CSS hex (#RRGGBB); FontStyle is the DXFontStyle token string
// (e.g. "Bold", "Strikeout", "Bold, Underline"). Only color/style rules are projected (Visibility/
// Enabled-only rules are dropped server-side — the client renders no generic actions/visibility).
public record AppearanceRuleDto(string Criteria, IReadOnlyList<string> TargetItems,
    string? FontColor, string? BackColor, string? FontStyle);
// GAP-010: Aggregated (nestedList only) mirrors IMemberInfo.IsAggregated -- an aggregated (composite)
// collection is owned by the master (New/Delete), a non-aggregated (shared) one is Link/Unlink. The client
// uses it to decide which nested-collection actions to offer. Nullable/default (null on item nodes and old
// payloads) so existing positional constructions keep compiling.
// EDIT-001: EditorAlias is the editor the app DECLARED via [EditorAlias], e.g. "DxHtmlPropertyEditor" on a
// string member. It is ADDITIVE to Editor, which stays the CLR-derived classification -- a client that does
// not understand the alias behaves exactly as before. Null when the member declares none.
public record LayoutNode(string Kind, string? Caption, string? Member, string? Editor,
    bool? AllowWrite, bool? Required, int? MaxLength, string? ViewId, string? MasterKeyMember,
    LookupMetadata? Lookup, List<EnumValueMetadata>? Enum, List<LayoutNode>? Children, bool? Aggregated = null,
    string? EditorAlias = null);
public record ActionMetadata(string Id, string Caption, bool SelectionRequired);

// GAP-004: one flat, security-trimmed nav-menu entry. See NavigationProjector for the filter.
public record NavigationItemDto(string Caption, string ViewId);
