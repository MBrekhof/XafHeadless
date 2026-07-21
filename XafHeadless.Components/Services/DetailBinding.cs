using System.Globalization;
using System.Text.Json;
using XafHeadless.Components.Contracts;

namespace XafHeadless.Components.Services;

// Pure translation logic for XafDetailView (Task 9): dirty-diff, layout-node collapse, lookup-expand
// derivation, and single-object value materialization from a fetched OData JSON row. No DevExpress /
// Blazor types here so it stays unit-testable without a Blazor host (see DetailBindingTests). The
// component (XafDetailView) owns the mutable dirty/error dictionaries and drives these functions.
public static class DetailBinding {
    // ---- dirty-diff -------------------------------------------------------------------------

    // Equality used to decide whether an edited value differs from the originally-loaded value.
    // Empty string is treated as null (an emptied text box must not read as a change from a null
    // original); numeric CLR types are normalized to decimal so an int original compares equal to
    // the decimal the number editors round-trip.
    public static bool ValuesEqual(object? a, object? b) {
        a = Normalize(a);
        b = Normalize(b);
        if (a is null || b is null) return a is null && b is null;
        if (a is decimal da && b is decimal db) return da == db;
        return a.Equals(b);
    }

    static object? Normalize(object? v) => v switch {
        null => null,
        string s => s.Length == 0 ? null : s,
        bool or DateTime => v,
        _ => TryDecimal(v, out var d) ? d : v,
    };

    static bool TryDecimal(object v, out decimal d) {
        try { d = Convert.ToDecimal(v, CultureInfo.InvariantCulture); return true; }
        catch { d = 0; return false; }
    }

    // Adds member->newValue to the changes dict when it differs from the original, or removes it
    // when the user has edited the value back to what it was (so Save stays disabled if nothing
    // net-changed). The changes dict therefore always holds ONLY genuinely-changed members — exactly
    // the partial body POST api/save/{entity}/{key} expects.
    public static void ApplyChange(Dictionary<string, object?> changes, object? original, string member, object? newValue) {
        if (ValuesEqual(original, newValue)) changes.Remove(member);
        else changes[member] = newValue;
    }

    // ---- layout-node collapse ---------------------------------------------------------------

    // XAF bisects large editor sets into deeply nested captionless 2-column wrapper groups. A group
    // with no caption and a single child is pure structural noise -- collapse it to its child. A
    // captioned group (a real section) or a multi-child group is meaningful and is kept.
    public static bool ShouldCollapse(LayoutNode node) =>
        node.Kind == "group" && string.IsNullOrEmpty(node.Caption) && node.Children is { Count: 1 };

    public static LayoutNode Unwrap(LayoutNode node) {
        while (ShouldCollapse(node)) node = node.Children![0];
        return node;
    }

    // ---- tree walks -------------------------------------------------------------------------

    // Every editor (item) node anywhere in the layout, in document order. nestedList nodes have no
    // children so they contribute nothing (their collections are Task 10's XafListView, not editors).
    public static IEnumerable<LayoutNode> AllItems(LayoutNode? root) {
        if (root is null) yield break;
        if (root.Kind == "item") { yield return root; yield break; }
        foreach (var child in root.Children ?? Enumerable.Empty<LayoutNode>())
            foreach (var item in AllItems(child)) yield return item;
    }

    // $expand covering exactly the lookup members that appear as items in the layout (the brief's
    // rule). Plain member names, no nested $select -- $select is never sent (model-vs-EDM divergence,
    // see docs/notes/save-contract.md + GridBinding). Null when the layout has no lookup editors.
    public static string? LookupExpand(LayoutNode? root) {
        var members = AllItems(root)
            .Where(i => i.Editor == "lookup" && i.Lookup is not null && i.Member is not null)
            .Select(i => i.Member!).Distinct().ToList();
        return members.Count == 0 ? null : string.Join(",", members);
    }

    // ---- value materialization --------------------------------------------------------------

    // Pulls a single item's current value out of the fetched OData row as the CLR type its editor
    // binds to. A member missing from the row (a model item that isn't a real OData EDM property --
    // the task-8 divergence) yields null instead of throwing.
    public static object? ItemValue(JsonElement row, LayoutNode item) {
        if (item.Member is null) return null;
        if (!row.TryGetProperty(item.Member, out var el) || el.ValueKind == JsonValueKind.Null) return null;
        return item.Editor switch {
            "lookup" => item.Lookup is { } lk && el.ValueKind == JsonValueKind.Object
                            ? LookupKey(el, lk.KeyMember) : null,
            "enum" => EnumValueCanon.Canonicalize(Scalar(el)),
            "date" or "dateonly" => el.ValueKind == JsonValueKind.String && el.TryGetDateTime(out var dt) ? dt : null,
            "bool" => el.ValueKind is JsonValueKind.True or JsonValueKind.False ? el.GetBoolean() : null,
            "int" or "decimal" => el.ValueKind == JsonValueKind.Number ? el.GetDecimal() : null,
            _ => Scalar(el),   // "string" and any unmapped hint -> raw scalar (badge shows it read-only)
        };
    }

    static object? LookupKey(JsonElement nav, string keyMember) =>
        nav.TryGetProperty(keyMember, out var k) ? Scalar(k) : null;

    static object? Scalar(JsonElement el) => el.ValueKind switch {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.GetDecimal(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => el.GetRawText(),
    };

    // OData equality filter for the single-object fetch, e.g. "SampleNumber eq 1665361". Numeric keys
    // are emitted unquoted (matches GridBinding.BuildMasterFilter's documented numeric-key shape).
    public static string KeyFilter(string keyMember, string key) => $"{keyMember} eq {key}";
}
