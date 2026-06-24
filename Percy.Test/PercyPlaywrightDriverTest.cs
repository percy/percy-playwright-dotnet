using Xunit;
using System;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace PercyIO.Playwright.Tests
{
    // Covers the concrete PercyPlaywrightDriver. The GUID reflection methods
    // (GetPageGUID/GetFrameGUID/GetBrowserGuid) read Playwright's internal
    // <Guid>k__BackingField and therefore need a real Chromium page — they are
    // exercised through the live browser fixture. GetSessionId additionally issues a
    // BrowserStack-Automate "getSessionDetails" executor over the live CDP bridge,
    // which only exists on a real Automate session; its caching/parsing logic is
    // exercised through a tiny test subclass that overrides the two protected-virtual
    // I/O seams (GetBrowserGuid + FetchSessionDetails) while leaving every other line
    // of GetSessionId real.

    // ─── GetSessionId via the protected-virtual seams (no live Automate session) ──
    public class PercyPlaywrightDriverSessionTests
    {
        // Test double: overrides only the two I/O primitives. The page is never
        // touched by these overrides, so a null page is safe here.
        private sealed class FakeDriver : PercyPlaywrightDriver
        {
            private readonly string _browserGuid;
            private readonly string _sessionJson;
            public int FetchCount { get; private set; }

            public FakeDriver(string browserGuid, string sessionJson) : base(null!)
            {
                _browserGuid = browserGuid;
                _sessionJson = sessionJson;
            }

            protected override string GetBrowserGuid() => _browserGuid;

            protected override string FetchSessionDetails()
            {
                FetchCount++;
                return _sessionJson;
            }
        }

        [Fact]
        public void GetSessionId_ParsesHashedId_AndReturnsIt()
        {
            var driver = new FakeDriver("browser-guid-A", "{\"hashed_id\":\"abc123\"}");
            Assert.Equal("abc123", driver.GetSessionId());
            // First call is a cache miss -> exactly one fetch.
            Assert.Equal(1, driver.FetchCount);
        }

        [Fact]
        public void GetSessionId_CachesPerBrowserGuid_SecondCallDoesNotRefetch()
        {
            // Cache is a process-wide static keyed by browser guid; use a unique guid
            // so this test is independent of any other run.
            string guid = "browser-guid-" + Guid.NewGuid();
            var driver = new FakeDriver(guid, "{\"hashed_id\":\"cached-id\"}");

            Assert.Equal("cached-id", driver.GetSessionId()); // miss -> fetch + store
            Assert.Equal("cached-id", driver.GetSessionId()); // hit  -> no fetch

            Assert.Equal(1, driver.FetchCount);
        }

        // ─── GUID reflection (GetPageGUID/GetFrameGUID/GetBrowserGuid) offline ─────
        //
        // In production these reflect Playwright's internal <Guid>k__BackingField off
        // the page / main-frame / browser impl types (live-only). The production code
        // reads that field off the object returned by the protected-virtual
        // Get*GuidSource seams, which default to this.page[.MainFrame] / Context.Browser.
        // Here we override only those seams to hand back a stand-in object whose
        // BaseType exposes the SAME backing field, so the real GetField / GetValue /
        // cast / return lines execute without a live Chromium page.

        // Base type carrying an auto-property whose compiler-generated backing field is
        // exactly "<Guid>k__BackingField" — the same name the production reflection reads.
        private class GuidBackingBase
        {
            public string Guid { get; set; } = "";
        }

        // Derived type: its BaseType is GuidBackingBase, mirroring how Playwright impl
        // classes derive from a base that holds the Guid backing field.
        private sealed class GuidBackingImpl : GuidBackingBase { }

        private sealed class GuidSeamDriver : PercyPlaywrightDriver
        {
            private readonly GuidBackingImpl _page;
            private readonly GuidBackingImpl _frame;
            private readonly GuidBackingImpl _browser;

            public GuidSeamDriver(string pageGuid, string frameGuid, string browserGuid) : base(null!)
            {
                _page = new GuidBackingImpl { Guid = pageGuid };
                _frame = new GuidBackingImpl { Guid = frameGuid };
                _browser = new GuidBackingImpl { Guid = browserGuid };
            }

            protected override object GetPageGuidSource() => _page;
            protected override object GetFrameGuidSource() => _frame;
            protected override object GetBrowserGuidSource() => _browser;

            // Expose the protected GetBrowserGuid for direct assertion.
            public string CallGetBrowserGuid() => GetBrowserGuid();
        }

        [Fact]
        public void GetPageGUID_ReadsBackingFieldFromSource()
        {
            var driver = new GuidSeamDriver("page-guid-x", "frame-guid-y", "browser-guid-z");
            Assert.Equal("page-guid-x", driver.GetPageGUID());
        }

        [Fact]
        public void GetFrameGUID_ReadsBackingFieldFromSource()
        {
            var driver = new GuidSeamDriver("page-guid-x", "frame-guid-y", "browser-guid-z");
            Assert.Equal("frame-guid-y", driver.GetFrameGUID());
        }

        [Fact]
        public void GetBrowserGuid_ReadsBackingFieldFromSource()
        {
            var driver = new GuidSeamDriver("page-guid-x", "frame-guid-y", "browser-guid-z");
            Assert.Equal("browser-guid-z", driver.CallGetBrowserGuid());
        }

        // Exposes the DEFAULT (non-overridden) Get*GuidSource seam bodies so their
        // production one-liners — which simply return this.page / this.page.MainFrame /
        // this.page.Context.Browser — are exercised offline against a mocked page.
        private sealed class BaseSourceDriver : PercyPlaywrightDriver
        {
            public BaseSourceDriver(Microsoft.Playwright.IPage page) : base(page) { }
            public object CallPageSource() => GetPageGuidSource();
            public object CallFrameSource() => GetFrameGuidSource();
            public object CallBrowserSource() => GetBrowserGuidSource();
        }

        [Fact]
        public void GuidSourceSeams_DefaultBodies_ReturnPageFrameAndBrowser()
        {
            var frame = new Moq.Mock<Microsoft.Playwright.IFrame>().Object;
            var browser = new Moq.Mock<Microsoft.Playwright.IBrowser>().Object;
            var ctx = new Moq.Mock<Microsoft.Playwright.IBrowserContext>();
            ctx.SetupGet(c => c.Browser).Returns(browser);
            var page = new Moq.Mock<Microsoft.Playwright.IPage>();
            page.SetupGet(p => p.MainFrame).Returns(frame);
            page.SetupGet(p => p.Context).Returns(ctx.Object);

            var driver = new BaseSourceDriver(page.Object);
            Assert.Same(page.Object, driver.CallPageSource());
            Assert.Same(frame, driver.CallFrameSource());
            Assert.Same(browser, driver.CallBrowserSource());
        }
    }

    // ─── GUID reflection methods against a real Chromium page ─────────────────────
    [Collection("PercySerialTests")]
    public class PercyPlaywrightDriverReflectionTests : IAsyncLifetime
    {
        private IPlaywright _playwright = null!;
        private IBrowser _browser = null!;
        private IPage _page = null!;

        public async Task InitializeAsync()
        {
            _playwright = await Microsoft.Playwright.Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            _page = await _browser.NewPageAsync();
            await _page.GotoAsync($"{Percy.CLI_API}/test/snapshot");
        }

        public async Task DisposeAsync()
        {
            await _browser.CloseAsync();
            _playwright.Dispose();
        }

        private PercyPlaywrightDriver NewDriver() => new PercyPlaywrightDriver(_page);

        [Fact]
        public void GetUrl_ReturnsLivePageUrl()
        {
            Assert.Equal(_page.Url, NewDriver().GetUrl());
        }

        [Fact]
        public void GetPageGUID_ReadsInternalGuidFromRealPage()
        {
            string guid = NewDriver().GetPageGUID();
            Assert.False(string.IsNullOrEmpty(guid));
        }

        [Fact]
        public void GetFrameGUID_ReadsInternalGuidFromRealMainFrame()
        {
            string guid = NewDriver().GetFrameGUID();
            Assert.False(string.IsNullOrEmpty(guid));
        }

        [Fact]
        public void GetBrowserGuid_ReadsInternalGuidFromRealBrowser()
        {
            // GetBrowserGuid is protected; invoke through the real (non-overridden)
            // implementation to cover the browser-reflection lines on a live browser.
            var method = typeof(PercyPlaywrightDriver).GetMethod(
                "GetBrowserGuid", BindingFlags.NonPublic | BindingFlags.Instance)!;
            string guid = (string)method.Invoke(NewDriver(), null)!;
            Assert.False(string.IsNullOrEmpty(guid));
        }
    }
}
