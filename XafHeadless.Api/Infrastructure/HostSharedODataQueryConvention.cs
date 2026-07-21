using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.OData.Query;

namespace XafHeadless.Api.Infrastructure;

// SVR-003: GLOBAL workaround for an OData-vs-standalone-DbContext defect that makes $filter/$top read
// WRONG data for host-shared BOs (JobDefinition, JobExecutionRecord).
//
// Root cause (proven live, see docs/DEVIATIONS.md): ASP.NET Core
// OData's [EnableQuery] parameterizes every $filter/$top literal into a
// LinqParameterContainer.TypedProperty. That container resolves to default(T) when the query executes
// on the MultiTenancy standalone shared-BO DbContext (.WithTenantResolver -> IDBContextSwitcher.
// UseStandaloneDBContext=true) -- so string filters match null, Guid filters match Guid.Empty, $top
// becomes Take(0), etc. Per-tenant types (Order) run on the normal DI-registered tenant context and are
// unaffected. Setting EnableConstantParameterization=false inlines the literals (exactly how enum
// filters already work correctly), which was validated in-process for every case (investigation B.3).
//
// The [EnableQuery] lives on DevExpress's generated DataControllerBase.Get()/Get(key)
// (DevExpress.ExpressApp.WebApi/Mvc/DataController.cs:129-134, installed 26.1 source) which app code
// can't edit. EnableQueryAttribute is an ActionFilterAttribute whose actual instance lands in
// ActionModel.Filters, and EnableConstantParameterization is a public settable bool (verified via
// reflection against Microsoft.AspNetCore.OData 9.3.2) -- so this application-model convention mutates
// that instance in place. Applied globally per the owner decision; it is a no-op for per-tenant types
// (their literals inline harmlessly -- EF Core re-parameterizes during its own SQL generation, so DB
// correctness/param-safety is unchanged, only OData-layer plan-cache reuse is lost -- negligible here).
//
// The underlying framework defect (LinqParameterContainer.TypedProperty -> default(T) on the standalone
// shared-BO context) warrants a DevExpress support ticket -- this is an app-side workaround, not a fix.
public sealed class HostSharedODataQueryConvention : IApplicationModelConvention {
    public void Apply(ApplicationModel application) {
        foreach (var controller in application.Controllers)
            foreach (var action in controller.Actions)
                foreach (var filter in action.Filters)
                    if (filter is EnableQueryAttribute enableQuery)
                        enableQuery.EnableConstantParameterization = false;
    }
}
