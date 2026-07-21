namespace XafHeadless.Api.Commands;

public record CommandRequest(string[]? ObjectKeys); // nullable: non-nullable ref type triggers ASP.NET Core's implicit-required model validation -> 400 before the controller ever runs
public record CommandResult(bool Success, string Message, string[] RefreshKeys);

// Task 6 command registry contract: DI resolves IEnumerable<IHeadlessCommand>, CommandsController
// picks one by Id. Execute gets a SECURED ObjectSpace (see CommandsController) -- same security
// posture as the OData/save paths, no bypass.
public interface IHeadlessCommand {
    string Id { get; }
    CommandResult Execute(DevExpress.ExpressApp.IObjectSpace os, string[] objectKeys);
}
