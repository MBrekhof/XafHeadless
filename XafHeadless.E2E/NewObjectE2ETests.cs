using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace XafHeadless.E2E;

// CRUD-001: the client half of GAP-003. The create endpoint (POST api/save/{type}) shipped and was proven
// server-side on 2026-07-12, but nothing in the UI could reach it -- no New action, no blank form. These
// two tests cover the two outcomes the save contract defines, and BOTH were confirmed against the live
// host before being written, rather than assumed:
//   POST api/save/Order    {}  -> 201 {"key":"..."}          (no required members)
//   POST api/save/Employee {}  -> 422 with six MemberErrors   ("\"City\" must not be empty." etc.)
// So Order exercises the success-and-navigate path and Employee exercises field-level validation.
[TestClass]
public class NewObjectE2ETests : PlaywrightFixture {
    static readonly Regex NewButton = new("^new$", RegexOptions.IgnoreCase);
    static readonly Regex SaveButton = new("^save$", RegexOptions.IgnoreCase);

    // The Modernist theme uppercases button captions via text-transform, and that can reach the accessible
    // name, so every caption here matches case-insensitively (the lesson DateFilterE2ETests learned).

    [TestMethod]
    public async Task OrderListView_New_createsTheObject_andNavigatesToIt() {
        string? createdKey = null;
        try {
            await LoginAsync();
            await NavigateSpa("/list/Order_ListView");
            await Expect(Page).ToHaveURLAsync(new Regex(@"/list/Order_ListView$"), new() { Timeout = 15000 });

            // The button is gated on the server-projected Allow.New (model AND security), so its presence
            // for Admin is itself part of the contract.
            var newButton = Page.GetByRole(AriaRole.Button, new() { NameRegex = NewButton });
            await Expect(newButton).ToBeVisibleAsync(new() { Timeout = 15000 });

            await newButton.ClickAsync();
            await Expect(Page).ToHaveURLAsync(new Regex(@"/new/Order_DetailView$"), new() { Timeout = 15000 });

            // Save is enabled with no edits: an all-defaults Order is valid, and the SERVER decides that.
            var save = Page.GetByRole(AriaRole.Button, new() { NameRegex = SaveButton });
            await Expect(save).ToBeEnabledAsync(new() { Timeout = 15000 });
            await save.ClickAsync();

            // The 201 carries the key the server generated; the client had none to send. Landing on the
            // real object's route is the whole point -- otherwise the user is stranded on /new.
            await Expect(Page).ToHaveURLAsync(
                new Regex(@"/detail/Order_DetailView/[0-9a-fA-F-]{36}$"), new() { Timeout = 20000 });
            createdKey = Regex.Match(Page.Url, "[0-9a-fA-F-]{36}$").Value;
            Assert.IsFalse(string.IsNullOrEmpty(createdKey), "could not read the new key back off the URL");
            await Shot("crud001-01-order-created");
        }
        finally {
            // The demo database is disposable, but a test that leaves rows behind quietly grows the very
            // view other tests page through -- delete what this one made.
            if (createdKey is not null) {
                using var http = await ApiClientAsync();
                await http.DeleteAsync($"api/save/Order/{createdKey}");
            }
        }
    }

    [TestMethod]
    public async Task EmployeeNew_rejectedByValidation_showsFieldLevelErrors_andStaysOnTheForm() {
        await LoginAsync();
        await NavigateSpa("/new/Employee_DetailView");
        await Expect(Page).ToHaveURLAsync(new Regex(@"/new/Employee_DetailView$"), new() { Timeout = 15000 });

        var save = Page.GetByRole(AriaRole.Button, new() { NameRegex = SaveButton });
        await Expect(save).ToBeEnabledAsync(new() { Timeout = 15000 });
        await save.ClickAsync();

        // Employee carries real [RuleRequiredField] rules, so an empty create is a 422 -- the failure the
        // save contract exists for. The user must be told which members, and must NOT be navigated away
        // from the form they still have to fix.
        await Expect(Page.GetByText(new Regex("must not be empty", RegexOptions.IgnoreCase)).First)
            .ToBeVisibleAsync(new() { Timeout = 20000 });
        await Expect(Page).ToHaveURLAsync(new Regex(@"/new/Employee_DetailView$"), new() { Timeout = 5000 });
        await Shot("crud001-02-employee-validation");
    }
}
