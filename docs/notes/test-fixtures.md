# Test fixtures — restricted-role dev users

Two generations of the same idea: a role/user pair that denies read on one member, used to prove
security trims server-side per role. The **26.1 seed** section below is the current, live fixture;
the **original POC fixture** section stays as-is — its cleanup obligation (SEC-002) is still open.

## The 26.1 seed fixture (Migration Task 2)

- **Role** `HeadlessDemo_Restricted` — grants `AddTypePermission<Order>(Read, Allow)`; explicitly
  `AddMemberPermission<Order>(Read, "TotalAmount", null, Deny)` — **DENY read on `Order.TotalAmount`**
  (a real, non-key, non-collection `Order_ListView` column and `Order_DetailView` item, confirmed
  live before it was chosen).
- **User** `restricted@company1.com` — blank password, tenant-email form so `TenantByEmailResolver`
  routes it to the `company1.com` tenant, same pattern the demo's own `Updater.EnsureUser` uses.
- **Seeded into the DISPOSABLE demo DB** — via an idempotent, gated endpoint:
  `POST api/test-fixtures/restricted-role` (`Controllers/TestFixturesController.cs`). Gated two ways:
  environment (`IWebHostEnvironment.IsDevelopment()`, else `404`) and role (an in-action
  `ISecurityStrategyBase.User` → `PermissionPolicyUser.Roles.Any(IsAdministrative)` check, else
  `403` — **not** `[Authorize(Roles=...)]`, because XAF JWTs carry no role claims; see
  `HOW-TO-IMPLEMENT.md` gotcha 13). A non-admin caller (e.g. Restricted itself) gets `403` live-verified.
  Test-side seeding runs once via MSTest `[AssemblyInitialize]`, not per-call, to avoid a
  check-then-act race across parallel test methods.
- Credentials live in `XafHeadless.Api.Tests/testsettings.json` as `Test:RestrictedUser` /
  `Test:RestrictedPassword` — **public demo fixtures**, fine to commit (unlike the original POC
  fixture below, this DB is disposable, not a shared customer dev DB).
- **Side effect, not a bug:** the restricted role also loses `Customer.Name` (no type-level
  permission on `Customer` at all) — doesn't affect any assertion (all checks key on
  `TotalAmount`/`RestrictedDeniedMember`), but worth knowing if you extend the fixture.

### Drop/reseed recovery

This DB is disposable — no cleanup obligation, just a recovery recipe if it's ever dropped or
corrupted:

1. Delete the LocalDB catalogs: the host catalog `XafHeadlessDemo` (this host's own — self-seeds on
   next run) **and**, if the tenant data itself needs resetting, the tenant catalog
   `OutlookInspiredDemo_company1` (hardcoded by the demo's own `Updater.CreateTenant`; our host never
   reseeds tenant data).
2. Rerun `XafHeadless.Api` once — it self-seeds the host catalog (`Tenant`/`Admin`/`TaxRates` rows).
3. If the tenant catalog was also dropped, either run the demo's own `Blazor.Server` app once against
   it (re-seeds the rich 55k-row demo data), or add `.WithTenantDatabaseUpdater()` to `Startup.cs` so
   the host self-seeds the tenant DB via the demo's own DataGenerator (not added by default — see
   TODO `MT-001`).
4. Re-run `POST api/test-fixtures/restricted-role` as Admin (idempotent) to reseed the restricted
   role/user, or just let the test suite's `[AssemblyInitialize]` do it on the next run.

## The original POC fixture (a separate dev database — SEC-002 still open)

### What exists

- **Role** `HeadlessPOC_Restricted` — `PermissionPolicyRole`, `DenyAllByDefault`; grants
  `AddTypePermission<PocEntity>(ReadOnlyAccess, Allow)` (read on the POC entity); explicitly
  `AddMemberPermission<PocEntity>(Read, "Status", null, Deny)` — **DENY read on the POC entity's
  `Status`** (the model's `KnownModel.PocEntityStatusColumn`, a visible/non-key `PocEntity_ListView`
  column, so the trimming is observable in both the ListView columns and the DetailView layout tree).
- **User** `HeadlessPOC_Restricted` — `PermissionPolicyUser`, roles `Default` + `HeadlessPOC_Restricted`,
  **empty password** (matches the repo convention already used by `Admin` and every `Test*` user in
  this dev DB, so the restricted auth path exercises the identical login flow — only the role differs).
- Both are **data rows in a separate (production) dev database** (`PermissionPolicyRoleBase` /
  `PermissionPolicyUser` tables) — no schema change. Created via a one-off scratch console seeder
  (`INonSecuredObjectSpaceFactory` + `UserManager`, mirroring the production app's own `RoleUpdater`/
  `SeedTestUsers` pattern), run once against the dev DB. The seeder itself was **not committed**
  (kept in `…\scratchpad\HeadlessPocSeeder\`, per the brief) — see "Recreate" below to rebuild it if
  the dev DB is ever reset.
- Credentials live in the gitignored `XafHeadless.Api.Tests/testsettings.Development.json` as
  `Test:RestrictedUser` / `Test:RestrictedPassword` (empty string), consumed by
  `TestBase.GetClientAsync("Restricted")`.

### What depends on it

- **Gate criterion 1** (design §Verification item 1): "metadata endpoint called as two roles (admin +
  a restricted production-app role) returns different member sets and `allow` flags" — the restricted
  user *is* that second role.
- **2 integration tests** authenticate as `Restricted` (`XafHeadless.Api.Tests`, verified by grep —
  these are the only two call sites of `GetClientAsync("Restricted")`):
  - `ListViewMetadataTests.Restricted_role_sees_fewer_or_equal_members_and_no_forbidden_ones`
  - `DetailViewMetadataTests.Restricted_role_layout_omits_item_node_for_denied_member`

  Both assert against `KnownModel.RestrictedDeniedMember` (= `Status`): the ListView columns and the
  DetailView layout tree must omit it for the restricted role while the admin role still sees it.

### Recreate (outline)

If the dev DB is reset and these rows are lost, recreate via any XAF host running against the same
separate dev-database connection string (the `XafHeadless.Api` host itself, or a scratch console app):

1. Via `INonSecuredObjectSpaceFactory`, open a non-secured object space.
2. Create a `PermissionPolicyRole` named `HeadlessPOC_Restricted`: `IsAdministrative = false`,
   `DenyAllByDefault`; add a type permission on the POC entity (`ReadOnlyAccess`, `Allow`); add a
   member permission on its `Status` member (`Read`, `Deny`).
3. Create a `PermissionPolicyUser` named `HeadlessPOC_Restricted` with an empty password (via
   `UserManager.CreateUserAsync`/`SetPasswordAsync`, matching the repo's `Admin` convention); assign
   it the `Default` role and the new `HeadlessPOC_Restricted` role.
4. `CommitChanges()`.
5. Put `Test:RestrictedUser=HeadlessPOC_Restricted` / `Test:RestrictedPassword=` (empty) into
   `testsettings.Development.json`.

(Task 3's seeder did exactly this, idempotently — deleting + recreating the two rows by name on
each run. Full source is not in the repo; this outline is enough to rebuild it.)

### Cleanup obligation (SEC-002, still open)

This is a **standing account with an empty password sitting in a shared dev database**, created
purely as POC test fixture. When the POC winds down (kill-gate accepted or the branch is dropped),
**delete both rows** (`PermissionPolicyRole` + `PermissionPolicyUser` named `HeadlessPOC_Restricted`
in that dev database) — or, if the POC continues into a follow-on phase, set a real password on the user
before the dev DB is shared more broadly. Do not leave an empty-password account behind as
incidental infrastructure. **This obligation exists regardless of the 26.1 seed** — the two fixtures
live in different databases entirely.
