using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace XafHeadless.E2E;

// EDIT-001: an app can DECLARE an editor with [EditorAlias], and until now the projector ignored that
// entirely -- it classified from the member's CLR type alone. Customer.Website is
// [EditorAlias(HyperLinkPropertyEditor)] over a string, so it rendered as an ordinary text box: the
// declaration was silently discarded, and the field looked exactly like any other string.
//
// The alias now reaches the client and HyperLinkEditor renders a followable link beside the value. The
// field stays editable, because the value IS a string and making it read-only would remove capability.
[TestClass]
public class EditorAliasE2ETests : PlaywrightFixture {
    // A Customer whose Website is actually populated -- an empty one proves nothing about the link, and
    // "the first row" is not a stable choice in a suite where sibling tests re-sort list views.
    static async Task<string> CustomerKeyWithAWebsiteAsync() {
        using var http = await ApiClientAsync();
        var resp = await http.GetAsync("api/odata/Customer?$top=20&$select=ID,Website");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        foreach (var row in doc.RootElement.GetProperty("value").EnumerateArray())
            if (row.TryGetProperty("Website", out var w) && w.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(w.GetString()))
                return row.GetProperty("ID").GetString()!;
        Assert.Fail("no Customer in the first 20 has a Website -- cannot prove anything about the editor");
        return "";
    }

    [TestMethod]
    public async Task CustomerDetail_DeclaredHyperLinkAlias_RendersAFollowableLink() {
        var key = await CustomerKeyWithAWebsiteAsync();

        await LoginAsync();
        await NavigateSpa($"/detail/Customer_DetailView/{key}");
        await Expect(Page).ToHaveURLAsync(
            new Regex(@"/detail/Customer_DetailView/[0-9a-fA-F-]{36}$"), new() { Timeout = 20000 });

        // The declared alias resolved to HyperLinkEditor, which offers the value as a link. Before this
        // change no anchor existed anywhere in the form for Website -- it was a bare text box.
        var link = Page.GetByRole(AriaRole.Link, new() { NameString = "Open" }).First;
        await Expect(link).ToBeVisibleAsync(new() { Timeout = 20000 });

        // Only absolute http(s) URLs become hrefs: a raw user-controlled string in an href is an injection
        // vector, so anything else stays plain text. Pin that here so the guard cannot silently loosen.
        var href = await link.GetAttributeAsync("href");
        Assert.IsNotNull(href);
        StringAssert.Matches(href, new Regex("^https?://", RegexOptions.IgnoreCase),
            "only absolute http(s) URLs may become an href");
        await Shot("edit001-01-hyperlink-alias");
    }
}
