using Microsoft.AspNetCore.Components;
using XafHeadless.Components.Services;

namespace XafHeadless.Components.Components;

// Cascaded down the LayoutNodeRenderer tree so every editor can read its current value, write edits
// into the shared dirty dictionary, read its validation error, and reach the ApiClient (lookup data).
// XafDetailView owns Values (originally loaded), Changes (dirty), and Errors (from a 422); a NEW
// DetailViewState instance is created whenever those change so the cascade re-propagates and editors
// re-render (e.g. to show freshly-arrived validation errors).
public class DetailViewState {
    public string ObjectKey { get; init; } = "";
    // This detail object's own key member name (ViewMetadata.KeyMember, e.g. Order's "ID"). A nested
    // master-detail list uses it to filter the child on the related master's key: "{nav}/{KeyMember}".
    public string KeyMember { get; init; } = "";
    public IReadOnlyDictionary<string, object?> Values { get; init; } = new Dictionary<string, object?>();
    public Dictionary<string, object?> Changes { get; init; } = new();
    public IReadOnlyDictionary<string, string> Errors { get; init; } = new Dictionary<string, string>();
    public ApiClient Api { get; init; } = default!;
    public IReadOnlyDictionary<string, RenderFragment<object?>> MemberTemplates { get; init; } =
        new Dictionary<string, RenderFragment<object?>>();
    public Action? OnChanged { get; init; }

    public object? Current(string member) =>
        Changes.TryGetValue(member, out var v) ? v : Values.GetValueOrDefault(member);

    public string? Error(string member) => Errors.GetValueOrDefault(member);

    public void Set(string member, object? value) {
        DetailBinding.ApplyChange(Changes, Values.GetValueOrDefault(member), member, value);
        OnChanged?.Invoke();
    }
}
