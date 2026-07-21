using System.Runtime.CompilerServices;
using DevExpress.Data.Filtering;
using DevExpress.Data.Filtering.Helpers;
using XafHeadless.Components.Contracts;

namespace XafHeadless.Components.Services;

// GAP-002: client-side conditional-appearance evaluation. Given the view's projected AppearanceRuleDto's
// + ColumnMetadata, decides -- per grid row (an ExpandoObject/IDictionary MaterializeRow produced) -- which
// color/style each Fitting rule applies. Uses the framework evaluator the GAP-002 spike proved
// (DevExpress.Data.Filtering.Helpers.ExpressionEvaluator over EvaluatorContextDescriptorDefault(typeof(object)),
// which special-cases ExpandoObject) -- never a re-implemented criteria engine. DevExpress-only deps
// (DevExpress.Data.Filtering via DevExpress.Blazor); zero DevExpress.ExpressApp/demo refs (wire rule).
//
// Two mandatory guards, both proven by the spike:
//   (a) MISSING-MEMBER guard: ExpressionEvaluator.Fit THROWS InvalidPropertyPathException if the criteria
//       references a member the row has no key for. So a rule is skipped for a row unless every member its
//       criteria references is a row key. (XafListView also enriches rows with these members up front --
//       see GridBinding.MaterializeRow's appearanceMembers -- so a rule targeting a member that isn't a
//       visible column, e.g. Evaluation.Rating, still evaluates.)
//   (b) ENUM-LITERAL normalization: criteria compare an enum member to its CLR NAME literal (nameof(Member)).
//       VERIFIED LIVE (not the spike's assumption): OData returns the enum's NAME, and MaterializeRow keeps
//       that NAME (the numeric EnumValueMetadata.Value never matches the name, so EnumCaption falls back to
//       the raw name) -- so the row ALSO holds the NAME. Name == criteria literal -> they match directly.
//       The one case that still needs a rewrite is a criteria authored with the CAPTION instead of the name;
//       we normalize such a literal back to the NAME (the row's form) by walking the parsed CriteriaOperator
//       tree. (For this demo every enum caption equals its name, so the rewrite is a verified no-op here; the
//       unit tests exercise the divergent case with synthetic metadata.)
public sealed class AppearanceEvaluator {
    // typeof(object) is enough to unlock the descriptor's ExpandoObject branch (spike §2); reused across rows.
    static readonly EvaluatorContextDescriptorDefault Descriptor = new(typeof(object));

    sealed record Prepared(AppearanceRuleDto Rule, ExpressionEvaluator Evaluator, IReadOnlyList<string> Members);

    readonly List<Prepared> prepared = new();
    readonly HashSet<string> referencedMembers = new(StringComparer.Ordinal);
    readonly ConditionalWeakTable<object, IReadOnlyList<AppliedAppearance>> memo = new();

    // GAP-002 enum-fix: appearanceEnums is the server-projected ViewMetadata.AppearanceEnums channel --
    // enum metadata for members the rules can reference that AREN'T a displayed column (e.g. Rating on
    // Employee_Evaluations_ListView). Without it, enumsByMember only ever knows about column-backed enum
    // members, so the caption->name rewrite below is structurally inert for anything else. Column metadata
    // wins on conflict (TryAdd only fills gaps) -- additive, no behavior change when it's null.
    public AppearanceEvaluator(IReadOnlyList<AppearanceRuleDto>? rules, IReadOnlyList<ColumnMetadata>? columns,
            IReadOnlyDictionary<string, IReadOnlyList<EnumValueMetadata>>? appearanceEnums = null) {
        var enumsByMember = (columns ?? Array.Empty<ColumnMetadata>())
            .Where(c => c.DataType == "enum" && c.Enum is { Count: > 0 })
            .GroupBy(c => c.Member, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<EnumValueMetadata>)g.First().Enum!, StringComparer.Ordinal);
        if (appearanceEnums is not null)
            foreach (var (member, values) in appearanceEnums)
                enumsByMember.TryAdd(member, values);

        foreach (var rule in rules ?? Array.Empty<AppearanceRuleDto>()) {
            CriteriaOperator criteria;
            try { criteria = CriteriaOperator.Parse(rule.Criteria); }
            catch { continue; }                               // unparseable criteria -> drop the rule, never crash
            if (ReferenceEquals(criteria, null)) continue;
            var members = new List<string>();
            CollectAndRewrite(criteria, enumsByMember, members);
            prepared.Add(new Prepared(rule, new ExpressionEvaluator(Descriptor, criteria), members));
            foreach (var m in members) referencedMembers.Add(m);
        }
    }

    public bool HasRules => prepared.Count > 0;

    // The union of members every rule's criteria references -- XafListView carries these into each row
    // (GridBinding.MaterializeRow) so criteria over non-column members (e.g. Rating) can evaluate.
    public IReadOnlyCollection<string> ReferencedMembers => referencedMembers;

    // CustomizeElement fires once per cell AND per row on every re-render, so memoize per row identity.
    // ConditionalWeakTable auto-evicts when the row ExpandoObject is GC'd (rows are rebuilt on each data load).
    public IReadOnlyList<AppliedAppearance> Evaluate(IDictionary<string, object?> row) =>
        memo.GetValue(row, _ => EvaluateCore(row));

    IReadOnlyList<AppliedAppearance> EvaluateCore(IDictionary<string, object?> row) {
        List<AppliedAppearance>? matched = null;
        foreach (var p in prepared) {
            if (p.Members.Any(m => !row.ContainsKey(m))) continue;   // guard (a): missing member -> skip (would throw)
            bool fits;
            try { fits = p.Evaluator.Fit(row); }
            catch { continue; }                                       // defensive: one bad rule never kills the row
            if (!fits) continue;
            (matched ??= new()).Add(new AppliedAppearance(
                p.Rule.TargetItems, p.Rule.TargetItems.Contains("*"),
                p.Rule.FontColor, p.Rule.BackColor, p.Rule.FontStyle));
        }
        return matched ?? (IReadOnlyList<AppliedAppearance>)Array.Empty<AppliedAppearance>();
    }

    // Single recursive walk of the parsed CriteriaOperator tree. VERIFIED node API against installed 26.1
    // DevExpress.Data/Filtering/Criteria.cs (same family GAP-005's ODataFilterTranslator uses):
    //   OperandProperty.PropertyName; OperandValue.Value (settable); BinaryOperator.LeftOperand/RightOperand;
    //   GroupOperator.Operands / FunctionOperator.Operands (CriteriaOperatorCollection); UnaryOperator.Operand.
    // Job 1: collect every referenced OperandProperty name. Job 2: enum-literal normalization (guard b).
    static void CollectAndRewrite(CriteriaOperator op,
            IReadOnlyDictionary<string, IReadOnlyList<EnumValueMetadata>> enums, List<string> members) {
        switch (op) {
            case OperandProperty p:
                if (!members.Contains(p.PropertyName)) members.Add(p.PropertyName);
                break;
            case BinaryOperator b:
                NormalizeEnum(b.LeftOperand, b.RightOperand, enums);
                NormalizeEnum(b.RightOperand, b.LeftOperand, enums);
                CollectAndRewrite(b.LeftOperand, enums, members);
                CollectAndRewrite(b.RightOperand, enums, members);
                break;
            case UnaryOperator u:
                CollectAndRewrite(u.Operand, enums, members);
                break;
            case FunctionOperator f:
                if (f.Operands.Count == 2) {
                    NormalizeEnum(f.Operands[0], f.Operands[1], enums);
                    NormalizeEnum(f.Operands[1], f.Operands[0], enums);
                }
                foreach (CriteriaOperator o in f.Operands) CollectAndRewrite(o, enums, members);
                break;
            case GroupOperator g:
                foreach (CriteriaOperator o in g.Operands) CollectAndRewrite(o, enums, members);
                break;
        }
    }

    // If `prop` is an enum-typed member and `val` is a string literal that is a CAPTION (not already a NAME),
    // rewrite the literal in place to the member NAME -- the form the grid row stores. No-op when the literal
    // is already a name (the nameof case) or the member isn't an enum column.
    static void NormalizeEnum(CriteriaOperator prop, CriteriaOperator val,
            IReadOnlyDictionary<string, IReadOnlyList<EnumValueMetadata>> enums) {
        if (prop is not OperandProperty p || !enums.TryGetValue(p.PropertyName, out var values)) return;
        if (val is not OperandValue { Value: string literal } ov) return;
        if (values.Any(e => e.Name == literal)) return;                       // already the row's NAME form
        if (values.FirstOrDefault(e => e.Caption == literal)?.Name is { } name) ov.Value = name;
    }
}

// One rule that Fit a row: the members it styles (or the whole row) plus the color/style to apply.
public sealed record AppliedAppearance(IReadOnlyList<string> TargetItems, bool WholeRow,
    string? FontColor, string? BackColor, string? FontStyle);
