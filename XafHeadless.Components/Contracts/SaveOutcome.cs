namespace XafHeadless.Components.Contracts;

// Client-side parse of the save contract (docs/notes/save-contract.md): 200 -> Success with no
// errors; 422 -> { MemberErrors: { member: msg }, Messages: [...] }, nothing committed.
public record SaveOutcome(bool Success, Dictionary<string, string> MemberErrors, string[] Messages);
