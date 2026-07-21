using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Core.Internal;
using DevExpress.ExpressApp.Model;
using DevExpress.ExpressApp.Security;
using DevExpress.ExpressApp.SystemModule;
using DevExpress.ExpressApp.WebApi.Services;
using Microsoft.Extensions.Options;

namespace XafHeadless.Api.Metadata;

// GAP-004 (MINIMAL scope): projects the model's navigation tree into a flat, security-trimmed client
// menu. Deliberately NOT the faithful nav tree -- no groups, no icons, CanNavigate folded into CanRead.
//
// Nav-model source (verified against C:\Program Files\DevExpress 26.1\Components\Sources\
// DevExpress.ExpressApp\DevExpress.ExpressApp\SystemModule\ShowNavigationItemController.cs):
//   - IModelApplicationNavigationItems.NavigationItems -> IModelRootNavigationItems.AllItems
//     (IModelList<IModelNavigationItem>). IModelApplicationNavigationItems is an EXTENDER interface
//     added to IModelApplication by ShowNavigationItemController.ExtendModelInterfaces
//     (`extenders.Add<IModelApplication, IModelApplicationNavigationItems>()`, line ~807) -- always
//     present, ShowNavigationItemController lives in the core SystemModule every XAF app loads. Cast
//     `model` to it, same pattern ViewMetadataProjector uses for its own extender interfaces.
//   - AllItems is ALREADY FLATTENED by XAF itself: ModelNavigationItemsDomainLogic.Get_AllItems walks
//     navigationItems.Items recursively (CollectItemsWithView) and keeps only items with item.View !=
//     null -- i.e. leaf items, in nav-tree order, groups excluded automatically. No manual flattening
//     needed here.
//   - item.Caption falls back to the view's Caption via ModelNavigationItemDomainLogic.Get_Caption
//     (a DomainLogic default-value calculator for IModelBaseChoiceActionItem.Caption) whenever the
//     item's own Caption isn't explicitly set in the model -- reading item.Caption directly already
//     gets "item caption, falling back to view caption" for free.
//
// OData-exposure check -- CORRECTED after a live-host finding (do not use IEdmModel.EntityContainer.
// EntitySets(); see below): the true signal is WebApiOptions.BusinessObjects, the exact
// Collection<Type> Startup.cs's options.BusinessObject<T>() calls populate (WebApiOptions.cs:
// BusinessObject<T> just does `BusinessObjects.Add(typeof(T))`, nothing more). It's read back here via
// the standard `IOptions<WebApiOptions>` DI container -- the SAME options object Startup.cs configures
// -- so this can never drift out of sync with Startup.cs without being a second hardcoded copy.
//
// Why EntitySets() was wrong (verified live against the running host, Admin token):
//   GET api/model/navigation initially included {"Caption":"Users","ViewId":"ApplicationUser_ListView"}
//   even though ApplicationUser is NEVER passed to options.BusinessObject<T>() in Startup.cs. Root
//   cause (DevExpress.ExpressApp.WebApi.Services.EdmModelCustomizers.TypesInfoEdmModelCustomizer.
//   CustomizeEdmModel): AddEntitySet is called for the BusinessObjects list AND for every type its
//   RequiredTypesCollector walk pulls in transitively (association/lookup targets) -- ApplicationUser
//   is reachable that way (Employee/ApplicationUser association) and gets a real EDM EntitySet even
//   though it was never independently registered. Confirmed the practical consequence live:
//     GET api/odata/ApplicationUser -> 404 (not queryable), while
//     GET api/model/views/ApplicationUser_ListView -> 200 (the ListView metadata itself projects fine).
//   So EntitySets() answers "does this type appear anywhere in the EDM schema" (too broad -- exactly
//   the /list/{viewId} 404-on-data-fetch trap rule 2 exists to prevent), not "is api/odata/{type}
//   independently queryable". WebApiOptions.BusinessObjects answers the latter directly, and Order/
//   Customer/Product/Employee/Evaluation (all explicitly registered) were confirmed live to be queryable.
public class NavigationProjector {
    readonly IModelApplication model;
    readonly ISecurityProvider securityProvider;
    readonly IObjectSpaceFactory objectSpaceFactory;
    readonly HashSet<Type> exposedTypes;

    public NavigationProjector(ISharedApplicationProvider applicationProvider, ISecurityProvider securityProvider,
        IObjectSpaceFactory objectSpaceFactory, IOptions<WebApiOptions> webApiOptions) {
        model = applicationProvider.GetContainer().Application.Model;
        this.securityProvider = securityProvider;
        this.objectSpaceFactory = objectSpaceFactory;
        exposedTypes = new HashSet<Type>(webApiOptions.Value.BusinessObjects);
    }

    public List<NavigationItemDto> ProjectNavigation() {
        var security = (IRequestSecurityStrategy)securityProvider.GetSecurity();
        var navItems = ((IModelApplicationNavigationItems)model).NavigationItems.AllItems; // pre-flattened, ordered

        var result = new List<NavigationItemDto>();
        var seenTypes = new HashSet<Type>();
        foreach (var item in navItems) {
            if (item.View is not IModelListView listView) continue;    // rule 1: ListView only
            var type = listView.ModelClass.TypeInfo.Type;
            seenTypes.Add(type);
            if (!exposedTypes.Contains(type)) continue;                 // rule 2: OData-exposed (independently queryable)
            using var os = objectSpaceFactory.CreateObjectSpace(type);
            if (!security.CanRead(type, os)) continue;                  // rule 3: CanRead (folds CanNavigate)
            result.Add(new NavigationItemDto(item.Caption, item.View.Id));
        }

        // SVR-001 Dispatch H finding: a .WithSharedBusinessObjects type ([DefaultClassOptions], e.g.
        // JobDefinition/JobExecutionRecord) can be IsNavigationItem=true with a real DefaultListView in
        // BOModel yet still be ABSENT from the generated NavigationItems.AllItems tree above -- verified
        // live (api/diagnostics probe, since reverted): NavigationItemNodeGenerator (XAF's SystemModule,
        // walks Application.BOModel.GetUnsorted() to build the Default nav group) evidently runs before
        // WithSharedBusinessObjects merges these types into BOModel, so the auto-generated nav item
        // never materializes for the life of the shared Application singleton -- even though model.Views
        // [...] and security.CanRead(type, os) both fully reflect the type (CanRead confirmed TRUE for
        // Admin on JobDefinition, live). So: fall back to BOModel directly for any IsNavigationItem
        // class the tree above never produced an item for, applying the SAME rules 2+3.
        foreach (var modelClass in model.BOModel.GetUnsorted()) {
            var type = modelClass.TypeInfo.Type;
            if (seenTypes.Contains(type)) continue;
            if (((IModelClassNavigation)modelClass).IsNavigationItem != true) continue;
            if (modelClass.DefaultListView is not { } defaultListView) continue;
            if (!exposedTypes.Contains(type)) continue;                 // rule 2
            using var os = objectSpaceFactory.CreateObjectSpace(type);
            if (!security.CanRead(type, os)) continue;                  // rule 3
            result.Add(new NavigationItemDto(defaultListView.Caption, defaultListView.Id));
        }
        return result;
    }
}
