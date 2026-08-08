namespace XafHeadless.Components.Contracts;

// Client-side parse of the save contract (docs/notes/save-contract.md): 200 -> Success with no
// errors; 422 -> { MemberErrors: { member: msg }, Messages: [...] }, nothing committed.
// CRUD-001: Key carries the SERVER-generated key of a newly created object (POST api/save/{type} answers
// 201 with it -- the client never sends one). Null for updates and for any failed call. Added last with a
// default so every existing 3-arg construction keeps compiling.
public record SaveOutcome(bool Success, Dictionary<string, string> MemberErrors, string[] Messages,
    string? Key = null);
