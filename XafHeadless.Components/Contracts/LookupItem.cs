namespace XafHeadless.Components.Contracts;

// PH2-005 / LOOKUP-001: one candidate from api/lookup/{type}. Text is resolved SERVER-side by walking the
// same display path the projector puts on the wire, so a combo and a grid cell show identical text for the
// same object.
public record LookupItem(string Key, string Text);
