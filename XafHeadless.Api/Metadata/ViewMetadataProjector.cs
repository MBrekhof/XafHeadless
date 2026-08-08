using DevExpress.ExpressApp;
using DevExpress.ExpressApp.ConditionalAppearance;
using DevExpress.ExpressApp.Core.Internal;
using DevExpress.ExpressApp.DC;
using DevExpress.ExpressApp.Model;
using DevExpress.ExpressApp.Security;
using DevExpress.ExpressApp.Utils;
using DevExpress.ExpressApp.WebApi.Services;
using Microsoft.Extensions.Options;

namespace XafHeadless.Api.Metadata;

// Projects the shared XAF application model for a view, then TRIMS it against the current request
// user's SecurityStrategy. Three binding contract rules:
//   1. Unreadable members are OMITTED (not flagged) — they never appear in the payload.
//   2. Allow flags are model AND security (server-side): a client can't re-enable an action.
//   3. Enum/lookup metadata is projected declaratively so the client renders without extra calls.
// Security is asked, never re-implemented: every permission decision routes through IsGrantedExtensions
// on the request SecurityStrategy (verified 25.2 API — the type/member overloads require an IObjectSpace;
// the ObjectSpace-less overloads are [Obsolete]).
public class ViewMetadataProjector {
    readonly IModelApplication model;
    readonly ISecurityProvider securityProvider;
    readonly IObjectSpaceFactory objectSpaceFactory;
    readonly HashSet<Type> exposedTypes;

    public ViewMetadataProjector(ISharedApplicationProvider applicationProvider,
        ISecurityProvider securityProvider, IObjectSpaceFactory objectSpaceFactory,
        IOptions<WebApiOptions> webApiOptions) {
        // IModelApplication is not directly injectable in this headless host — reach it via the shared
        // XafApplication (Task 1 pattern), which is the same model the standard XAF UI would build.
        model = applicationProvider.GetContainer().Application.Model;
        this.securityProvider = securityProvider;
        this.objectSpaceFactory = objectSpaceFactory;
        // BUG-002: the set of types api/odata/{type} independently serves — the exact list Startup.cs's
        // options.BusinessObject<T>() calls populate (same signal NavigationProjector uses; NOT the broader
        // EDM entity-set list, which includes transitive association/lookup targets that 404 on data fetch).
        exposedTypes = new HashSet<Type>(webApiOptions.Value.BusinessObjects);
    }

    public ViewMetadata? ProjectListView(string viewId) {
        if (model.Views[viewId] is not IModelListView list) return null;
        var type = list.ModelClass.TypeInfo.Type;
        var security = (IRequestSecurityStrategy)securityProvider.GetSecurity();
        using var os = objectSpaceFactory.CreateObjectSpace(type);
        var columns = list.Columns
            .Where(c => c.Index >= 0 && c.ModelMember?.MemberInfo != null)
            .Where(c => security.CanRead(type, os, c.ModelMember!.MemberInfo.Name)) // rule 1: omit, don't flag
            .OrderBy(c => c.Index)
            .Select(ProjectColumn).ToList();
        var appearance = ProjectAppearance(list.ModelClass, viewId, isDetailView: false);
        var appearanceEnums = ProjectAppearanceEnums(list.ModelClass, columns.Select(c => c.Member), appearance, security, os, type);
        return new ViewMetadata(viewId, "ListView", type.Name, list.ModelClass.TypeInfo.KeyMember.Name, list.Caption,
            new AllowSet(                                                    // rule 2: model ∩ security
                Edit: list.AllowEdit && security.CanWrite(type, os),
                New: list.AllowNew && security.CanCreate(type, os),
                Delete: list.AllowDelete && security.CanDelete(type, os)),
            columns, Layout: null, Actions: new(),
            Appearance: appearance, AppearanceEnums: appearanceEnums);
    }

    // DetailView projection: walks the Layout tree (group/tabs/tab/item/nestedList), applying the
    // same two rules as ProjectListView at every node — rule 1 (omit unreadable members) means a
    // whole subtree can disappear, not just get flagged.
    public ViewMetadata? ProjectDetailView(string viewId) {
        if (model.Views[viewId] is not IModelDetailView detail) return null;
        var type = detail.ModelClass.TypeInfo.Type;
        var security = (IRequestSecurityStrategy)securityProvider.GetSecurity();
        using var os = objectSpaceFactory.CreateObjectSpace(type);

        bool CanReadMember(IMemberInfo mi) => security.CanRead(type, os, mi.Name);
        bool CanWriteMember(IMemberInfo mi) => security.CanWrite(type, os, mi.Name);

        LayoutNode? WalkViewItem(IModelLayoutViewItem item) {
            if (item.ViewItem is not IModelPropertyEditor pe || pe.ModelMember?.MemberInfo is not { } mi) return null;
            if (!CanReadMember(mi)) return null;                             // rule 1: omit
            var caption = ((IModelViewItem)pe).Caption;                     // ambiguous with IModelCommonMemberViewItem.Caption
            if (IsBusinessObjectCollection(mi)) {
                // BUG-002: only project a nested collection whose child type is INDEPENDENTLY OData-queryable.
                // A nestedList over a non-exposed child type (e.g. Customer.Employees -> CustomerEmployee, a
                // join entity never passed to options.BusinessObject<T>()) would 404 on the child grid's
                // api/odata/{type} data fetch -> the raw "Failed to load data ... 404" the owner saw. Omit the
                // tab instead of rendering a broken one -- same exposure signal the nav menu filters on.
                if (!exposedTypes.Contains(mi.ListElementTypeInfo!.Type)) return null;
                var nestedViewId = pe.View?.Id ?? $"{mi.ListElementTypeInfo.Type.Name}_ListView";
                var masterKey = mi.AssociatedMemberInfo?.Name ?? "";
                // GAP-010: IsAggregated tells the client Link/Unlink (shared) vs New/Delete (owned).
                return new LayoutNode("nestedList", caption, pe.PropertyName, null, null, null, null,
                    nestedViewId, masterKey, null, null, null, mi.IsAggregated);
            }
            // BUG-001: a member with IsList but a non-business-object element type is a blob (byte[] -> an
            // "image" item), NOT a nested collection view. Render byte[] as an image item; omit any other
            // non-entity "list" -- there is no real "{Element}_ListView" to fetch, and projecting one
            // produced a metadata request for e.g. "Byte_ListView" that 404s (Employee.Photo/Customer.Logo).
            if (mi.IsList && ClassifyDataType(mi) != "image") return null;
            return new LayoutNode("item", caption, pe.PropertyName, ClassifyDataType(mi),
                AllowWrite: pe.AllowEdit && CanWriteMember(mi),              // rule 2: model ∩ security
                Required: !mi.MemberTypeInfo?.Type.IsValueType == true && IsRequired(pe),
                MaxLength: pe.ModelMember.Size > 0 ? pe.ModelMember.Size : null,
                null, null, ProjectLookup(mi), ProjectEnum(mi), null,
                EditorAlias: ResolveEditorAlias(mi));
        }

        // .OfType<IModelNode>() (not a raw LINQ .Select() on the IModelList<T> interface) matches the
        // brief's original enumeration shape — kept for consistency; behaves identically here since
        // IModelList<T> is a plain IList<T>. NOTE: the POC entity's DetailView "SimpleEditors" group covers
        // ~180 scalar members, and ModelDetailViewLayoutNodesGenerator.LayoutInTwoColumns recursively
        // bisects large editor sets into nested 2-column sub-groups — a genuinely deep (14-20+ level),
        // non-cyclic tree. See Startup.cs's JsonSerializerOptions.MaxDepth bump for the actual fix.
        LayoutNode? WalkLayout(IModelNode node) => node switch {
            IModelTabbedGroup tabs => new LayoutNode("tabs", null, null, null, null, null, null, null, null, null, null,
                tabs.OfType<IModelLayoutGroup>().Select(WalkTab).Where(t => t is not null).Select(t => t!).ToList()),
            IModelLayoutGroup group => Prune(new LayoutNode("group",
                group.ShowCaption == true ? group.Caption : null, null, null, null, null, null, null, null, null, null,
                group.OfType<IModelNode>().Select(WalkLayout).Where(c => c is not null).Select(c => c!).ToList())),
            IModelLayoutViewItem item => WalkViewItem(item),
            _ => null
        };

        LayoutNode? WalkTab(IModelLayoutGroup tab) {
            var children = tab.OfType<IModelNode>().Select(WalkLayout).Where(c => c is not null).Select(c => c!).ToList();
            return children.Count == 0 ? null
                : new LayoutNode("tab", tab.Caption, null, null, null, null, null, null, null, null, null, children);
        }

        // detail.Layout is the IModelViewLayout container (an IModelList<IModelViewLayoutElement>), not
        // itself a group/tabs/item — ModelDetailViewLayoutNodesGenerator always adds exactly one root
        // "Main" IModelLayoutGroup under it (verified: DevExpress.ExpressApp\Model\NodeGenerators\
        // ModelDetailViewLayoutNodesGenerator.cs:89), so walk its children and take the (single) result.
        var layout = detail.Layout.OfType<IModelNode>().Select(WalkLayout).FirstOrDefault(n => n is not null);

        var appearance = ProjectAppearance(detail.ModelClass, viewId, isDetailView: true);
        var appearanceEnums = ProjectAppearanceEnums(detail.ModelClass, CollectMembers(layout), appearance, security, os, type);
        return new ViewMetadata(viewId, "DetailView", type.Name, detail.ModelClass.TypeInfo.KeyMember.Name, detail.Caption,
            new AllowSet(detail.AllowEdit && security.CanWrite(type, os), false, false),
            Columns: null, Layout: layout, Actions: new(),
            Appearance: appearance, AppearanceEnums: appearanceEnums);
    }

    // GAP-002: project the class's ConditionalAppearance ViewItem rules that apply to THIS view. The
    // rules live on the IModelClass (IModelConditionalAppearance.AppearanceRules, registered by the
    // ConditionalAppearance module which loads via RequiredModuleTypes even though the WebApi builder
    // has no AddConditionalAppearance() — HOW-TO-IMPLEMENT gotcha 14). AppearanceController.GetRulesFromModel
    // is the framework's own read path (public static; walks base classes too) — verified against installed
    // 26.1 source (DevExpress.ExpressApp.ConditionalAppearance/AppearanceController.cs:221). Each rule node
    // is an IAppearanceRuleProperties: Criteria/TargetItems/AppearanceItemType/Context (strings) +
    // FontColor/BackColor (System.Drawing.Color?) + FontStyle (DXFontStyle?). We honor Context with the same
    // rule the framework's AppearanceController.IsRuleFitToContext uses (source ibid.:196).
    static IReadOnlyList<AppearanceRuleDto> ProjectAppearance(IModelClass modelClass, string viewId, bool isDetailView) {
        var currentContexts = new[] { viewId, isDetailView ? "DetailView" : "ListView" };
        var result = new List<AppearanceRuleDto>();
        foreach (IAppearanceRuleProperties rule in AppearanceController.GetRulesFromModel(modelClass)) {
            if (rule.AppearanceItemType != AppearanceController.AppearanceViewItemType) continue; // ViewItem only
            if (!ContextFits(rule.Context, currentContexts)) continue;
            var fontColor = rule.FontColor is { } fc ? ToCss(fc) : null;
            var backColor = rule.BackColor is { } bc ? ToCss(bc) : null;
            var fontStyle = rule.FontStyle is { } fs && fs != DevExpress.Drawing.DXFontStyle.Regular ? fs.ToString() : null;
            if (fontColor is null && backColor is null && fontStyle is null) continue; // no renderable prop (Visibility/Enabled-only)
            result.Add(new AppearanceRuleDto(rule.Criteria ?? "", SplitList(rule.TargetItems), fontColor, backColor, fontStyle));
        }
        return result;
    }

    // GAP-002 enum-fix: enum metadata for class members the appearance rules can reference but that AREN'T
    // already among the view's displayed members (columns for a ListView, layout item/nestedList members
    // for a DetailView) -- the channel AppearanceEvaluator needs to rewrite a caption literal on a non-column
    // enum member (e.g. Evaluation.Rating, referenced by "Rating='Good'" but not a column of
    // Employee_Evaluations_ListView; see docs/notes). Scoping approach (b) from the brief: every enum-typed
    // class member not already displayed, rather than parsing each rule's Criteria to find referenced
    // members -- simpler, and the set is tiny (a handful of enum members per class at most). Only computed
    // when the view actually has appearance rules, so a rule-less view pays zero extra cost. ITypeInfo.Members
    // verified against installed 26.1 source (DevExpress.ExpressApp/DC/ITypeInfo.cs).
    static IReadOnlyDictionary<string, IReadOnlyList<EnumValueMetadata>>? ProjectAppearanceEnums(
            IModelClass modelClass, IEnumerable<string> displayedMembers, IReadOnlyList<AppearanceRuleDto> appearance,
            IRequestSecurityStrategy security, IObjectSpace os, Type type) {
        if (appearance.Count == 0) return null;
        var displayed = new HashSet<string>(displayedMembers, StringComparer.Ordinal);
        Dictionary<string, IReadOnlyList<EnumValueMetadata>>? result = null;
        foreach (var mi in modelClass.TypeInfo.Members) {
            if (displayed.Contains(mi.Name)) continue;
            if (ProjectEnum(mi) is not { } values) continue;              // enum-typed members only
            if (!security.CanRead(type, os, mi.Name)) continue;          // rule 1: omit unreadable members
            (result ??= new(StringComparer.Ordinal))[mi.Name] = values;
        }
        return result;
    }

    // Collects every Member name in a projected DetailView layout tree ("item" and "nestedList" nodes both
    // carry Member; "group"/"tabs"/"tab" recurse via Children) -- the "already displayed" set for
    // ProjectAppearanceEnums on a DetailView (ListView uses its flat Columns list instead).
    static IEnumerable<string> CollectMembers(LayoutNode? node) {
        if (node is null) yield break;
        if (node.Member is not null) yield return node.Member;
        if (node.Children is not null)
            foreach (var child in node.Children)
                foreach (var m in CollectMembers(child))
                    yield return m;
    }

    // Mirror of AppearanceController.IsRuleFitToContext (verified against 26.1 source): no contexts -> all
    // views; else the rule's contexts (a view id and/or "ListView"/"DetailView"/"Any") must intersect the
    // current view's contexts, with "Any" inverting to "everywhere but the listed contexts".
    static bool ContextFits(string? context, string[] currentContexts) {
        var targets = SplitList(context);
        if (targets.Count == 0) return true;
        var inCurrent = targets.Any(currentContexts.Contains);
        return targets.Contains(AppearanceController.AppearanceContextAny) ? !inCurrent : inCurrent;
    }

    // Same delimiter/trim rule as AppearanceRule.Parse (Delimiters = ',' ';').
    static List<string> SplitList(string? items) =>
        string.IsNullOrEmpty(items) ? new()
            : items.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();

    static string ToCss(System.Drawing.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    static LayoutNode? Prune(LayoutNode g) => g.Children is { Count: > 0 } ? g : null;
    static bool IsRequired(IModelPropertyEditor pe) =>
        pe.ModelMember.MemberInfo.FindAttributes<DevExpress.Persistent.Validation.RuleRequiredFieldAttribute>().Any();

    static ColumnMetadata ProjectColumn(IModelColumn c) {
        var mi = c.ModelMember!.MemberInfo;
        var sortOrder = c.SortOrder.ToString();
        return new ColumnMetadata(c.PropertyName, c.Caption,
            ClassifyDataType(mi), c.SortIndex >= 0 ? c.SortIndex : (int?)null,
            sortOrder is "None" ? null : sortOrder.ToLowerInvariant(),
            ProjectLookup(mi), ProjectEnum(mi));
    }

    // DATA-001: the SINGLE lookup predicate. ClassifyDataType (which stamps DataType/Editor) and
    // ProjectLookup (which populates the Lookup metadata) used to gate on two DIFFERENT conditions —
    // ClassifyDataType on `IsDomainComponent || (IsAssociation && !IsList)`, ProjectLookup on
    // `!IsList && IsPersistent` — so nothing structurally guaranteed they'd agree; a member could be
    // stamped "lookup" with a null Lookup, or a non-"lookup" with a populated Lookup. Both now route
    // through this one function, so the classification and the projection can NEVER disagree by
    // construction, for ANY member. Verified against installed 26.1 source (IMemberInfo.cs / ITypeInfo.cs):
    // IsList + MemberTypeInfo are on IMemberInfo; IsDomainComponent + IsPersistent are on the target
    // ITypeInfo. (Note: for the EF Core provider, EFCoreTypeInfoSource.InitTypeInfo co-sets
    // IsDomainComponent AND IsPersistent = true on every mapped entity type, so the two are equivalent
    // over EF entity targets — this predicate is a behavior-preserving de-duplication for such members,
    // and additionally closes the latent gap for a non-persistent DomainComponent target, where they'd
    // differ. IsAssociation is deliberately NOT read: an inverse-less one-way reference — verified
    // IsAssociation==false via the host-owned LookupProbe.Ref fixture — still classifies as a lookup on
    // the strength of the target being a persistent/domain-component type, not on having an inverse.)
    static bool IsLookupMember(IMemberInfo mi) =>
        !mi.IsList && (mi.MemberTypeInfo?.IsDomainComponent == true || mi.MemberTypeInfo?.IsPersistent == true);

    // BUG-001: the list counterpart of IsLookupMember. A member is a nested-view collection ONLY when its
    // element type is a business object. byte[] (and any non-entity IList) reports IsList == true, but its
    // element type (System.Byte, ...) is neither a domain component nor persistent -- projecting such a
    // member as a nested "{ListElementType}_ListView" asks the client to fetch a view that doesn't exist
    // (e.g. "Byte_ListView" -> 404). ListElementTypeInfo is an ITypeInfo (same IsDomainComponent/IsPersistent
    // surface as MemberTypeInfo) -- verified against installed 26.1 source (DC/IMemberInfo.cs:71).
    static bool IsBusinessObjectCollection(IMemberInfo mi) =>
        mi.IsList && (mi.ListElementTypeInfo?.IsDomainComponent == true || mi.ListElementTypeInfo?.IsPersistent == true);

    internal static string ClassifyDataType(IMemberInfo mi) {
        var t = Nullable.GetUnderlyingType(mi.MemberType) ?? mi.MemberType;
        if (t == typeof(byte[])) return "image";                             // BUG-001: blob/image; reports IsList but is not a BO collection
        if (mi.IsList) return "collection";
        if (t.IsEnum) return "enum";
        if (IsLookupMember(mi)) return "lookup";
        if (t == typeof(bool)) return "bool";
        // GRID-004 (companion-headless backport, review I1 there): DateOnly is NOT "date" -- it is Edm.Date
        // on the OData wire (bare yyyy-MM-dd literals), while "date" columns are Edm.DateTimeOffset
        // (instant literals). Conflating them handed DateOnly columns the grid's date-range filter,
        // whose zone-converted instant literal Edm.Date rejects (400) or day-shifts. "dateonly"
        // renders/edits like "date" in detail views but is excluded from server-side filter/group
        // (GridBinding); teach ODataFilterTranslator bare-date literals if DateOnly filtering
        // becomes a real ask.
        if (t == typeof(DateTime)) return "date";
        if (t == typeof(DateOnly)) return "dateonly";
        if (t == typeof(int) || t == typeof(long) || t == typeof(short)) return "int";
        if (t == typeof(decimal) || t == typeof(double) || t == typeof(float)) return "decimal";
        return "string";
    }

    static LookupMetadata? ProjectLookup(IMemberInfo mi) {
        if (!IsLookupMember(mi)) return null;
        var targetInfo = mi.MemberTypeInfo!;
        // GRID-005: keep the display member's IMemberInfo, not just its name, so its data type can be
        // classified the same way any other member is -- that type is the only thing telling the client
        // whether the display path is orderable at all.
        //
        // BUG-008: and one hop is not enough. XAF's [XafDefaultProperty] frequently points at ANOTHER
        // reference -- CustomerStore's default property is Emblem, which is an entity, not a scalar --
        // so stopping here produced a display path that resolved to an object: $expand=Store($select=
        // Emblem) returned a nav property, the cell had no text, and the column rendered permanently
        // blank. Walk the default-property chain until it lands on something that is not a reference,
        // and send the whole dotted path. Verified live: $expand=Store($expand=Emblem($select=CityName))
        // returns {"Emblem":{"CityName":"Tucson"}} and $orderby=Store/Emblem/CityName is a 200.
        //
        // The `visited` set is a cycle guard: two types whose default properties point at each other
        // would otherwise loop forever. On a cycle the walk stops on a reference, ClassifyDataType says
        // "lookup", and GRID-005's ceiling refuses to sort it -- the same honest degradation as before.
        var (path, landed) = ResolveDisplayPath(targetInfo);
        return new LookupMetadata(targetInfo.Type.Name, targetInfo.KeyMember.Name,
            path, ClassifyDataType(landed));
    }

    // BUG-008 / PH2-005: THE display path for a lookup target, and the member it lands on. Shared, because
    // two places need the identical answer and a second copy would drift: the projector puts the path on the
    // wire, and LookupController evaluates it server-side to build candidate text. The path is also a valid
    // XAF criteria path and a valid OData nav path, which is what makes both uses possible.
    //
    // Walks the default-property chain because [XafDefaultProperty] frequently points at ANOTHER reference
    // (CustomerStore's is Emblem, an entity), and stopping at one hop yields a path resolving to an object --
    // the permanently blank column BUG-008 fixed. `visited` guards a cycle: two types whose default
    // properties point at each other stop the walk on a reference, which callers then treat as unrenderable
    // / unsortable rather than looping forever.
    internal static (string Path, IMemberInfo Landed) ResolveDisplayPath(ITypeInfo targetInfo) {
        var segments = new List<string>();
        var display = targetInfo.DefaultMember ?? targetInfo.KeyMember;
        var visited = new HashSet<Type> { targetInfo.Type };
        while (IsLookupMember(display) && visited.Add(display.MemberTypeInfo!.Type)) {
            segments.Add(display.Name);
            var next = display.MemberTypeInfo!;
            display = next.DefaultMember ?? next.KeyMember;
        }
        segments.Add(display.Name);
        return (string.Join('.', segments), display);
    }

    // EDIT-001: the editor the app DECLARED, as opposed to the one its CLR type implies. Until this
    // existed the projector read only the type, so `[EditorAlias(DxHtmlPropertyEditor)] string Comments`
    // projected as a plain "string" and rendered as a text box -- silently, since nothing recorded that a
    // specific editor had been asked for.
    //
    // The resolution order MIRRORS XAF's own (verified against installed 26.1 source,
    // ModelMemberLogic.TryGetAlias): the member's attribute, then the same-named member on any implemented
    // INTERFACE, then the member's TYPE. The two fallbacks are the reason this was not written from
    // memory -- an alias declared on an interface or on a value type would otherwise be silently missed.
    //
    // Deliberately NOT IModelMember.PropertyEditorType: that resolves the alias to a platform-specific
    // editor Type (a WinForms/Blazor class) which means nothing to a headless client. The alias STRING is
    // the portable half of the contract.
    // Fully qualified rather than a `using DevExpress.Persistent.Base`: that namespace carries a lot of
    // common names and this file already juggles several DevExpress namespaces.
    static string? ResolveEditorAlias(IMemberInfo mi) {
        var attribute = mi.FindAttribute<DevExpress.Persistent.Base.EditorAliasAttribute>();
        if (attribute is null)
            foreach (var interfaceInfo in mi.Owner.ImplementedInterfaces) {
                attribute = interfaceInfo.FindMember(mi.Name)
                    ?.FindAttribute<DevExpress.Persistent.Base.EditorAliasAttribute>();
                if (attribute is not null) break;
            }
        attribute ??= mi.MemberTypeInfo?.FindAttribute<DevExpress.Persistent.Base.EditorAliasAttribute>();
        return attribute?.Alias;
    }

    static List<EnumValueMetadata>? ProjectEnum(IMemberInfo mi) {
        var t = Nullable.GetUnderlyingType(mi.MemberType) ?? mi.MemberType;
        if (!t.IsEnum) return null;
        var descriptor = new EnumDescriptor(t);
        return Enum.GetValues(t).Cast<object>()
            .Select(v => new EnumValueMetadata(Convert.ToInt64(v), descriptor.GetCaption(v), Enum.GetName(t, v))).ToList();
    }
}
