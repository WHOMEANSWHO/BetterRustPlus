using Microsoft.VisualStudio.TestTools.UnitTesting;
using RustPlusDesk.Services.Auth;

namespace RustPlusDesktop.Tests;

[TestClass]
public sealed class WpfCompanionLoginBrowserTests
{
    private static readonly Uri Expected = new("http://localhost:3000/callback/nonce");

    [TestMethod]
    public void AcceptsOnlyExactCallbackOriginAndNonce()
    {
        Assert.IsTrue(WpfCompanionLoginBrowser.MatchesCallback(new Uri("http://localhost:3000/callback/nonce#code=fixture"), Expected));
        Assert.IsFalse(WpfCompanionLoginBrowser.MatchesCallback(new Uri("http://localhost:3000/callback/other#code=fixture"), Expected));
        Assert.IsFalse(WpfCompanionLoginBrowser.MatchesCallback(new Uri("http://localhost:3001/callback/nonce#code=fixture"), Expected));
        Assert.IsFalse(WpfCompanionLoginBrowser.MatchesCallback(new Uri("http://localhost.example.com:3000/callback/nonce#code=fixture"), Expected));
        Assert.IsFalse(WpfCompanionLoginBrowser.MatchesCallback(new Uri("http://user@localhost:3000/callback/nonce#code=fixture"), Expected));
    }
}
