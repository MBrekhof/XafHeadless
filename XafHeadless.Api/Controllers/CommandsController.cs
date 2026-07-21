using DevExpress.ExpressApp;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OutlookInspiredDemo.Module.BusinessObjects;
using XafHeadless.Api.Commands;

namespace XafHeadless.Api.Controllers;

// Task 6: generic command endpoint. Client Task 10 binds a button to POST /api/commands/{commandId}
// with { ObjectKeys } -> { Success, Message, RefreshKeys }. Commands resolve by Id out of DI
// (IEnumerable<IHeadlessCommand>), each executing against the same SECURED ObjectSpace pattern
// SaveController uses (IObjectSpaceFactory.CreateObjectSpace(Type), non-generic -- verified there).
[ApiController, Route("api/commands"), Authorize]
public class CommandsController : ControllerBase {
    readonly IObjectSpaceFactory osFactory;
    readonly IEnumerable<IHeadlessCommand> commands;
    public CommandsController(IObjectSpaceFactory osFactory, IEnumerable<IHeadlessCommand> commands) {
        this.osFactory = osFactory;
        this.commands = commands;
    }

    [HttpPost("{commandId}")]
    public IActionResult Execute(string commandId, [FromBody] CommandRequest request) {
        var command = commands.FirstOrDefault(c => c.Id.Equals(commandId, StringComparison.OrdinalIgnoreCase));
        if (command is null) return NotFound();
        var keys = request?.ObjectKeys ?? Array.Empty<string>(); // null-body/omitted-property guard
        using var os = osFactory.CreateObjectSpace(typeof(Order));
        return Ok(command.Execute(os, keys));
    }
}
