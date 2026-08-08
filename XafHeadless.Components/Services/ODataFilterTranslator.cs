using System.Globalization;
using DevExpress.Data.Filtering;

namespace XafHeadless.Components.Services;

// Translates the DxGrid filter row's CriteriaOperator (GridCustomDataSourceOptions.FilterCriteria --
// see ODataGridDataSource, which used to deliberately ignore it) into an OData v4 $filter fragment.
//
// Verified (not assumed) against dxdocs before writing:
//  - DxGrid.ShowFilterRow (docs.devexpress.com/Blazor/DevExpress.Blazor.DxGrid.ShowFilterRow, 26.1):
//    `[Parameter] public bool ShowFilterRow { get; set; }` -- enables the filter row.
//  - GridCustomDataSourceOptions.FilterCriteria (docs.devexpress.com/Blazor/DevExpress.Blazor.
//    GridCustomDataSourceOptions.FilterCriteria): `public CriteriaOperator FilterCriteria { get; }`,
//    inherited by both GridCustomDataSourceItemsOptions and GridCustomDataSourceCountOptions.
//  - CriteriaOperator shape (docs.devexpress.com/CoreLibraries/DevExpress.Data.Filtering.*, 26.1), cross
//    checked against the Blazor "Filter API in Blazor Grid" reference article's own examples:
//      BinaryOperator     : CriteriaOperator { LeftOperand, RightOperand, OperatorType: BinaryOperatorType }
//      FunctionOperator   : CriteriaOperator { Operands: CriteriaOperatorCollection, OperatorType: FunctionOperatorType }
//      GroupOperator      : CriteriaOperator { Operands: CriteriaOperatorCollection, OperatorType: GroupOperatorType }
//      UnaryOperator      : CriteriaOperator { Operand, OperatorType: UnaryOperatorType }
//        (NotOperator and NullOperator are both UnaryOperator subclasses with a fixed OperatorType --
//         matching on the base UnaryOperator's OperatorType field catches all three shapes uniformly)
//      OperandProperty    : CriteriaOperator { PropertyName }
//      OperandValue       : CriteriaOperator { Value }  (ConstantValue : OperandValue, same Value shape)
public static class ODataFilterTranslator {
    // Supported operator set (the filter row's common output, per GAP-005 decision 3):
    //   Binary: Equal/NotEqual/Greater/GreaterOrEqual/Less/LessOrEqual -> eq/ne/gt/ge/lt/le
    //   Function: Contains/StartsWith/EndsWith -> contains()/startswith()/endswith(); IsNull -> eq null
    //   Unary: IsNull -> eq null; Not(IsNull) -> ne null
    //   Group: And/Or, parenthesized
    // Anything outside that set (Like, InOperator, BetweenOperator, AggregateOperand, JoinOperand,
    // property-to-property comparisons, ...) is intentionally left untranslated -- see the `_ => null`
    // branches below -- rather than guessed at, per "never emit a silently-wrong filter".
    //
    // ponytail: scalar columns only (string/number/date/bool). Enum and lookup columns are OUT of scope
    // for this pass -- the grid materializes their cell value as a caption/display string (GridBinding.
    // MaterializeRow's EnumCaption/LookupDisplay), which does NOT match the OData property's real
    // literal representation, so translating their filter-row criteria as-is would silently produce a
    // WRONG filter. Enforcement lives in the caller: XafListView builds pathByField to include only
    // scalar columns, so an enum/lookup FieldName is simply absent from the map -- Path() below returns
    // null for it, and the whole clause is skipped (not defaulted to the raw name, unlike GridBinding.
    // BuildOrderBy's sort fallback). Upgrade path: teach this translator the real enum/lookup storage
    // shape if untranslated enum/lookup filtering becomes a real ask.
    public static string? Translate(CriteriaOperator? criteria, IReadOnlyDictionary<string, string> pathByField) =>
        criteria switch {
            null => null,
            GroupOperator g => TranslateGroup(g, pathByField),
            BinaryOperator b => TranslateBinary(b, pathByField),
            FunctionOperator f => TranslateFunction(f, pathByField),
            UnaryOperator { OperatorType: UnaryOperatorType.Not, Operand: var inner } => TranslateNullNegation(inner, pathByField),
            UnaryOperator { OperatorType: UnaryOperatorType.IsNull } u => IsNullClause(u.Operand, pathByField),
            _ => null
        };

    // AND-combines the baked-in master filter with the translated user filter (GAP-005 decision 2):
    // both present -> "(master) and (user)"; only one present -> that one; neither -> null.
    public static string? Combine(string? master, string? user) => (master, user) switch {
        (null, null) => null,
        ({ } m, null) => m,
        (null, { } u) => u,
        ({ } m, { } u) => $"({m}) and ({u})"
    };

    static string? TranslateNullNegation(CriteriaOperator inner, IReadOnlyDictionary<string, string> pathByField) =>
        inner switch {
            UnaryOperator { OperatorType: UnaryOperatorType.IsNull } u => NeNull(u.Operand, pathByField),
            FunctionOperator { OperatorType: FunctionOperatorType.IsNull, Operands.Count: 1 } f => NeNull(f.Operands[0], pathByField),
            _ => null // negating anything else -- ceiling, skip rather than guess
        };

    static string? IsNullClause(CriteriaOperator operand, IReadOnlyDictionary<string, string> pathByField) =>
        Path(operand, pathByField) is { } p ? $"{p} eq null" : null;

    static string? NeNull(CriteriaOperator operand, IReadOnlyDictionary<string, string> pathByField) =>
        Path(operand, pathByField) is { } p ? $"{p} ne null" : null;

    static string? TranslateGroup(GroupOperator g, IReadOnlyDictionary<string, string> pathByField) {
        var parts = new List<string>();
        foreach (CriteriaOperator operand in g.Operands) {
            var translated = Translate(operand, pathByField);
            if (translated is not null) parts.Add(translated);
        }
        return parts.Count switch {
            0 => null,
            1 => parts[0],
            _ => "(" + string.Join(g.OperatorType == GroupOperatorType.Or ? " or " : " and ", parts) + ")"
        };
    }

    static string? TranslateBinary(BinaryOperator b, IReadOnlyDictionary<string, string> pathByField) {
        if (Path(b.LeftOperand, pathByField) is not { } path) return null;
        var op = b.OperatorType switch {
            BinaryOperatorType.Equal => "eq",
            BinaryOperatorType.NotEqual => "ne",
            BinaryOperatorType.Greater => "gt",
            BinaryOperatorType.GreaterOrEqual => "ge",
            BinaryOperatorType.Less => "lt",
            BinaryOperatorType.LessOrEqual => "le",
            _ => null // e.g. Like -- ceiling
        };
        if (op is null) return null;
        // A DateTime constant compares over date(), never as an instant -- see the remarks above
        // FormatValue for the 400 an instant literal earns from this host.
        if (b.RightOperand is OperandValue { Value: DateTime day })
            return $"date({path}) {op} {day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
        return FormatValue(b.RightOperand) is { } value ? $"{path} {op} {value}" : null;
    }

    static string? TranslateFunction(FunctionOperator f, IReadOnlyDictionary<string, string> pathByField) {
        if (f.OperatorType == FunctionOperatorType.IsNull && f.Operands.Count == 1)
            return IsNullClause(f.Operands[0], pathByField);

        if (f.Operands.Count != 2 || Path(f.Operands[0], pathByField) is not { } path) return null;
        if (FormatValue(f.Operands[1]) is not { } value) return null;
        return f.OperatorType switch {
            FunctionOperatorType.Contains => $"contains({path},{value})",
            FunctionOperatorType.StartsWith => $"startswith({path},{value})",
            FunctionOperatorType.EndsWith => $"endswith({path},{value})",
            _ => null // e.g. IsNullOrEmpty, InRange -- ceiling
        };
    }

    static string? Path(CriteriaOperator operand, IReadOnlyDictionary<string, string> pathByField) =>
        operand is OperandProperty p && pathByField.TryGetValue(p.PropertyName, out var path) ? path : null;

    // DateTime comparisons do NOT go through here -- TranslateBinary emits them over date() with a
    // date-only literal, because this host cannot compare them as instants at all. The members are
    // Edm.DateTimeOffset in the EDM while the CLR property is DateTime, so OData finds no operator for
    // the pair and answers 400: "The binary operator GreaterThanOrEqual is not defined for the types
    // 'System.Nullable`1[System.DateTime]' and 'System.Nullable`1[System.DateTimeOffset]'" (live
    // evidence; a no-offset literal fails to parse as Edm.DateTimeOffset instead, and the unhandled
    // 400 terminated the Blazor circuit). date(path) against a date literal is the form it accepts,
    // and it needs no zone conversion -- the stored wall-time date is what the picker's day means.
    // This DateTime branch remains only for non-comparison operators (a DateTime reaching
    // contains/startswith), where an unquoted Convert.ToString would emit a broken literal.
    static string? FormatValue(CriteriaOperator operand) => operand switch {
        OperandValue { Value: null } => "null",
        OperandValue { Value: string s } => $"'{s.Replace("'", "''")}'",
        OperandValue { Value: bool b } => b ? "true" : "false",
        OperandValue { Value: DateTime dt } => dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + "Z",
        OperandValue { Value: DateTimeOffset dto } => dto.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + "Z",
        OperandValue { Value: { } v } => Convert.ToString(v, CultureInfo.InvariantCulture),
        _ => null // right operand isn't a value (e.g. property-to-property compare) -- ceiling
    };
}
