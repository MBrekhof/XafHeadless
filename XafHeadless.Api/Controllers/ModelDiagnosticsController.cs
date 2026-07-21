using DevExpress.ExpressApp.Core.Internal;
using DevExpress.ExpressApp.Model;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace XafHeadless.Api.Controllers;

// The spike endpoint: proves the UI-less host builds the Views application model for the referenced
// module, and lets the spike inspect any list/detail pair. The model lives on the shared
// XafApplication, reachable via ISharedApplicationProvider (registered as a singleton by
// AddXafWebApi). Under multi-tenancy the shared application uses an in-memory database, so this
// endpoint is tenant-independent — it reports the model, not tenant data.
[ApiController, Route("api/diagnostics"), Authorize]
public class ModelDiagnosticsController : ControllerBase {
    readonly ISharedApplicationProvider applicationProvider;
    public ModelDiagnosticsController(ISharedApplicationProvider applicationProvider)
        => this.applicationProvider = applicationProvider;

    [HttpGet("model")]
    public IActionResult GetModel(
        [FromQuery] string listView = "Order_ListView",
        [FromQuery] string detailView = "Order_DetailView") {
        var model = applicationProvider.GetContainer().Application.Model;
        var list = model.Views[listView] as IModelListView;
        var detail = model.Views[detailView] as IModelDetailView;
        return Ok(new {
            ViewsCount = model.Views.Count,
            ListViewId = listView,
            DetailViewId = detailView,
            HasListView = list != null,
            HasDetailView = detail != null,
            ListViewColumns = list?.Columns.Where(c => c.Index >= 0).Select(c => c.Id).ToArray() ?? Array.Empty<string>(),
            DetailViewCollections = detail?.Items.OfType<IModelPropertyEditor>()
                .Where(pe => pe.ModelMember?.MemberInfo?.IsList == true)
                .Select(pe => pe.PropertyName).ToArray() ?? Array.Empty<string>()
        });
    }
}
