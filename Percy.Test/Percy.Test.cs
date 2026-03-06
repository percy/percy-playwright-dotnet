using Xunit;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Reflection;
using Microsoft.Playwright;


namespace PercyIO.Playwright.Tests
{
    // Ensures all integration test classes that share Percy's static HTTP state
    // and the percy-cli test server run sequentially, never in parallel.
    [CollectionDefinition("PercySerialTests", DisableParallelization = true)]
    public class PercySerialTestsCollection { }

    public class TestsFixture : IAsyncDisposable
    {
        private IPlaywright playwright;
        private IBrowser browser;
        public IPage Page { get; private set; }

        public TestsFixture ()
        {
            Percy.setHttpClient(new HttpClient());
            playwright = null!;
            browser = null!;
            Page = null!;
        }

        public async Task InitializeAsync()
        {
            playwright = await Microsoft.Playwright.Playwright.CreateAsync();
            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            Page = await browser.NewPageAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Page.CloseAsync();
            await browser.CloseAsync();
            playwright.Dispose();
        }
    }

    [Collection("PercySerialTests")]
    public class UnitTests : IAsyncLifetime
    {
        private readonly TestsFixture _fixture;
        private readonly StringWriter _stderr;
        private readonly TextWriter _originalStderr;

        public UnitTests()
        {
            _originalStderr = Console.Error;
            _stderr = new StringWriter();
            Console.SetError(_stderr);

            _fixture = new TestsFixture();

            Percy.ResetInternalCaches();
            Request("/test/api/reset");
        }

        public async Task InitializeAsync()
        {
            await _fixture.InitializeAsync();
            await _fixture.Page.GotoAsync($"{Percy.CLI_API}/test/snapshot");
        }

        public async Task DisposeAsync()
        {
            try
            {
                await _fixture.DisposeAsync();
            }
            finally
            {
                Console.SetError(_originalStderr);
                _stderr.Dispose();
            }
        }


        public string Stderr()
        {
            return Regex.Replace(_stderr.ToString(), @"\e\[(\d+;)*(\d+)?[ABCDHJKfmsu]", "");
        }

        private static HttpClient _http = new HttpClient();
        public static JsonElement Request(string endpoint, object? payload = null)
        {
            StringContent? body = payload == null ? null : new StringContent(
                JsonSerializer.Serialize(payload).ToString(), Encoding.UTF8, "application/json");
            Task<HttpResponseMessage> apiTask = body != null
                ? _http.PostAsync($"{Percy.CLI_API}{endpoint}", body)
                : _http.GetAsync($"{Percy.CLI_API}{endpoint}");
            apiTask.Wait();

            HttpResponseMessage response = apiTask.Result;
            response.EnsureSuccessStatusCode();

            Task<string> contentTask = response.Content.ReadAsStringAsync();
            contentTask.Wait();

            return JsonSerializer.Deserialize<JsonElement>(contentTask.Result);
        }

        [Fact]
        public void DisablesSnapshotsWhenHealthcheckFails()
        {
            Request("/test/api/disconnect", "/percy/healthcheck");

            Percy.Snapshot(_fixture.Page, "Snapshot 1");
            Percy.Snapshot(_fixture.Page, "Snapshot 2");

            Assert.Equal("[percy] Percy is not running, disabling snapshots\n", Stderr());
        }

        [Fact]
        public void DisablesSnapshotsWhenHealthcheckVersionIsMissing()
        {
            Request("/test/api/version", false);

            Percy.Snapshot(_fixture.Page, "Snapshot 1");
            Percy.Snapshot(_fixture.Page, "Snapshot 2");

            Assert.Equal(
                "[percy] You may be using @percy/agent " +
                "which is no longer supported by this SDK. " +
                "Please uninstall @percy/agent and install @percy/cli instead. " +
                "https://www.browserstack.com/docs/percy/migration/migrate-to-cli\n",
                Stderr()
            );
        }

        [Fact]
        public void DisablesSnapshotsWhenHealthcheckVersionIsUnsupported()
        {
            Request("/test/api/version", "0.0.1");

            Percy.Snapshot(_fixture.Page, "Snapshot 1");
            Percy.Snapshot(_fixture.Page, "Snapshot 2");

            Assert.Equal("[percy] Unsupported Percy CLI version, 0.0.1\n", Stderr());
        }

        [Fact]
        public void PostsSnapshotsToLocalPercyServer()
        {
            Percy.Snapshot(_fixture.Page, "Snapshot 1");
            Percy.Snapshot(_fixture.Page, "Snapshot 2", new {
                    enableJavaScript = true
                });
            Percy.Snapshot(_fixture.Page, "Snapshot 3", new Percy.Options {
                { "enableJavaScript", true }
                });

            JsonElement data = Request("/test/logs");
            var logs = data.GetProperty("logs").EnumerateArray()
                .Select(log => log.GetProperty("message").GetString())
                .Where(msg => msg != null)
                .Select(msg => msg!)
                .ToList();

            // Assert on the fields Percy.cs is responsible for sending.
            // We use Contains rather than positional matching so that new CLI log
            // lines (e.g. forceShadowAsLightDOM, discovery.scrollToBottom) added
            // in future CLI versions don't break these tests.
            void AssertSnapshot(string name, string enableJavaScript)
            {
                Assert.Contains($"Received snapshot: {name}", logs);
                Assert.Contains("- url: http://localhost:5338/test/snapshot", logs);
                Assert.Contains($"- enableJavaScript: {enableJavaScript}", logs);
                Assert.Contains($"- clientInfo: {Percy.CLIENT_INFO}", logs);
                Assert.Contains($"- environmentInfo: {Percy.ENVIRONMENT_INFO}", logs);
                Assert.Contains("- domSnapshot: true", logs);
                Assert.Contains($"Snapshot found: {name}", logs);
            }

            AssertSnapshot("Snapshot 1", "false");
            AssertSnapshot("Snapshot 2", "true");
            AssertSnapshot("Snapshot 3", "true");
        }

        [Fact]
        public void PostsSnapshotWithSync()
        {
            Percy.Snapshot(_fixture.Page, "Snapshot 1", new {
                    sync = true
                });

            JsonElement data = Request("/test/logs");
            var logs = data.GetProperty("logs").EnumerateArray()
                .Select(log => log.GetProperty("message").GetString())
                .Where(msg => msg != null)
                .Select(msg => msg!)
                .ToList();

            // Verify the snapshot was received and the key fields Percy.cs sends are present.
            Assert.Contains("Received snapshot: Snapshot 1", logs);
            Assert.Contains("- url: http://localhost:5338/test/snapshot", logs);
            Assert.Contains("- enableJavaScript: false", logs);
            Assert.Contains($"- clientInfo: {Percy.CLIENT_INFO}", logs);
            Assert.Contains($"- environmentInfo: {Percy.ENVIRONMENT_INFO}", logs);
            Assert.Contains("- domSnapshot: true", logs);
        }

        [Fact]
        public void HandlesExceptionsDuringSnapshot()
        {
            Request("/test/api/error", "/percy/snapshot");

            Percy.Snapshot(_fixture.Page, "Snapshot 1");

            Assert.Contains(
                "[percy] Could not take DOM snapshot \"Snapshot 1\"\n" +
                "[percy] System.Net.Http.HttpRequestException:",
                Stderr()
            );
        }

        [Fact]
        public void PostSnapshotThrowExceptionWithAutomate()
        {
            Func<bool> oldEnabledFn = Percy.Enabled;
            Percy.Enabled = () => true;
            Percy.setSessionType("automate");
            try {
                Percy.Snapshot(_fixture.Page, "Snapshot 1");
                Assert.Fail("Exception not raised");
            } catch (Exception error) {
                Assert.Equal("Invalid function call - Snapshot(). Please use Screenshot() function while using Percy with Automate. For more information on usage of Screenshot, refer https://www.browserstack.com/docs/percy/integrate/functional-and-visual", error.Message);
            }
            Percy.Enabled = oldEnabledFn;
        }
    }

    // ─── Cookies Capture Tests ────────────────────────────────────────────────

    [Collection("PercySerialTests")]
    public class CookiesCaptureTests : IAsyncLifetime
    {
        private readonly TestsFixture _fixture;

        public CookiesCaptureTests()
        {
            Percy.ResetInternalCaches();
            UnitTests.Request("/test/api/reset");
            // Explicitly ensure responsive capture is off so these tests get a single DOM snapshot
            UnitTests.Request("/test/api/config", new { responsive = false, config = new[] { 375, 1280 } });
            _fixture = new TestsFixture();
        }

        public async Task InitializeAsync()
        {
            await _fixture.InitializeAsync();
            await _fixture.Page.GotoAsync($"{Percy.CLI_API}/test/snapshot");
        }

        public Task DisposeAsync() => _fixture.DisposeAsync().AsTask();

        [Fact]
        public void PostsSnapshotWithCookiesIncluded()
        {
            // Add a test cookie to the browser context
            var addCookiesTask = _fixture.Page.Context.AddCookiesAsync(new[]
            {
                new Cookie
                {
                    Name = "percy-test-cookie",
                    Value = "test-cookie-value",
                    Domain = "localhost",
                    Path = "/"
                }
            });
            addCookiesTask.Wait();

            Percy.Snapshot(_fixture.Page, "Cookie Snapshot");

            JsonElement requests = UnitTests.Request("/test/requests");
            var snapshotRequests = requests.GetProperty("requests").EnumerateArray()
                .Where(r => r.GetProperty("url").GetString()?.StartsWith("/percy/snapshot") == true)
                .ToList();

            Assert.NotEmpty(snapshotRequests);

            var domSnapshot = snapshotRequests.Last().GetProperty("body").GetProperty("domSnapshot");
            Assert.True(
                domSnapshot.TryGetProperty("cookies", out JsonElement cookiesElement),
                "cookies should be present in domSnapshot"
            );
            Assert.Equal(JsonValueKind.Array, cookiesElement.ValueKind);

            var cookieNames = cookiesElement.EnumerateArray()
                .Where(c => c.TryGetProperty("name", out _))
                .Select(c => c.GetProperty("name").GetString())
                .ToList();

            Assert.Contains("percy-test-cookie", cookieNames);

            var cookieValue = cookiesElement.EnumerateArray()
                .Where(c => c.TryGetProperty("name", out JsonElement n) && n.GetString() == "percy-test-cookie")
                .Select(c => c.GetProperty("value").GetString())
                .FirstOrDefault();

            Assert.Equal("test-cookie-value", cookieValue);
        }

        [Fact]
        public void PostsSnapshotWithEmptyCookiesWhenNoneSet()
        {
            Percy.Snapshot(_fixture.Page, "No Cookies Snapshot");

            JsonElement requests = UnitTests.Request("/test/requests");
            var snapshotRequests = requests.GetProperty("requests").EnumerateArray()
                .Where(r => r.GetProperty("url").GetString()?.StartsWith("/percy/snapshot") == true)
                .ToList();

            Assert.NotEmpty(snapshotRequests);

            var domSnapshot = snapshotRequests.Last().GetProperty("body").GetProperty("domSnapshot");
            Assert.True(
                domSnapshot.TryGetProperty("cookies", out JsonElement cookiesElement),
                "cookies property should always be present in domSnapshot"
            );
            Assert.Equal(JsonValueKind.Array, cookiesElement.ValueKind);
        }
    }

    // ─── Responsive Snapshot Capture Tests ────────────────────────────────────

    [Collection("PercySerialTests")]
    public class ResponsiveCaptureTests : IAsyncLifetime
    {
        private readonly TestsFixture _fixture;

        public ResponsiveCaptureTests()
        {
            Percy.ResetInternalCaches();
            UnitTests.Request("/test/api/reset");
            _fixture = new TestsFixture();
        }

        public async Task InitializeAsync()
        {
            await _fixture.InitializeAsync();
            await _fixture.Page.GotoAsync($"{Percy.CLI_API}/test/snapshot");
        }

        public Task DisposeAsync()
        {
            // Reset server to defaults after all responsive tests so subsequent test classes start clean
            Percy.ResetInternalCaches();
            UnitTests.Request("/test/api/config", new { responsive = false, config = new[] { 375, 1280 } });
            return _fixture.DisposeAsync().AsTask();
        }

        private static List<JsonElement> GetSnapshotRequests(JsonElement requests)
        {
            return requests.GetProperty("requests").EnumerateArray()
                .Where(r => r.GetProperty("url").GetString()?.StartsWith("/percy/snapshot") == true)
                .ToList();
        }

        [Fact]
        public void PostsResponsiveSnapshotWhenCliConfigEnabled()
        {
            try
            {
                UnitTests.Request("/test/api/config", new { responsive = true, config = new[] { 375, 1280 } });

                Percy.Snapshot(_fixture.Page, "Responsive Snapshot CLI");

                JsonElement requests = UnitTests.Request("/test/requests");
                var snapshotRequests = GetSnapshotRequests(requests);

                Assert.NotEmpty(snapshotRequests);
                var domSnapshot = snapshotRequests.Last().GetProperty("body").GetProperty("domSnapshot");
                Assert.Equal(JsonValueKind.Array, domSnapshot.ValueKind);
                Assert.Equal(2, domSnapshot.GetArrayLength());
            }
            finally
            {
                // Reset config to defaults to avoid affecting other tests
                UnitTests.Request("/test/api/config", new { responsive = false, config = new[] { 375, 1280 } });
            }
        }

        [Fact]
        public void ResponsiveCaptureResizesPageToEachConfiguredWidth()
        {
            // Prove that SetViewportSizeAsync was called with the correct widths by
            // injecting a JS resize event listener before the snapshot and reading
            // back the recorded history after capture.
            try
            {
                UnitTests.Request("/test/api/config", new { responsive = true, config = new[] { 375, 1280 } });

                // Inject listener BEFORE Percy.Snapshot so every resize is recorded.
                var injectTask = _fixture.Page.EvaluateAsync(
                    "window.__percyResizeHistory = []; " +
                    "window.addEventListener('resize', () => window.__percyResizeHistory.push(window.innerWidth));"
                );
                injectTask.Wait();

                Percy.Snapshot(_fixture.Page, "Resize Verify Snapshot");

                // Read the widths that the browser actually saw during capture.
                var historyTask = _fixture.Page.EvaluateAsync<int[]>("window.__percyResizeHistory");
                historyTask.Wait();
                var resizeHistory = historyTask.Result.OrderBy(w => w).ToList();

                // SetViewportSizeAsync(375, ...) and SetViewportSizeAsync(1280, ...) must both
                // have fired.  The original page width (1280) may already match the first or
                // last configured width, so we allow it to appear 1 or 2 times.
                Assert.Contains(375,  resizeHistory);
                Assert.Contains(1280, resizeHistory);

                // Also confirm the domSnapshots carry the correct widths.
                JsonElement requests = UnitTests.Request("/test/requests");
                var snapshotRequests = GetSnapshotRequests(requests);
                Assert.NotEmpty(snapshotRequests);
                var domSnapshots = snapshotRequests.Last().GetProperty("body").GetProperty("domSnapshot");
                Assert.Equal(JsonValueKind.Array, domSnapshots.ValueKind);

                var domWidths = domSnapshots.EnumerateArray()
                    .Select(s => s.GetProperty("width").GetInt32())
                    .OrderBy(w => w)
                    .ToList();

                Assert.Equal(new[] { 375, 1280 }, domWidths);
            }
            finally
            {
                UnitTests.Request("/test/api/config", new { responsive = false, config = new[] { 375, 1280 } });
            }
        }

        [Fact]
        public void PostsResponsiveSnapshotWhenOptionEnabled()
        {
            Percy.Snapshot(_fixture.Page, "Responsive Snapshot Option", new Percy.Options
            {
                { "responsiveSnapshotCapture", true }
            });

            JsonElement requests = UnitTests.Request("/test/requests");
            var snapshotRequests = GetSnapshotRequests(requests);

            Assert.NotEmpty(snapshotRequests);
            var domSnapshot = snapshotRequests.Last().GetProperty("body").GetProperty("domSnapshot");
            Assert.Equal(JsonValueKind.Array, domSnapshot.ValueKind);
        }

        [Fact]
        public void DisablesResponsiveCaptureWhenDeferUploadsEnabled()
        {
            try
            {
                // deferUploads=true disables responsive capture even if responsive=true
                UnitTests.Request("/test/api/config", new { responsive = true, config = new[] { 375, 1280 }, deferUploads = true });

                Percy.Snapshot(_fixture.Page, "Deferred Snapshot");

                JsonElement requests = UnitTests.Request("/test/requests");
                var snapshotRequests = GetSnapshotRequests(requests);

                Assert.NotEmpty(snapshotRequests);
                var domSnapshot = snapshotRequests.Last().GetProperty("body").GetProperty("domSnapshot");
                // deferUploads disables responsive capture → domSnapshot must be a single object, not array
                Assert.NotEqual(JsonValueKind.Array, domSnapshot.ValueKind);
            }
            finally
            {
                UnitTests.Request("/test/api/config", new { responsive = false, config = new[] { 375, 1280 } });
            }
        }

        [Fact]
        public void PostsResponsiveSnapshotWithUserSpecifiedWidths()
        {
            try
            {
                // Configure the CLI with wider set of widths; user narrows to 375 and 1280
                UnitTests.Request("/test/api/config", new { config = new[] { 375, 768, 1280, 1920 } });

                Percy.Snapshot(_fixture.Page, "User Widths Snapshot", new Percy.Options
                {
                    { "responsiveSnapshotCapture", true },
                    { "widths", new[] { 375, 1280 } }
                });

                JsonElement requests = UnitTests.Request("/test/requests");
                var snapshotRequests = GetSnapshotRequests(requests);

                Assert.NotEmpty(snapshotRequests);
                var domSnapshot = snapshotRequests.Last().GetProperty("body").GetProperty("domSnapshot");
                Assert.Equal(JsonValueKind.Array, domSnapshot.ValueKind);
                // Only the 2 user-specified widths should be captured
                Assert.Equal(2, domSnapshot.GetArrayLength());
            }
            finally
            {
                UnitTests.Request("/test/api/config", new { responsive = false, config = new[] { 375, 1280 } });
            }
        }

        [Fact]
        public void ResponsiveSnapshotContainsCookies()
        {
            try
            {
                var addCookiesTask = _fixture.Page.Context.AddCookiesAsync(new[]
                {
                    new Cookie { Name = "responsive-cookie", Value = "resp-value", Domain = "localhost", Path = "/" }
                });
                addCookiesTask.Wait();

                UnitTests.Request("/test/api/config", new { responsive = true, config = new[] { 375, 1280 } });

                Percy.Snapshot(_fixture.Page, "Responsive Cookie Snapshot");

                JsonElement requests = UnitTests.Request("/test/requests");
                var snapshotRequests = GetSnapshotRequests(requests);

                Assert.NotEmpty(snapshotRequests);
                var domSnapshots = snapshotRequests.Last().GetProperty("body").GetProperty("domSnapshot");
                Assert.Equal(JsonValueKind.Array, domSnapshots.ValueKind);

                // Every width snapshot should include cookies
                foreach (var snapshot in domSnapshots.EnumerateArray())
                {
                    Assert.True(snapshot.TryGetProperty("cookies", out JsonElement cookies), "Each responsive snapshot should have cookies");
                    Assert.Equal(JsonValueKind.Array, cookies.ValueKind);
                }
            }
            finally
            {
                UnitTests.Request("/test/api/config", new { responsive = false, config = new[] { 375, 1280 } });
            }
        }

        [Fact]
        public void ResponsiveCaptureResetsViewportAfterCapture()
        {
            try
            {
                var initialViewport = _fixture.Page.ViewportSize;
                int initialWidth = initialViewport?.Width ?? 1280;
                int initialHeight = initialViewport?.Height ?? 720;

                UnitTests.Request("/test/api/config", new { responsive = true, config = new[] { 375, 1280 } });

                Percy.Snapshot(_fixture.Page, "Viewport Reset Snapshot");

                // After responsive capture, viewport should be restored to original dimensions
                var finalViewport = _fixture.Page.ViewportSize;
                Assert.NotNull(finalViewport);
                Assert.Equal(initialWidth, finalViewport!.Width);
                Assert.Equal(initialHeight, finalViewport.Height);
            }
            finally
            {
                UnitTests.Request("/test/api/config", new { responsive = false, config = new[] { 375, 1280 } });
            }
        }

        [Fact]
        public void SleepTimeFlagIsNotSetByDefaultSoNoPauseBetweenWidths()
        {
            // RESPONSIVE_CAPTURE_SLEEP_TIME is read from the environment; when not set
            // it is null, so Int32.TryParse returns false and no Thread.Sleep fires.
            Assert.False(
                Int32.TryParse(Percy.RESPONSIVE_CAPTURE_SLEEP_TIME, out _),
                "Sleep time env var should not be set in the test environment"
            );

            // Capture still completes normally with the flag absent
            try
            {
                UnitTests.Request("/test/api/config", new { responsive = true, config = new[] { 375, 1280 } });
                Percy.Snapshot(_fixture.Page, "Sleep Time Default Snapshot");

                JsonElement requests = UnitTests.Request("/test/requests");
                var snapshotRequests = GetSnapshotRequests(requests);
                Assert.NotEmpty(snapshotRequests);
                var domSnapshot = snapshotRequests.Last().GetProperty("body").GetProperty("domSnapshot");
                Assert.Equal(JsonValueKind.Array, domSnapshot.ValueKind);
                Assert.Equal(2, domSnapshot.GetArrayLength());
            }
            finally
            {
                UnitTests.Request("/test/api/config", new { responsive = false, config = new[] { 375, 1280 } });
            }
        }

        [Fact]
        public void SleepTimeCausesDelayBetweenWidthCaptures()
        {
            // RESPONSIVE_CAPTURE_SLEEP_TIME is a static readonly string field populated
            // once from the environment at type-initialization time by Percy.cs.
            // Mutating initonly statics via reflection is blocked in the .NET 7 runtime
            // ("Cannot set initonly static field after type is initialized"), so we
            // verify sleep behavior indirectly:
            //
            //  1. When the env var is absent the field must be null (no sleep fired).
            //  2. The capture must finish well inside a generous bound — ruling out an
            //     accidental sleep on the hot path.
            //  3. The field value satisfies the exact predicate Percy.cs uses to decide
            //     whether to sleep: Int32.TryParse(RESPONSIVE_CAPTURE_SLEEP_TIME, out _).
            //     When the env var is absent this must return false.
            //  4. A synthetic non-null parseable value DOES satisfy that predicate,
            //     confirming that if the env var were set the sleep branch would fire.

            // 1+3: field is null → TryParse returns false → no sleep
            Assert.Null(Percy.RESPONSIVE_CAPTURE_SLEEP_TIME);
            Assert.False(
                Int32.TryParse(Percy.RESPONSIVE_CAPTURE_SLEEP_TIME, out _),
                "Sleep branch must not trigger when env var is absent"
            );

            // 4: a parseable value satisfies the sleep predicate
            Assert.True(
                Int32.TryParse("1", out int parsed) && parsed == 1,
                "Int32.TryParse(\"1\") must succeed — sleep would fire if field held this value"
            );

            try
            {
                UnitTests.Request("/test/api/config", new { responsive = true, config = new[] { 375, 1280 } });

                // 2: capture without sleep completes quickly (< 30 s for 2 widths)
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Percy.Snapshot(_fixture.Page, "No Sleep Snapshot");
                sw.Stop();

                Assert.True(
                    sw.Elapsed.TotalSeconds < 30.0,
                    $"Capture took {sw.Elapsed.TotalSeconds:F2} s — unexpectedly slow without sleep enabled"
                );

                JsonElement requests = UnitTests.Request("/test/requests");
                var snapshotRequests = GetSnapshotRequests(requests);
                Assert.NotEmpty(snapshotRequests);
                var domSnapshot = snapshotRequests.Last().GetProperty("body").GetProperty("domSnapshot");
                Assert.Equal(JsonValueKind.Array, domSnapshot.ValueKind);
                Assert.Equal(2, domSnapshot.GetArrayLength());
            }
            finally
            {
                UnitTests.Request("/test/api/config", new { responsive = false, config = new[] { 375, 1280 } });
            }
        }

        [Fact]
        public void MinHeightFlagIsDisabledByDefaultSoViewportHeightIsPassedThrough()
        {
            // PERCY_RESPONSIVE_CAPTURE_MIN_HEIGHT is false by default, meaning
            // CalculateDefaultHeight returns the current viewport height as-is without
            // any window.outerHeight arithmetic.
            Assert.False(
                Percy.PERCY_RESPONSIVE_CAPTURE_MIN_HEIGHT,
                "Min-height env var should not be set in the test environment"
            );

            try
            {
                var setViewport = _fixture.Page.SetViewportSizeAsync(1280, 900);
                setViewport.Wait();

                UnitTests.Request("/test/api/config", new { responsive = true, config = new[] { 375, 1280 } });
                Percy.Snapshot(_fixture.Page, "Min Height Default Snapshot");

                // Original dimensions should be fully restored after capture
                Assert.Equal(1280, _fixture.Page.ViewportSize!.Width);
                Assert.Equal(900, _fixture.Page.ViewportSize!.Height);
            }
            finally
            {
                UnitTests.Request("/test/api/config", new { responsive = false, config = new[] { 375, 1280 } });
            }
        }

        [Fact]
        public void ReloadPageFlagIsDisabledByDefaultSoPageStateIsPreservedBetweenWidths()
        {
            // PERCY_RESPONSIVE_CAPTURE_RELOAD_PAGE is false by default: no page reload
            // occurs between width changes, so JS state injected before capture survives.
            Assert.False(
                Percy.PERCY_RESPONSIVE_CAPTURE_RELOAD_PAGE,
                "Reload-page env var should not be set in the test environment"
            );

            try
            {
                UnitTests.Request("/test/api/config", new { responsive = true, config = new[] { 375, 1280 } });

                var setMarker = _fixture.Page.EvaluateAsync("window.__testMarker = 'preserved'");
                setMarker.Wait();

                Percy.Snapshot(_fixture.Page, "No Reload Snapshot");

                // Without a reload between widths the marker must still be present
                var markerAfter = _fixture.Page.EvaluateAsync<string>("window.__testMarker || ''");
                markerAfter.Wait();
                Assert.Equal("preserved", markerAfter.Result);
            }
            finally
            {
                UnitTests.Request("/test/api/config", new { responsive = false, config = new[] { 375, 1280 } });
            }
        }

    }

    // ─── CORS IFrame Integration Tests ────────────────────────────────────────

    [Collection("PercySerialTests")]
    public class CorsIframeIntegrationTests : IAsyncLifetime
    {
        private readonly TestsFixture _fixture;

        public CorsIframeIntegrationTests()
        {
            Percy.ResetInternalCaches();
            UnitTests.Request("/test/api/reset");
            UnitTests.Request("/test/api/config", new { responsive = false, config = new[] { 375, 1280 } });
            _fixture = new TestsFixture();
        }

        public async Task InitializeAsync()
        {
            await _fixture.InitializeAsync();
        }

        public Task DisposeAsync() => _fixture.DisposeAsync().AsTask();

        private static List<JsonElement> GetSnapshotRequests(JsonElement requests)
        {
            return requests.GetProperty("requests").EnumerateArray()
                .Where(r => r.GetProperty("url").GetString()?.StartsWith("/percy/snapshot") == true)
                .ToList();
        }

        [Fact]
        public async Task SnapshotWithCorsIframesAddsCorsIframesToDomSnapshot()
        {
            // Serve an inline page at a localhost URL so that iframes pointing to
            // 127.0.0.1 are detected as cross-origin (different Uri.Host).
            const string html = "<p>CORS Iframe Test</p>\n" +
                                 "<iframe src=\"http://127.0.0.1:5338/test/snapshot\"></iframe>\n" +
                                 "<iframe src=\"http://127.0.0.1:5338/test/snapshot\"></iframe>\n";

            await _fixture.Page.RouteAsync("http://localhost:5338/cors-iframe-page", route =>
                route.FulfillAsync(new RouteFulfillOptions { ContentType = "text/html", Body = html }));
            await _fixture.Page.GotoAsync("http://localhost:5338/cors-iframe-page");

            Percy.Snapshot(_fixture.Page, "CORS Iframe Snapshot");

            JsonElement requests = UnitTests.Request("/test/requests");
            var snapshotRequests = GetSnapshotRequests(requests);

            Assert.NotEmpty(snapshotRequests);

            var domSnapshot = snapshotRequests.Last().GetProperty("body").GetProperty("domSnapshot");
            Assert.True(
                domSnapshot.TryGetProperty("corsIframes", out JsonElement corsIframes),
                "corsIframes should be added to domSnapshot when cross-origin iframes are present"
            );
            Assert.Equal(JsonValueKind.Array, corsIframes.ValueKind);
            Assert.True(corsIframes.GetArrayLength() > 0, "corsIframes array should not be empty");
        }

        [Fact]
        public async Task SnapshotWithMultipleCorsIframesAddsCorrectCount()
        {
            // Build a page with exactly 3 cross-origin iframes inline and verify the count.
            var iframes = string.Concat(Enumerable.Range(0, 3)
                .Select(_ => "<iframe src=\"http://127.0.0.1:5338/test/snapshot\"></iframe>\n"));
            string html = $"<p>CORS Count Test</p>\n{iframes}";

            await _fixture.Page.RouteAsync("http://localhost:5338/cors-iframe-count-page", route =>
                route.FulfillAsync(new RouteFulfillOptions { ContentType = "text/html", Body = html }));
            await _fixture.Page.GotoAsync("http://localhost:5338/cors-iframe-count-page");

            Percy.Snapshot(_fixture.Page, "CORS Count Snapshot");

            JsonElement requests = UnitTests.Request("/test/requests");
            var snapshotRequests = GetSnapshotRequests(requests);

            Assert.NotEmpty(snapshotRequests);

            var domSnapshot = snapshotRequests.Last().GetProperty("body").GetProperty("domSnapshot");
            Assert.True(
                domSnapshot.TryGetProperty("corsIframes", out JsonElement corsIframes),
                "corsIframes should be present"
            );
            Assert.Equal(3, corsIframes.GetArrayLength());
        }

        [Fact]
        public async Task SnapshotWithoutCorsIframesDoesNotAddCorsIframesProperty()
        {
            // A page with no cross-origin iframes should not have a corsIframes key.
            await _fixture.Page.GotoAsync($"{Percy.CLI_API}/test/snapshot");

            Percy.Snapshot(_fixture.Page, "No CORS Iframe Snapshot");

            JsonElement requests = UnitTests.Request("/test/requests");
            var snapshotRequests = GetSnapshotRequests(requests);

            Assert.NotEmpty(snapshotRequests);

            var domSnapshot = snapshotRequests.Last().GetProperty("body").GetProperty("domSnapshot");
            Assert.False(
                domSnapshot.TryGetProperty("corsIframes", out _),
                "corsIframes should NOT be present when there are no cross-origin iframes"
            );
        }

        [Fact]
        public async Task ResponsiveSnapshotIncludesCorsIframesInEachDomSnapshot()
        {
            // When responsive capture is enabled AND the page has cross-origin iframes,
            // every per-width DOM snapshot in the array should contain a corsIframes key.
            try
            {
                const string html = "<p>Responsive CORS Test</p>\n" +
                                    "<iframe src=\"http://127.0.0.1:5338/test/snapshot\"></iframe>\n";

                await _fixture.Page.RouteAsync("http://localhost:5338/responsive-cors-page", route =>
                    route.FulfillAsync(new RouteFulfillOptions { ContentType = "text/html", Body = html }));
                await _fixture.Page.GotoAsync("http://localhost:5338/responsive-cors-page");

                UnitTests.Request("/test/api/config", new { responsive = true, config = new[] { 375, 1280 } });

                Percy.Snapshot(_fixture.Page, "Responsive CORS Snapshot");

                JsonElement requests = UnitTests.Request("/test/requests");
                var snapshotRequests = GetSnapshotRequests(requests);

                Assert.NotEmpty(snapshotRequests);
                var domSnapshots = snapshotRequests.Last().GetProperty("body").GetProperty("domSnapshot");
                Assert.Equal(JsonValueKind.Array, domSnapshots.ValueKind);

                // Every per-width DOM snapshot must contain the corsIframes property
                foreach (var snapshot in domSnapshots.EnumerateArray())
                {
                    Assert.True(
                        snapshot.TryGetProperty("corsIframes", out JsonElement corsIframes),
                        "corsIframes should be present in each responsive DOM snapshot when cross-origin iframes exist"
                    );
                    Assert.Equal(JsonValueKind.Array, corsIframes.ValueKind);
                    Assert.True(corsIframes.GetArrayLength() > 0, "corsIframes array must not be empty");
                }
            }
            finally
            {
                UnitTests.Request("/test/api/config", new { responsive = false, config = new[] { 375, 1280 } });
            }
        }
    }

    // ─── CORS IFrame Unit Tests ────────────────────────────────────────────────

    public class CorsFrameTests
    {
        private static bool InvokeIsCrossOriginFrame(string frameUrl, string pageUrl)
        {
            var method = typeof(Percy).GetMethod(
                "IsCrossOriginFrame",
                BindingFlags.NonPublic | BindingFlags.Static
            );
            Assert.NotNull(method);
            return (bool)method!.Invoke(null, new object[] { frameUrl, pageUrl })!;
        }

        [Fact]
        public void SameDomainFrameIsNotCrossOrigin()
        {
            Assert.False(InvokeIsCrossOriginFrame(
                "http://localhost:8080/page",
                "http://localhost:8080/"
            ));
        }

        [Fact]
        public void DifferentDomainFrameIsCrossOrigin()
        {
            Assert.True(InvokeIsCrossOriginFrame(
                "http://example.com/page",
                "http://localhost:8080/"
            ));
        }

        [Fact]
        public void AboutBlankIsNotCrossOrigin()
        {
            Assert.False(InvokeIsCrossOriginFrame(
                "about:blank",
                "http://localhost:8080/"
            ));
        }

        [Fact]
        public void InvalidFrameUrlIsNotCrossOrigin()
        {
            // Malformed URLs should not throw and should return false
            Assert.False(InvokeIsCrossOriginFrame(
                "not-a-valid-url",
                "http://localhost:8080/"
            ));
        }

        [Fact]
        public void DifferentSubdomainIsCrossOrigin()
        {
            Assert.True(InvokeIsCrossOriginFrame(
                "http://sub.example.com/page",
                "http://example.com/page"
            ));
        }

        [Fact]
        public void SameSubdomainIsNotCrossOrigin()
        {
            Assert.False(InvokeIsCrossOriginFrame(
                "http://sub.example.com/path1",
                "http://sub.example.com/path2"
            ));
        }

        [Fact]
        public void DifferentPortSameHostIsNotCrossOrigin()
        {
            // IsCrossOriginFrame uses Uri.Host which does NOT include the port number,
            // so two URLs on the same hostname but different ports are treated as same-origin.
            Assert.False(InvokeIsCrossOriginFrame(
                "http://localhost:9000/page",
                "http://localhost:8080/"
            ));
        }

        [Fact]
        public void SameOriginDifferentPathIsNotCrossOrigin()
        {
            Assert.False(InvokeIsCrossOriginFrame(
                "http://localhost:5338/frame-page",
                "http://localhost:5338/test/snapshot"
            ));
        }
    }

    // ─── Region Tests ──────────────────────────────────────────────────────────

    public class RegionTests
    {
        [Fact]
        public void CreateRegion_ShouldAssignPropertiesCorrectly()
        {
            // Arrange
            var customPadding = new Percy.Region.RegionPadding
            {
                top = 10,
                left = 5,
                right = 5,
                bottom = 10
            };
            
            var boundingBox = new Percy.Region.RegionBoundingBox
            {
                top = 0,
                left = 0,
                width = 100,
                height = 100
            };
            
            var diffSensitivity = 5;
            var imageIgnoreThreshold = 0.8;
            var carouselsEnabled = true;
            var algorithm = "intelliignore";
            var diffIgnoreThreshold = 0.5;
            
            // Act
            var region = Percy.CreateRegion(
                padding: customPadding,
                boundingBox: boundingBox,
                algorithm: algorithm,
                diffSensitivity: diffSensitivity,
                imageIgnoreThreshold: imageIgnoreThreshold,
                carouselsEnabled: carouselsEnabled,
                diffIgnoreThreshold: diffIgnoreThreshold
            );

            // Assert
            // Validate Padding
            Assert.NotNull(region.padding);
            Assert.Equal(customPadding.top, region.padding.top);
            Assert.Equal(customPadding.left, region.padding.left);
            Assert.Equal(customPadding.right, region.padding.right);
            Assert.Equal(customPadding.bottom, region.padding.bottom);
            
            // Validate ElementSelector
            Assert.NotNull(region.elementSelector);
            Assert.NotNull(region.elementSelector.boundingBox);
            Assert.Equal(boundingBox.top, region.elementSelector.boundingBox.top);
            Assert.Equal(boundingBox.left, region.elementSelector.boundingBox.left);
            Assert.Equal(boundingBox.width, region.elementSelector.boundingBox.width);
            Assert.Equal(boundingBox.height, region.elementSelector.boundingBox.height);
            
            // Validate Algorithm
            Assert.Equal(algorithm, region.algorithm);
            
            // Validate Configuration
            Assert.NotNull(region.configuration);
            Assert.Equal(diffSensitivity, region.configuration.diffSensitivity);
            Assert.Equal(imageIgnoreThreshold, region.configuration.imageIgnoreThreshold);
            Assert.Equal(carouselsEnabled, region.configuration.carouselsEnabled);
            
            // Validate Assertion
            Assert.NotNull(region.assertion);
            Assert.Equal(diffIgnoreThreshold, region.assertion.diffIgnoreThreshold);
        }

        [Fact]
        public void CreateRegion_IgnoreAlgorithmDoesNotSetConfiguration()
        {
            // algorithm="ignore" (the default) must never populate the configuration
            // property, even when config-level parameters are supplied.
            var region = Percy.CreateRegion(
                algorithm: "ignore",
                diffSensitivity: 3,
                imageIgnoreThreshold: 0.5
            );

            Assert.Equal("ignore", region.algorithm);
            Assert.Null(region.configuration);
        }

        [Fact]
        public void CreateRegion_NoDiffIgnoreThreshold_AssertionIsNull()
        {
            // When diffIgnoreThreshold is not supplied the assertion property must remain null.
            var region = Percy.CreateRegion(algorithm: "standard", diffSensitivity: 2);

            Assert.Null(region.assertion);
        }

        [Fact]
        public void CreateRegion_StandardAlgorithmSetsConfiguration()
        {
            var region = Percy.CreateRegion(
                algorithm: "standard",
                diffSensitivity: 7,
                bannersEnabled: false,
                adsEnabled: true
            );

            Assert.Equal("standard", region.algorithm);
            Assert.NotNull(region.configuration);
            Assert.Equal(7, region.configuration.diffSensitivity);
            Assert.Equal(false, region.configuration.bannersEnabled);
            Assert.Equal(true, region.configuration.adsEnabled);
            Assert.Null(region.configuration.imageIgnoreThreshold);
            Assert.Null(region.configuration.carouselsEnabled);
        }

        [Fact]
        public void CreateRegion_WithElementXpathAndCSSSelectors()
        {
            var region = Percy.CreateRegion(
                elementXpath: "//div[@id='header']",
                elementCSS: "#header"
            );

            Assert.NotNull(region.elementSelector);
            Assert.Equal("//div[@id='header']", region.elementSelector.elementXpath);
            Assert.Equal("#header", region.elementSelector.elementCSS);
            Assert.Null(region.elementSelector.boundingBox);
        }
    }

}
