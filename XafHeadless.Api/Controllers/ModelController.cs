using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using XafHeadless.Api.Metadata;

namespace XafHeadless.Api.Controllers;

[ApiController, Route("api/model"), Authorize]
public class ModelController : ControllerBase {
    readonly ViewMetadataProjector projector;
    readonly NavigationProjector navigationProjector;
    public ModelController(ViewMetadataProjector projector, NavigationProjector navigationProjector) {
        this.projector = projector;
        this.navigationProjector = navigationProjector;
    }

    [HttpGet("views/{viewId}")]
    public IActionResult GetView(string viewId) {
        var meta = viewId.EndsWith("_DetailView")
            ? projector.ProjectDetailView(viewId)   // Task 4; returns null until then
            : projector.ProjectListView(viewId);
        return meta is null ? NotFound() : Ok(meta);
    }

    // GAP-004: minimal flat, security-trimmed client menu.
    [HttpGet("navigation")]
    public IActionResult GetNavigation() => Ok(navigationProjector.ProjectNavigation());
}
