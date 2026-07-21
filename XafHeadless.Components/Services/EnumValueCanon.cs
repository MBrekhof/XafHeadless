using System.Globalization;
using System.Text.Json;

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
}
