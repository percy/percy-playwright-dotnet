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
