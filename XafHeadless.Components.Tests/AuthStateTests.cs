using XafHeadless.Components.Services;

namespace XafHeadless.Components.Tests;

// GAP-007: guards the pure, JS-free decision seam the persist/restore flow depends on. The JS-interop
// path itself (sessionStorage read/write) is proven by the dual-render-mode E2E, not here.
[TestClass]
public class AuthStateTests {
    [TestMethod]
    public void RestoreAttempted_is_false_until_MarkRestored() {
        var s = new AuthState();
        Assert.IsFalse(s.RestoreAttempted, "must start un-restored so the /login redirect stays deferred");
    }

    [TestMethod]
    public void MarkRestored_latches_and_raises_Changed() {
        var s = new AuthState();
        var raised = 0;
        s.Changed += () => raised++;
        s.MarkRestored();
        Assert.IsTrue(s.RestoreAttempted, "MarkRestored must latch RestoreAttempted");
        Assert.AreEqual(1, raised, "MarkRestored must raise Changed so MainLayout re-evaluates the redirect");
    }

    [TestMethod]
    public void SetToken_updates_token_and_raises_Changed() {
        var s = new AuthState();
        var raised = 0;
        s.Changed += () => raised++;
        s.SetToken("jwt");
        Assert.AreEqual("jwt", s.Token);
        s.SetToken(null);
        Assert.IsNull(s.Token);
        Assert.AreEqual(2, raised, "each SetToken must raise Changed (login persist + 401 clear)");
    }
}
