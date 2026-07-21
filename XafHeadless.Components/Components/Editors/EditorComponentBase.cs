using Microsoft.AspNetCore.Components;
using XafHeadless.Components.Contracts;

namespace XafHeadless.Components.Components.Editors;

// Shared plumbing for every editor: the item metadata (Node), the cascaded DetailViewState, and the
// read/write/error/readonly helpers so each editor .razor is just its DevExpress control + one error
// line. Editors bind one-way (Value + *Changed) and push edits through Set so the dirty dictionary
// stays the single source of truth.
public abstract class EditorComponentBase : ComponentBase {
    [CascadingParameter] public DetailViewState State { get; set; } = default!;
    [Parameter] public LayoutNode Node { get; set; } = default!;

    protected string Member => Node.Member!;
    protected bool IsReadOnly => Node.AllowWrite == false;
    protected object? Current => State.Current(Member);
    protected string? Error => State.Error(Member);
    protected void Set(object? value) => State.Set(Member, value);
}
