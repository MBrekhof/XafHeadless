# Save contract (Task 5; refreshed for the 26.1 seed — Migration Task 2/5)

## The validating save path

Use `POST /api/save/{type}/{key}` (`XafHeadless.Api/Controllers/SaveController.cs`), body = partial
member dictionary. `type` is checked against an exposed-type allowlist (`Order`, `Employee`);
an unexposed type returns `404`.

Example — `POST api/save/Employee/{key}` with `{ "FirstName": null }`:

- **200 OK** — the change committed.
- **422 Unprocessable Entity** — a validation rule was violated. Body:
  ```json
  { "MemberErrors": { "FirstName": "\"First Name\" must not be empty." }, "Messages": ["\"First Name\" must not be empty."] }
  ```
  `MemberErrors` maps member name -> message (one entry per member named by a failing rule);
  `Messages` is the flat list of all failing rule messages. Nothing commits on the 422 path —
  validation runs in an `ObjectSpace.Committing` handler, which throws before `CommitChanges()`
  can persist anything. Verified by re-reading the object afterward
  (`GET api/odata/Employee({key})?$select=FirstName`) — the original value is unchanged.

## Platform finding (kill-gate evidence)

**The standalone XAF Web API host runs NO XAF validation on OData writes.** Plain OData
PATCH/PUT/DELETE against any `options.BusinessObject<T>()` entity are completely non-validating on
their own — a PATCH that violates a real `[RuleRequiredField]` rule would return `204 No Content`
and silently persist the invalid value.

Root cause: `DevExpress.ExpressApp.Validation.PersistenceValidationController` is a **ViewController**
— it only wires into `IObjectSpace.Committing` from `OnActivated`/`OnDeactivated`, both of which
require a live `View`/`Frame`. A headless Web API host never creates one, so the controller never
activates and `Committing` is never subscribed. `IValidator` itself *is* registered in DI
(`AddXafAspNetCore` calls `AddAspNetCoreValidation()` unconditionally), but nothing in the CRUD/OData
pipeline calls it.

`SaveController` works around this by wiring `IValidator` itself: it subscribes to
`ObjectSpace.Committing` and calls `IValidator.RuleSet.ValidateAllTargets(...)` before
`CommitChanges()` is allowed to proceed.

### The OData write surface is now BLOCKED, not merely documented

**Migration Task 1 closed SEC-001.** `Middleware/ODataReadOnlyMiddleware.cs` rejects any non-GET
request under `/api/odata` — `405 Method Not Allowed`, `Allow: GET`, JSON body — before it reaches
the OData pipeline at all. This is enforced (`ODataWriteGuardTests`: authed POST/PATCH/DELETE → 405,
GET → 200), not a warning clients are trusted to heed. **Any client of this host must go through a
validating save endpoint like `SaveController`** — raw OData writes are not merely discouraged, they
are guarded off at the middleware layer.

## Closed: the old Required-flag mismatch

The POC's `Status` field example (the production app's own module had **zero** real
`[RuleRequiredField]` rules anywhere, so `SaveController`'s 422 there was enforcing an ad-hoc
POC-only runtime rule registered via `CustomizeApplicationRuntimeRules`, invisible to metadata
introspection) **does not exist on the 26.1 seed.** The demo model's `Employee` type carries **real**
declarative rules (`FirstName`/`LastName`/`Title`/`Email`/`Address`/`City`, all
`[RuleRequiredField]`), so `GET api/model/views/Employee_DetailView` reports `Required:true` on
`FirstName` and the save path's 422 on a null `FirstName` is enforcing exactly that declared rule —
**metadata and save behavior now agree.** The old mismatch was an artifact of the POC's ad-hoc
runtime rule standing in for a model that had no real required-field rules to demonstrate against; it
is not a recurring platform trait, and no ad-hoc runtime rule was carried into this host (there is
none registered in `Startup.cs`).
