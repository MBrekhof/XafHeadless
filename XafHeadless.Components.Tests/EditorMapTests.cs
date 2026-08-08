using XafHeadless.Components.Components.Editors;
using XafHeadless.Components.Contracts;

namespace XafHeadless.Components.Tests;

[TestClass]
public class EditorMapTests {
    // Synthetic map keeps the resolution logic under test independent of the concrete Blazor
    // editor component types (those are exercised by the build + browser smoke). Marker Types only.
    static readonly EditorMap Map = new(new Dictionary<string, Type> {
        ["string"] = typeof(string),
        ["int"] = typeof(int),
        ["HyperLinkPropertyEditor"] = typeof(Uri),   // stands in for a declared-alias editor
    });

    static LayoutNode ItemWith(string? editor, string? alias = null) =>
        new("item", "M", "M", editor, true, false, null, null, null, null, null, null,
            Aggregated: null, EditorAlias: alias);

    [TestMethod]
    public void Resolve_returns_mapped_type_for_a_known_hint() {
        Assert.AreEqual(typeof(string), Map.Resolve("string"));
        Assert.AreEqual(typeof(int), Map.Resolve("int"));
    }

    [TestMethod]
    public void Resolve_returns_null_for_an_unsupported_hint() {
        Assert.IsNull(Map.Resolve("richtext"));   // graceful-degradation trigger -> badge
        Assert.IsNull(Map.Resolve((string?)null));
    }

    [TestMethod]
    public void Resolve_from_layout_node_uses_its_editor_hint() {
        Assert.AreEqual(typeof(string), Map.Resolve(ItemWith("string")));
        Assert.IsNull(Map.Resolve(ItemWith("collection")));   // not in map -> unsupported
    }

    // EDIT-001: an app can DECLARE an editor with [EditorAlias] and the projector now sends it. When we
    // implement that alias it wins over the CLR-derived hint -- that is the whole point of declaring it.
    [TestMethod]
    public void A_declared_alias_wins_over_the_clr_derived_hint() {
        Assert.AreEqual(typeof(Uri), Map.Resolve(ItemWith("string", alias: "HyperLinkPropertyEditor")));
    }

    // ...but an alias we do NOT implement must fall back to the CLR hint, never to the badge. A
    // DxHtmlPropertyEditor string still reads and edits perfectly well as text, so degrading it to
    // "unsupported editor" would REMOVE a working editor -- a regression dressed up as honesty.
    [TestMethod]
    public void An_unimplemented_alias_falls_back_to_the_clr_hint_not_to_the_badge() {
        Assert.AreEqual(typeof(string), Map.Resolve(ItemWith("string", alias: "DxHtmlPropertyEditor")));
        Assert.AreEqual(typeof(string), Map.Resolve(ItemWith("string", alias: "PdfViewerPropertyEditor")));
    }

    [TestMethod]
    public void Default_map_covers_every_classify_data_type_scalar_hint() {
        // The hints ViewMetadataProjector.ClassifyDataType can emit for a DetailView *item*.
        foreach (var hint in new[] { "string", "int", "decimal", "date", "dateonly", "bool", "enum", "lookup" })
            Assert.IsNotNull(EditorMap.Default.Resolve(hint), $"no editor mapped for '{hint}'");
    }
}
