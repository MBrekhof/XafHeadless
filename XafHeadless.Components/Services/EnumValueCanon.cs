using System.Globalization;
using System.Text.Json;
using XafHeadless.Components.Contracts;

namespace XafHeadless.Components.Services;

// PH2-006: extracted from GridBinding/DetailBinding (T9 finding: "Canon triplicated") --
// both copies were byte-for-byte identical (mod a trailing comma), so a green Components.Tests suite
// after this extraction is the proof no behavior changed.
//
// Canonicalizes both a materialized CLR scalar (string/decimal/bool from GridBinding.RawValue /
// DetailBinding.Scalar) and a deserialized EnumValueMetadata.Value (a boxed JsonElement, since
// System.Text.Json deserializes `object`-typed record properties to JsonElement) to the same comparable
// string, so enum value matching works regardless of whether the server sent the enum as a number or a
// string.
//
// NOTE: EnumEditor.razor has its own similarly-named Canon that was intentionally NOT folded in here --
// it differs in real ways (non-nullable return, no `bool` special-case, null -> "" instead of null)
// because it only ever canonicalizes EnumValueMetadata.Value option lists (never bool, never a missing
// value), not general row scalars. Forcing it onto this shared version would risk a behavior change for
// no reuse benefit -- see the PH2-006 report's "canon triplication" assessment.
internal static class EnumValueCanon {
    internal static string? Canonicalize(object? v) => v switch {
        null => null,
        JsonElement { ValueKind: JsonValueKind.String } el => el.GetString(),
        JsonElement el => el.GetRawText(),
        bool b => b ? "true" : "false",
        _ => Convert.ToString(v, CultureInfo.InvariantCulture)
    };

    // BUG-012: OData serialises an enum member as its NAME ("NeedAssistance"), while EnumValueMetadata.Value
    // is the NUMBER. Matching only on Value never matched a live row, so captions fell back to the raw name
    // and the enum editor showed blank for any existing value. Name first (the wire form), Value second
    // (a numeric row, should a server ever send one).
    internal static EnumValueMetadata? Find(IEnumerable<EnumValueMetadata> values, object? raw) {
        var text = Canonicalize(raw);
        if (text is null) return null;
        return values.FirstOrDefault(e => e.Name == text)
            ?? values.FirstOrDefault(e => Canonicalize(e.Value) == text);
    }
}
