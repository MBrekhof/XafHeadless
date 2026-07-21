using System.Text.Json;

namespace XafHeadless.Api.Tests;

// PH2-006 (folded from GAP-009 review): the same private static Flatten(JsonElement) walked the
// projected Layout tree identically in DetailViewMetadataTests and NViewSweepMetadataTests
// (byte-for-byte, verified before extraction) -- consolidated here so every metadata test that needs
// to flatten a Layout tree into a flat list of nodes shares one implementation.
internal static class MetadataTestHelpers {
    internal static IEnumerable<JsonElement> Flatten(JsonElement node) {
        yield return node;
        if (node.TryGetProperty("Children", out var children) && children.ValueKind == JsonValueKind.Array)
            foreach (var c in children.EnumerateArray())
                foreach (var d in Flatten(c)) yield return d;
    }
}
