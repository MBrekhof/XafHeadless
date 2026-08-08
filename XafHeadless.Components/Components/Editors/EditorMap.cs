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

    // EDIT-001: an app can DECLARE an editor with [EditorAlias] and the projector now sends it as
    // EditorAlias. A declared alias we implement WINS -- that is the point of declaring it. An alias we do
    // NOT implement falls back to the CLR-derived hint, deliberately, and never to the badge: a
    // DxHtmlPropertyEditor string still reads and edits perfectly well as text, so degrading it to
    // "unsupported editor" would remove a working editor. Falling back keeps this strictly additive --
    // nothing that rendered before renders worse now.
    public Type? Resolve(LayoutNode node) => Resolve(node.EditorAlias) ?? Resolve(node.Editor);

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

        // EDIT-001: keys below are DECLARED editor aliases, not CLR-derived hints. Only the two cheap,
        // safe ones are implemented. The rest deliberately fall back to their CLR editor:
        //   DxHtmlPropertyEditor  -- rendering stored HTML is an XSS vector and needs a sanitiser or a
        //                            read-only renderer, not a naive MarkupString. Carded as EDIT-002.
        //   PdfViewerPropertyEditor, MapHomeOfficePropertyEditor, EnumImageOnlyEditor -- heavy, and two are
        //                            demo-custom editors with no general meaning.
        ["HyperLinkPropertyEditor"] = typeof(HyperLinkEditor),
        ["ProgressBarPropertyEditor"] = typeof(ProgressBarEditor),
    });
}
