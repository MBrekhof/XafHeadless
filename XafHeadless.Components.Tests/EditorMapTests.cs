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
    });

    static LayoutNode ItemWith(string? editor) =>
        new("item", "M", "M", editor, true, false, null, null, null, null, null, null);

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

    [TestMethod]
    public void Default_map_covers_every_classify_data_type_scalar_hint() {
        // The hints ViewMetadataProjector.ClassifyDataType can emit for a DetailView *item*.
        foreach (var hint in new[] { "string", "int", "decimal", "date", "dateonly", "bool", "enum", "lookup" })
            Assert.IsNotNull(EditorMap.Default.Resolve(hint), $"no editor mapped for '{hint}'");
    }
}
