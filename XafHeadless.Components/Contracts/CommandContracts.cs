namespace XafHeadless.Components.Contracts;

// Mirror of XafHeadless.Api/Commands/IHeadlessCommand.cs -- same property names, do not rename.
public record CommandRequest(string[]? ObjectKeys);
public record CommandResult(bool Success, string Message, string[] RefreshKeys);
