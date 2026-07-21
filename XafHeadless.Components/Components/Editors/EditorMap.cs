using XafHeadless.Components.Contracts;

namespace XafHeadless.Components.Components.Editors;

// Maps an item's Editor hint (ViewMetadataProjector.ClassifyDataType output: string/int/decimal/
// date/bool/enum/lookup) to the Blazor editor component that renders it. LayoutNodeRenderer resolves
// the type here and renders it via <DynamicComponent>; a null result is the graceful-degradation
// signal -> the renderer shows the value read-only plus an "unsupported editor: {hint}" badge.
// Registered in DI as a singleton via ClientServiceRegistration.AddXafHeadlessClient() so it is
// trivially swappable/extendable.
public class EditorMap(IReadOnlyDictionary<string, Type> map) {
    public Type? Resolve(string? hint) => hint is not null && map.TryGetValue(hint, out var t) ? t : null;
    public Type? Resolve(LayoutNode node) => Resolve(node.Editor);

    public static EditorMap Default { get; } = new(new Dictionary<string, Type> {
        ["string"] = typeof(StringEditor),
        ["int"] = typeof(NumberEditor),
        ["decimal"] = typeof(NumberEditor),
        ["date"] = typeof(DateEditor),
        ["dateonly"] = typeof(DateEditor),   // GRID-004: DateOnly member -- same editor, distinct grid semantics
        ["bool"] = typeof(BoolEditor),
        ["enum"] = typeof(EnumEditor),
        ["lookup"] = typeof(LookupEditor),
        ["image"] = typeof(ImageEditor),   // BUG-001/UI-001: byte[] blob -> <img> data-URI
    });
}
