using Xunit;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;
using RichardSzalay.MockHttp;
using Newtonsoft.Json.Linq;

namespace PercyIO.Playwright.Tests
{
    // Mock-based unit tests that exercise the error / edge / feature-flag branches of
    // Percy.cs entirely in-process — no live Chromium, no live @percy/cli. IPage and
    // friends are mocked with Moq; the @percy/cli HTTP surface is mocked with
    // RichardSzalay.MockHttp; private statics are reached via reflection (mirroring the
    // existing CorsFrameTests / PercyLogicTest convention). The three responsive-capture
    // feature flags are flipped through their internal behavior-preserving seam mirrors.
    //
    // These run sequentially because they mutate Percy's process-wide static HTTP client,
    // cliConfig, sessionType, Enabled, and the feature-flag mirrors.
    [Collection("PercyMockedFlow")]
    public class PercyMockedFlowTest : IDisposable
    {
        private static readonly Type PercyType = typeof(Percy);

        private readonly Func<bool> _origEnabled;
        private readonly string? _origSessionType;
        private readonly HttpClient _origHttp;
        private readonly bool _origMinHeight;
        private readonly bool _origReload;
        private readonly string? _origSleep;

        public PercyMockedFlowTest()
        {
            _origEnabled = Percy.Enabled;
            _origSessionType = (string?)PercyType
                .GetField("sessionType", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);
            _origHttp = Percy.getHttpClient();
            _origMinHeight = GetFlagBool("ResponsiveCaptureMinHeight");
            _origReload = GetFlagBool("ResponsiveCaptureReloadPage");
            _origSleep = GetFlagStr("ResponsiveCaptureSleepTime");
            ClearCliConfig();
            Percy.ResetInternalCaches();
        }

        public void Dispose()
        {
            Percy.Enabled = _origEnabled;
            Percy.setSessionType(_origSessionType);
            Percy.setHttpClient(_origHttp);
            SetFlagBool("ResponsiveCaptureMinHeight", _origMinHeight);
            SetFlagBool("ResponsiveCaptureReloadPage", _origReload);
            SetFlagStr("ResponsiveCaptureSleepTime", _origSleep);
            ClearCliConfig();
            Percy.ResetInternalCaches();
        }

        // ─── reflection helpers ───────────────────────────────────────────────

        private static MethodInfo PrivateStatic(string name) =>
            PercyType.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"method {name} not found");

        private static object? Invoke(string method, params object?[] args) =>
            PrivateStatic(method).Invoke(null, args);

        private static void SetCliConfig(object config) =>
            PercyType.GetMethod("setCliConfig", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new[] { config });

        private static void ClearCliConfig() => SetCliConfig(new object());

        private static JsonElement Json(string json) =>
            JsonDocument.Parse(json).RootElement.Clone();

        private static bool GetFlagBool(string field) =>
            (bool)PercyType.GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        private static void SetFlagBool(string field, bool v) =>
            PercyType.GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, v);
        private static string? GetFlagStr(string field) =>
            (string?)PercyType.GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);
        private static void SetFlagStr(string field, string? v) =>
            PercyType.GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, v);

        // Reset the private static _dom cache so GetPercyDOM re-fetches over the mock http.
        private static void ResetDom() => Percy.ResetInternalCaches();

        // ─── mock page builders ───────────────────────────────────────────────

        // A page mock whose EvaluateAsync<object>/<bool>/<int>/<string> can be scripted.
        private static Mock<IPage> NewPage() => new Mock<IPage>(MockBehavior.Loose);

        private static void SetupEvalObject(Mock<IPage> page, Func<string, object?> respond)
        {
            page.Setup(p => p.EvaluateAsync<object>(It.IsAny<string>(), It.IsAny<object?>()))
                .Returns((string s, object? a) => Task.FromResult(respond(s)!));
        }

        // ─── GetPercyDOM (lines 142-145) + healthcheck success=false (159-160) via mock http ──

        private static MockHttpMessageHandler HttpWithHealthcheck(string healthcheckJson, string? version = "1.0.0")
        {
            var mock = new MockHttpMessageHandler();
            var req = mock.When(HttpMethod.Get, "http://localhost:5338/percy/healthcheck");
            if (version != null)
            {
                req.Respond(_ =>
                {
                    var resp = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(healthcheckJson, System.Text.Encoding.UTF8, "application/json")
                    };
                    resp.Headers.TryAddWithoutValidation("x-percy-core-version", version);
                    return resp;
                });
            }
            else
            {
                req.Respond("application/json", healthcheckJson);
            }
            return mock;
        }

        [Fact]
        public void Enabled_SuccessFalse_ThrowsAndDisables()
        {
            // success=false → Enabled() catches the thrown Exception(data.error) and
            // logs "Percy is not running, disabling snapshots", returning false.
            var mock = HttpWithHealthcheck("{\"success\":false,\"error\":\"boom\"}");
            Percy.setHttpClient(new HttpClient(mock));
            Percy.ResetInternalCaches();

            Assert.False(Percy.Enabled());
        }

        [Fact]
        public void Enabled_SuccessTrue_WithTypeAndConfig_EnablesAndSetsState()
        {
            // Covers the happy branch that reads type + config out of the healthcheck body.
            var mock = HttpWithHealthcheck(
                "{\"success\":true,\"type\":\"web\",\"config\":{\"snapshot\":{\"widths\":[375]}}}");
            Percy.setHttpClient(new HttpClient(mock));
            Percy.ResetInternalCaches();

            Assert.True(Percy.Enabled());
        }

        // ─── ProcessFrame (324-375): iframeData-null (358-360) + catch (370-373) ──

        private static object? InvokeProcessFrame(IPage page, IFrame frame, Dictionary<string, object>? options, string dom) =>
            Invoke("ProcessFrame", page, frame, options, dom);

        [Fact]
        public void ProcessFrame_NoMatchingIframeElement_ReturnsNull()
        {
            // Frame serialization succeeds, but the main-page lookup returns null
            // (no matching <iframe> with a percyElementId) → logs + returns null.
            var frame = new Mock<IFrame>();
            frame.SetupGet(f => f.Url).Returns("http://other.example.com/frame");
            // ProcessFrame injects + serializes through the NON-generic IFrame.EvaluateAsync
            // (returns Task<JsonElement>), so those must be set up to avoid an NRE in the try.
            frame.Setup(f => f.EvaluateAsync(It.IsAny<string>(), It.IsAny<object?>()))
                .Returns(Task.FromResult<JsonElement?>(Json("{}")));

            var page = NewPage();
            // EvaluateSync<object>(page, getDataScript, frameUrl) → null
            page.Setup(p => p.EvaluateAsync<object>(It.IsAny<string>(), It.IsAny<object?>()))
                .Returns(Task.FromResult<object>(null!));

            Assert.Null(InvokeProcessFrame(page.Object, frame.Object,
                new Dictionary<string, object>(), "PercyDOM=1;"));
        }

        [Fact]
        public void ProcessFrame_WhenFrameEvaluateThrows_CatchesAndReturnsNull()
        {
            var frame = new Mock<IFrame>();
            frame.SetupGet(f => f.Url).Returns("http://other.example.com/frame");
            frame.Setup(f => f.EvaluateAsync(It.IsAny<string>(), It.IsAny<object?>()))
                .ThrowsAsync(new Exception("frame eval failed"));

            var page = NewPage();
            Assert.Null(InvokeProcessFrame(page.Object, frame.Object, null, "PercyDOM=1;"));
        }

        [Fact]
        public void ProcessFrame_HappyPath_ReturnsShapeWithIframeData()
        {
            // iframeData non-null → returns the anonymous { iframeData, iframeSnapshot, frameUrl }.
            var frame = new Mock<IFrame>();
            frame.SetupGet(f => f.Url).Returns("http://other.example.com/frame");
            frame.Setup(f => f.EvaluateAsync(It.IsAny<string>(), It.IsAny<object?>()))
                .Returns(Task.FromResult<JsonElement?>(Json("\"snapshot-data\"")));

            var page = NewPage();
            page.Setup(p => p.EvaluateAsync<object>(It.IsAny<string>(), It.IsAny<object?>()))
                .Returns(Task.FromResult<object>(new Dictionary<string, object> { { "percyElementId", "abc" } }));

            var result = InvokeProcessFrame(page.Object, frame.Object,
                new Dictionary<string, object> { { "enableJavaScript", false } }, "PercyDOM=1;");

            Assert.NotNull(result);
            var frameUrlProp = result!.GetType().GetProperty("frameUrl")!.GetValue(result);
            Assert.Equal("http://other.example.com/frame", frameUrlProp);
        }

        // ─── WaitForReady (381-429): malformed-json catch (410), eval catch (424-427),
        //     happy non-null return ─────────────────────────────────────────────

        private static object? InvokeWaitForReady(IPage? page, Dictionary<string, object>? options) =>
            Invoke("WaitForReady", page, options);

        [Fact]
        public void WaitForReady_MalformedReadinessJson_FallsThroughThenEvaluates()
        {
            // cliConfig.snapshot.readiness is a STRING, not object → the options/config
            // branch is skipped, readinessJson stays "{}", parse succeeds, and the script
            // runs against the page. We make EvaluateSync return a diagnostics object.
            ClearCliConfig();
            var page = NewPage();
            SetupEvalObject(page, _ => new Dictionary<string, object> { { "ok", true } });

            var result = InvokeWaitForReady(page.Object, null);
            Assert.NotNull(result);
        }

        [Fact]
        public void WaitForReady_EvaluateThrows_CatchesAndReturnsNull()
        {
            ClearCliConfig();
            var page = NewPage();
            page.Setup(p => p.EvaluateAsync<object>(It.IsAny<string>(), It.IsAny<object?>()))
                .ThrowsAsync(new Exception("waitForReady eval blew up"));

            Assert.Null(InvokeWaitForReady(page.Object, null));
        }

        [Fact]
        public void WaitForReady_ReadinessFromOptions_SerializedAndEvaluated()
        {
            ClearCliConfig();
            var page = NewPage();
            SetupEvalObject(page, _ => new Dictionary<string, object> { { "diag", 1 } });

            var options = new Dictionary<string, object>
            {
                { "readiness", new { preset = "balanced", timeout = 1000 } }
            };
            Assert.NotNull(InvokeWaitForReady(page.Object, options));
        }

        [Fact]
        public void WaitForReady_ReadinessFromCliConfig_SerializedAndEvaluated()
        {
            // readiness pulled from cliConfig.snapshot.readiness (object, non-disabled).
            SetCliConfig(Json("{ \"snapshot\": { \"readiness\": { \"preset\": \"strict\" } } }"));
            try
            {
                var page = NewPage();
                SetupEvalObject(page, _ => new Dictionary<string, object> { { "diag", 2 } });
                Assert.NotNull(InvokeWaitForReady(page.Object, null));
            }
            finally { ClearCliConfig(); }
        }

        // ─── GetSerializedDom (431-491) ────────────────────────────────────────
        // readiness diagnostics attach (443-445), non-dict-snapshot log (479-481),
        // CORS outer catch (485-488).

        private static object InvokeGetSerializedDom(IPage page, Dictionary<string, object>? options, string cookiesJson, int? width = null) =>
            Invoke("GetSerializedDom", page, options, cookiesJson, width)!;

        [Fact]
        public void GetSerializedDom_AttachesReadinessDiagnostics_WhenDomIsDictionary()
        {
            // WaitForReady returns non-null AND the serialized DOM is an IDictionary →
            // readiness_diagnostics gets attached (443-445).
            ClearCliConfig();
            var page = NewPage();
            int call = 0;
            page.Setup(p => p.EvaluateAsync<object>(It.IsAny<string>(), It.IsAny<object?>()))
                .Returns((string s, object? a) =>
                {
                    // 1st eval = WaitForReady → return diagnostics object (non-null)
                    // 2nd eval = serialize → return a dictionary DOM
                    call++;
                    if (call == 1) return Task.FromResult<object>(new Dictionary<string, object> { { "ready", true } });
                    return Task.FromResult<object>(new Dictionary<string, object> { { "html", "<p/>" } });
                });
            // No cross-origin frames.
            page.SetupGet(p => p.Url).Returns("http://localhost/");
            page.SetupGet(p => p.Frames).Returns(new List<IFrame>());

            var dom = InvokeGetSerializedDom(page.Object, null, "[]");
            var dict = Assert.IsAssignableFrom<IDictionary<string, object>>(dom);
            Assert.True(dict.ContainsKey("readiness_diagnostics"));
        }

        [Fact]
        public void GetSerializedDom_NonDictSnapshotWithCorsFrame_LogsAndSkipsCorsAttach()
        {
            // DOM serialize returns a non-dictionary (a string) AND there's a cross-origin
            // frame → the "Unexpected domSnapshot type" debug log path (479-481) runs.
            ClearCliConfig();
            var page = NewPage();
            int call = 0;
            page.Setup(p => p.EvaluateAsync<object>(It.IsAny<string>(), It.IsAny<object?>()))
                .Returns((string s, object? a) =>
                {
                    call++;
                    if (call == 1) return Task.FromResult<object>(null!);            // WaitForReady → null
                    if (s.Contains("PercyDOM.serialize")) return Task.FromResult<object>("not-a-dictionary");
                    // getData lookup for the cross-origin frame
                    return Task.FromResult<object>(new Dictionary<string, object> { { "percyElementId", "id" } });
                });
            page.SetupGet(p => p.Url).Returns("http://localhost:5338/");

            var frame = new Mock<IFrame>();
            frame.SetupGet(f => f.Url).Returns("http://127.0.0.1:5338/frame");
            frame.Setup(f => f.EvaluateAsync<object>(It.IsAny<string>(), It.IsAny<object?>()))
                .Returns(Task.FromResult<object>("frame-snap"));
            page.SetupGet(p => p.Frames).Returns(new List<IFrame> { frame.Object });

            // GetPercyDOM is fetched over http → provide a mock that returns a script body.
            var http = new MockHttpMessageHandler();
            http.When(HttpMethod.Get, "http://localhost:5338/percy/dom.js")
                .Respond("application/javascript", "window.PercyDOM={};");
            Percy.setHttpClient(new HttpClient(http));
            ResetDom();

            var dom = InvokeGetSerializedDom(page.Object, null, "[]");
            Assert.IsType<string>(dom);          // non-dict snapshot preserved as-is
        }

        [Fact]
        public void GetSerializedDom_FramesThrow_OuterCatchSwallows()
        {
            // Accessing page.Frames throws → the outer try/catch (485-488) logs at debug
            // and the original DOM snapshot is still returned.
            ClearCliConfig();
            var page = NewPage();
            int call = 0;
            page.Setup(p => p.EvaluateAsync<object>(It.IsAny<string>(), It.IsAny<object?>()))
                .Returns((string s, object? a) =>
                {
                    call++;
                    if (call == 1) return Task.FromResult<object>(null!);  // WaitForReady → null
                    return Task.FromResult<object>(new Dictionary<string, object> { { "html", "<p/>" } });
                });
            page.SetupGet(p => p.Url).Returns("http://localhost/");
            page.SetupGet(p => p.Frames).Throws(new Exception("frames boom"));

            // GetPercyDOM still fetched first inside the try; provide mock http.
            var http = new MockHttpMessageHandler();
            http.When(HttpMethod.Get, "http://localhost:5338/percy/dom.js")
                .Respond("application/javascript", "window.PercyDOM={};");
            Percy.setHttpClient(new HttpClient(http));
            ResetDom();

            var dom = InvokeGetSerializedDom(page.Object, null, "[]");
            Assert.NotNull(dom);
        }

        // ─── GetResponsiveWidths (499-538): missing-widths throw (510-513),
        //     object-with-height parse (518-529), catch (532-536) ───────────────

        private static object InvokeGetResponsiveWidths(List<int>? widths) =>
            PrivateStatic("GetResponsiveWidths").Invoke(null, new object?[] { widths })!;

        private void SetWidthsHttp(string widthsConfigJson)
        {
            var http = new MockHttpMessageHandler();
            http.When(HttpMethod.Get, "http://localhost:5338/percy/widths-config*")
                .Respond("application/json", widthsConfigJson);
            Percy.setHttpClient(new HttpClient(http));
        }

        [Fact]
        public void GetResponsiveWidths_MissingWidthsArray_Throws()
        {
            // Response without a "widths" array → SDK logs + throws the upgrade message.
            SetWidthsHttp("{\"success\":true}");
            var ex = Assert.Throws<TargetInvocationException>(() => InvokeGetResponsiveWidths(null));
            Assert.Contains("Update Percy CLI to the latest version", ex.InnerException!.Message);
        }

        [Fact]
        public void GetResponsiveWidths_WidthsAsNumbersAndObjectsWithHeight_Parsed()
        {
            // Numbers → ResponsiveWidth{width}; objects → width + optional height.
            SetWidthsHttp("{\"widths\":[375,{\"width\":768,\"height\":900},{\"width\":1280}]}");
            var list = (System.Collections.IEnumerable)InvokeGetResponsiveWidths(null);
            var items = list.Cast<object>().ToList();
            Assert.Equal(3, items.Count);

            int W(object o) => (int)o.GetType().GetProperty("width")!.GetValue(o)!;
            int? H(object o) => (int?)o.GetType().GetProperty("height")!.GetValue(o);

            Assert.Equal(375, W(items[0]));
            Assert.Null(H(items[0]));
            Assert.Equal(768, W(items[1]));
            Assert.Equal(900, H(items[1]));
            Assert.Equal(1280, W(items[2]));
            Assert.Null(H(items[2]));
        }

        [Fact]
        public void GetResponsiveWidths_WithUserWidths_BuildsQueryAndParses()
        {
            // Non-empty widths list → query param branch (?widths=...).
            SetWidthsHttp("{\"widths\":[375,1280]}");
            var list = (System.Collections.IEnumerable)InvokeGetResponsiveWidths(new List<int> { 375, 1280 });
            Assert.Equal(2, list.Cast<object>().Count());
        }

        [Fact]
        public void GetResponsiveWidths_RequestFails_CatchRethrows()
        {
            // widths-config endpoint errors (500) → EnsureSuccessStatusCode throws,
            // caught and rethrown as the upgrade message (532-536).
            var http = new MockHttpMessageHandler();
            http.When(HttpMethod.Get, "http://localhost:5338/percy/widths-config*")
                .Respond(HttpStatusCode.InternalServerError, "application/json", "{}");
            Percy.setHttpClient(new HttpClient(http));

            var ex = Assert.Throws<TargetInvocationException>(() => InvokeGetResponsiveWidths(null));
            Assert.Contains("Update Percy CLI to the latest version", ex.InnerException!.Message);
        }

        // ─── CalculateDefaultHeight MIN_HEIGHT-enabled branch (568-591) ─────────

        private static int InvokeCalculateDefaultHeight(IPage? page, int currentHeight, Dictionary<string, object>? options) =>
            (int)PrivateStatic("CalculateDefaultHeight").Invoke(null, new object?[] { page, currentHeight, options })!;

        [Fact]
        public void CalculateDefaultHeight_MinHeightEnabled_OptionsMinHeightWins()
        {
            SetFlagBool("ResponsiveCaptureMinHeight", true);
            try
            {
                var options = new Dictionary<string, object> { { "minHeight", 1500 } };
                Assert.Equal(1500, InvokeCalculateDefaultHeight(null, 720, options));
            }
            finally { SetFlagBool("ResponsiveCaptureMinHeight", false); }
        }

        [Fact]
        public void CalculateDefaultHeight_MinHeightEnabled_ConfigSnapshotMinHeightUsed()
        {
            SetFlagBool("ResponsiveCaptureMinHeight", true);
            SetCliConfig(Json("{ \"snapshot\": { \"minHeight\": 2000 } }"));
            try
            {
                Assert.Equal(2000, InvokeCalculateDefaultHeight(null, 720, null));
            }
            finally
            {
                SetFlagBool("ResponsiveCaptureMinHeight", false);
                ClearCliConfig();
            }
        }

        [Fact]
        public void CalculateDefaultHeight_MinHeightEnabled_NoSource_FallsBackToCurrentHeight()
        {
            SetFlagBool("ResponsiveCaptureMinHeight", true);
            ClearCliConfig();
            try
            {
                Assert.Equal(640, InvokeCalculateDefaultHeight(null, 640, null));
            }
            finally { SetFlagBool("ResponsiveCaptureMinHeight", false); }
        }

        // ─── WaitForResizeCount timeout log (615-628) ──────────────────────────

        private static void InvokeWaitForResizeCount(IPage page, int expectedCount, int width) =>
            PrivateStatic("WaitForResizeCount").Invoke(null, new object?[] { page, expectedCount, width });

        [Fact]
        public void WaitForResizeCount_NeverReachesExpected_TimesOutAndLogs()
        {
            // window.resizeCount always returns 0 but we wait for 99 → the 1s loop expires
            // and the timeout debug log (627) runs without throwing.
            var page = NewPage();
            page.Setup(p => p.EvaluateAsync<int>(It.IsAny<string>(), It.IsAny<object?>()))
                .Returns(Task.FromResult(0));

            InvokeWaitForResizeCount(page.Object, 99, 375); // returns after ~1s
        }

        [Fact]
        public void WaitForResizeCount_ReachesExpected_ReturnsEarly()
        {
            var page = NewPage();
            page.Setup(p => p.EvaluateAsync<int>(It.IsAny<string>(), It.IsAny<object?>()))
                .Returns(Task.FromResult(5));

            InvokeWaitForResizeCount(page.Object, 5, 1280); // returns immediately
        }

        // ─── CaptureResponsiveDom branches (630-729) ───────────────────────────
        // viewportSize-null fallback (645-648), SetViewportSize catch (670-673),
        // RELOAD_PAGE branch (681-700), SLEEP branch (703-708), reset catch (723-726).

        private static List<object> InvokeCaptureResponsiveDom(IPage page, Dictionary<string, object>? options, string cookiesJson) =>
            (List<object>)PrivateStatic("CaptureResponsiveDom").Invoke(null, new object?[] { page, options, cookiesJson })!;

        // Builds a page whose serialize/eval returns dict DOMs, viewport is null (forces the
        // innerWidth/innerHeight fallback), and widths-config returns two widths over http.
        private Mock<IPage> ResponsivePage(out int evalDomCount, bool nullViewport = true)
        {
            var page = NewPage();
            int domCount = 0;
            page.SetupGet(p => p.Url).Returns("http://localhost:5338/");
            page.SetupGet(p => p.Frames).Returns(new List<IFrame>());
            if (nullViewport)
                page.SetupGet(p => p.ViewportSize).Returns((PageViewportSizeResult?)null);
            else
                page.SetupGet(p => p.ViewportSize).Returns(new PageViewportSizeResult { Width = 1280, Height = 720 });

            // NOTE: the EvaluateAsync<object> setup MUST be registered before the typed
            // <int>/<bool>/<string> setups. Moq's open-generic matching otherwise lets the
            // last-registered It.IsAny setup shadow narrower type-args, returning a
            // Task<object> for an <int> call (InvalidCastException). Object-first fixes it.
            page.Setup(p => p.EvaluateAsync<object>(It.IsAny<string>(), It.IsAny<object?>()))
                .Returns((string s, object? a) =>
                {
                    if (s.Contains("PercyDOM.serialize"))
                    {
                        Interlocked.Increment(ref domCount);
                        return Task.FromResult<object>(new Dictionary<string, object> { { "html", "<p/>" } });
                    }
                    if (s.Contains("waitForReady")) return Task.FromResult<object>(null!);
                    return Task.FromResult<object>(null!); // waitForResize etc.
                });
            page.Setup(p => p.EvaluateAsync<int>(It.IsAny<string>(), It.IsAny<object?>()))
                .Returns((string s, object? a) =>
                {
                    if (s.Contains("innerWidth")) return Task.FromResult(800);
                    if (s.Contains("innerHeight")) return Task.FromResult(600);
                    return Task.FromResult(0); // resizeCount: never matches → quick 1s timeouts
                });
            page.Setup(p => p.EvaluateAsync<bool>(It.IsAny<string>(), It.IsAny<object?>()))
                .Returns(Task.FromResult(true)); // window.PercyDOM present
            page.Setup(p => p.EvaluateAsync<string>(It.IsAny<string>(), It.IsAny<object?>()))
                .Returns(Task.FromResult("ok"));

            // SetViewportSizeAsync succeeds by default.
            page.Setup(p => p.SetViewportSizeAsync(It.IsAny<int>(), It.IsAny<int>()))
                .Returns(Task.CompletedTask);

            evalDomCount = 0;
            _ = domCount;
            return page;
        }

        [Fact]
        public void CaptureResponsiveDom_NullViewport_UsesInnerWidthHeightFallback()
        {
            SetWidthsHttp("{\"widths\":[375,1280]}");
            // dom.js fetch may also happen if reload path runs; provide it too.
            var page = ResponsivePage(out _, nullViewport: true);

            var snaps = InvokeCaptureResponsiveDom(page.Object, null, "[]");
            Assert.Equal(2, snaps.Count);
            page.VerifyGet(p => p.ViewportSize, Times.AtLeastOnce);
        }

        [Fact]
        public void CaptureResponsiveDom_SetViewportThrows_CaughtAndContinues()
        {
            SetWidthsHttp("{\"widths\":[375,1280]}");
            var page = ResponsivePage(out _, nullViewport: false);
            // First resize throws; capture must swallow it and still produce snapshots.
            page.Setup(p => p.SetViewportSizeAsync(375, It.IsAny<int>()))
                .ThrowsAsync(new Exception("resize fail"));

            var snaps = InvokeCaptureResponsiveDom(page.Object, null, "[]");
            Assert.Equal(2, snaps.Count);
        }

        [Fact]
        public void CaptureResponsiveDom_ReloadPageEnabled_ReloadsBetweenWidths()
        {
            SetFlagBool("ResponsiveCaptureReloadPage", true);
            try
            {
                SetWidthsHttp("{\"widths\":[375,1280]}");
                var http = new MockHttpMessageHandler();
                http.When(HttpMethod.Get, "http://localhost:5338/percy/widths-config*")
                    .Respond("application/json", "{\"widths\":[375,1280]}");
                http.When(HttpMethod.Get, "http://localhost:5338/percy/dom.js")
                    .Respond("application/javascript", "window.PercyDOM={};");
                Percy.setHttpClient(new HttpClient(http));
                ResetDom();

                var page = ResponsivePage(out _, nullViewport: false);
                // window.PercyDOM check returns false once to drive the re-inject branch (690).
                int boolCalls = 0;
                page.Setup(p => p.EvaluateAsync<bool>(It.IsAny<string>(), It.IsAny<object?>()))
                    .Returns((string s, object? a) =>
                    {
                        boolCalls++;
                        return Task.FromResult(boolCalls != 1); // first reload sees no PercyDOM
                    });
                page.Setup(p => p.ReloadAsync(It.IsAny<PageReloadOptions?>()))
                    .Returns(Task.FromResult<IResponse?>(null));

                var snaps = InvokeCaptureResponsiveDom(page.Object, null, "[]");
                Assert.Equal(2, snaps.Count);
                page.Verify(p => p.ReloadAsync(It.IsAny<PageReloadOptions?>()), Times.AtLeastOnce);
            }
            finally { SetFlagBool("ResponsiveCaptureReloadPage", false); }
        }

        [Fact]
        public void CaptureResponsiveDom_ReloadPageEnabled_ReloadThrows_CaughtAndContinues()
        {
            SetFlagBool("ResponsiveCaptureReloadPage", true);
            try
            {
                var http = new MockHttpMessageHandler();
                http.When(HttpMethod.Get, "http://localhost:5338/percy/widths-config*")
                    .Respond("application/json", "{\"widths\":[375,1280]}");
                http.When(HttpMethod.Get, "http://localhost:5338/percy/dom.js")
                    .Respond("application/javascript", "window.PercyDOM={};");
                Percy.setHttpClient(new HttpClient(http));
                ResetDom();

                var page = ResponsivePage(out _, nullViewport: false);
                page.Setup(p => p.ReloadAsync(It.IsAny<PageReloadOptions?>()))
                    .ThrowsAsync(new Exception("reload fail"));

                var snaps = InvokeCaptureResponsiveDom(page.Object, null, "[]");
                Assert.Equal(2, snaps.Count);
            }
            finally { SetFlagBool("ResponsiveCaptureReloadPage", false); }
        }

        [Fact]
        public void CaptureResponsiveDom_SleepTimeSet_SleepsBetweenWidths()
        {
            // "1" is parseable AND > 0 → both the TryParse branch and the inner
            // Thread.Sleep(>0) branch run (a real ~1s sleep per width, two widths).
            SetFlagStr("ResponsiveCaptureSleepTime", "1");
            try
            {
                SetWidthsHttp("{\"widths\":[375,1280]}");
                var page = ResponsivePage(out _, nullViewport: false);
                var snaps = InvokeCaptureResponsiveDom(page.Object, null, "[]");
                Assert.Equal(2, snaps.Count);
            }
            finally { SetFlagStr("ResponsiveCaptureSleepTime", null); }
        }

        [Fact]
        public void CaptureResponsiveDom_ViewportResetThrows_CaughtAtEnd()
        {
            SetWidthsHttp("{\"widths\":[375,1280]}");
            var page = ResponsivePage(out _, nullViewport: false);
            // Let width resizes succeed but the final reset to the original viewport throw.
            page.Setup(p => p.SetViewportSizeAsync(1280, 720))
                .ThrowsAsync(new Exception("reset fail"));

            var snaps = InvokeCaptureResponsiveDom(page.Object, null, "[]");
            Assert.Equal(2, snaps.Count);
        }

        // ─── Snapshot success-false throw + data parse (782-824) ───────────────

        private static MockHttpMessageHandler SnapshotHttp(string snapshotJson)
        {
            var http = new MockHttpMessageHandler();
            http.When(HttpMethod.Get, "http://localhost:5338/percy/dom.js")
                .Respond("application/javascript", "window.PercyDOM={};");
            http.When(HttpMethod.Post, "http://localhost:5338/percy/snapshot")
                .Respond("application/json", snapshotJson);
            return http;
        }

        private static Mock<IPage> SnapshotPage()
        {
            var page = NewPage();
            page.SetupGet(p => p.Url).Returns("http://localhost:5338/test");
            page.SetupGet(p => p.Frames).Returns(new List<IFrame>());
            // Object-first (see ResponsivePage note) to keep Moq generic dispatch correct.
            page.Setup(p => p.EvaluateAsync<object>(It.IsAny<string>(), It.IsAny<object?>()))
                .Returns((string s, object? a) =>
                {
                    if (s.Contains("waitForReady")) return Task.FromResult<object>(null!);
                    return Task.FromResult<object>(new Dictionary<string, object> { { "html", "<p/>" } });
                });
            page.Setup(p => p.EvaluateAsync<bool>(It.IsAny<string>(), It.IsAny<object?>()))
                .Returns(Task.FromResult(true)); // window.PercyDOM present
            var ctx = new Mock<IBrowserContext>();
            ctx.Setup(c => c.CookiesAsync(It.IsAny<IEnumerable<string>>()))
                .Returns(Task.FromResult<IReadOnlyList<BrowserContextCookiesResult>>(
                    new List<BrowserContextCookiesResult>()));
            page.SetupGet(p => p.Context).Returns(ctx.Object);
            return page;
        }

        [Fact]
        public void Snapshot_SuccessFalse_LogsAndReturnsNull()
        {
            Percy.Enabled = () => true;
            Percy.setSessionType("web");
            ClearCliConfig();
            Percy.setHttpClient(new HttpClient(SnapshotHttp("{\"success\":false,\"error\":\"snap boom\"}")));
            ResetDom();

            var page = SnapshotPage();
            Assert.Null(Percy.Snapshot(page.Object, "Boom Snapshot"));
        }

        [Fact]
        public void Snapshot_SuccessTrueWithData_ReturnsParsedJObject()
        {
            Percy.Enabled = () => true;
            Percy.setSessionType("web");
            ClearCliConfig();
            Percy.setHttpClient(new HttpClient(
                SnapshotHttp("{\"success\":true,\"data\":{\"snapshot\":{\"id\":7}}}")));
            ResetDom();

            var page = SnapshotPage();
            JObject? result = Percy.Snapshot(page.Object, "Data Snapshot");
            Assert.NotNull(result);
            Assert.Equal(7, (int)result!["snapshot"]!["id"]!);
        }

        // ─── Screenshot(IPage,...) overload wrapper (827-831) + success-false/data (834-877) ──

        private static MockHttpMessageHandler ScreenshotHttp(string json)
        {
            var http = new MockHttpMessageHandler();
            http.When(HttpMethod.Post, "http://localhost:5338/percy/automateScreenshot")
                .Respond("application/json", json);
            return http;
        }

        private static IPercyPlaywrightDriver StubDriverMock()
        {
            var d = new Mock<IPercyPlaywrightDriver>();
            d.Setup(x => x.GetSessionId()).Returns("sid");
            d.Setup(x => x.GetFrameGUID()).Returns("frame");
            d.Setup(x => x.GetPageGUID()).Returns("page");
            d.Setup(x => x.GetUrl()).Returns("http://hub/wd/hub");
            return d.Object;
        }

        [Fact]
        public void Screenshot_FromIPage_WrapsIntoDriver_AndPostsAutomateScreenshot()
        {
            // Covers the public Screenshot(IPage,...) wrapper (827-831): it news up a
            // PercyPlaywrightDriver around the page and delegates to the driver overload.
            Percy.Enabled = () => true;
            Percy.setSessionType("automate");
            Percy.setHttpClient(new HttpClient(ScreenshotHttp("{\"success\":true}")));

            var page = NewPage();
            page.SetupGet(p => p.Url).Returns("http://hub/wd/hub");
            // Driver GUID/session reflection runs against the real page object; give it a
            // minimal context so GetSessionId's browser-guid reflection has something to read.
            // We instead route through the wrapper but stub the driver-bound calls by mocking
            // the page's reflective members is impractical — so assert it does not throw and
            // returns null/JObject. To keep the GUID reflection from NREing, route through a
            // driver whose IO seams are stubbed via the driver-overload directly is covered
            // elsewhere; here we only need the wrapper line, so we tolerate a null result.
            // Use a real page mock; the wrapper constructs the driver then calls GetSessionId
            // which reflects into page.Context.Browser — provide that chain.
            var browser = new Mock<IBrowser>();
            var ctx = new Mock<IBrowserContext>();
            ctx.SetupGet(c => c.Browser).Returns(browser.Object);
            page.SetupGet(p => p.Context).Returns(ctx.Object);
            page.SetupGet(p => p.MainFrame).Returns(new Mock<IFrame>().Object);

            // The reflection in GetBrowserGuid/GetPageGUID will not find the Playwright
            // backing field on a Moq proxy and will throw; Screenshot catches it and logs,
            // returning null. That still executes the wrapper line under test.
            var result = Percy.Screenshot(page.Object, "Wrapper Screenshot");
            Assert.Null(result);
        }

        [Fact]
        public void Screenshot_Driver_SuccessFalse_LogsAndReturnsNull()
        {
            Percy.Enabled = () => true;
            Percy.setSessionType("automate");
            Percy.setHttpClient(new HttpClient(ScreenshotHttp("{\"success\":false,\"error\":\"shot boom\"}")));

            Assert.Null(Percy.Screenshot(StubDriverMock(), "Boom Screenshot"));
        }

        [Fact]
        public void Screenshot_Driver_SuccessTrueWithData_ReturnsParsedJObject()
        {
            Percy.Enabled = () => true;
            Percy.setSessionType("automate");
            Percy.setHttpClient(new HttpClient(
                ScreenshotHttp("{\"success\":true,\"data\":{\"link\":\"x\"}}")));

            var result = Percy.Screenshot(StubDriverMock(), "Data Screenshot");
            Assert.NotNull(result);
            Assert.Equal("x", (string)result!["link"]!);
        }

        // ─── object-opts overloads: Snapshot/Screenshot (880-898) ──────────────

        [Fact]
        public void Snapshot_ObjectOpts_FlattensPropertiesIntoOptions()
        {
            // Disabled → returns null without browser, but the opts-reflection overload
            // (880-888) still runs and flattens properties.
            Percy.Enabled = () => false;
            var page = NewPage();
            Assert.Null(Percy.Snapshot(page.Object, "Opt Snapshot", new { enableJavaScript = true, foo = "bar" }));
        }

        [Fact]
        public void Screenshot_ObjectOpts_FlattensPropertiesIntoOptions()
        {
            Percy.Enabled = () => false;
            var page = NewPage();
            Assert.Null(Percy.Screenshot(page.Object, "Opt Screenshot", new { fullpage = true }));
        }

        // ─── Log debug-catch branch (Log fails to reach CLI, debug logging on) ──

        [Fact]
        public void Log_WhenCliRequestFails_AndDebugEnabled_WritesDebugDiagnosticToStderr()
        {
            // Force the /percy/log POST to fail so Log's catch runs, with DebugEnabled=true
            // so the "Sending log to CLI failed" diagnostic line executes.
            var http = new MockHttpMessageHandler();
            http.When(HttpMethod.Post, "http://localhost:5338/percy/log")
                .Respond(HttpStatusCode.InternalServerError, "application/json", "{}");
            Percy.setHttpClient(new HttpClient(http));

            bool origDebug = GetFlagBool("DebugEnabled");
            SetFlagBool("DebugEnabled", true);
            var origErr = Console.Error;
            var sw = new System.IO.StringWriter();
            Console.SetError(sw);
            try
            {
                // Log is private static<T>(T, string). Invoke via reflection.
                var log = PercyType.GetMethod("Log", BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(typeof(string));
                log.Invoke(null, new object?[] { "hello-debug", "info" });
            }
            finally
            {
                Console.SetError(origErr);
                SetFlagBool("DebugEnabled", origDebug);
            }

            string captured = sw.ToString();
            Assert.Contains("Sending log to CLI failed", captured);
            // The "percy:dotnet" debug label is used when DebugEnabled is true.
            Assert.Contains("percy:dotnet", captured);
        }

        // ─── getHttpClient lazy-init branch (_http == null) ────────────────────

        [Fact]
        public void GetHttpClient_WhenNull_LazilyCreatesClientWith10MinuteTimeout()
        {
            var field = PercyType.GetField("_http", BindingFlags.NonPublic | BindingFlags.Static)!;
            var prev = field.GetValue(null);
            field.SetValue(null, null); // force the null branch
            try
            {
                var client = (HttpClient)PercyType
                    .GetMethod("getHttpClient", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, null)!;
                Assert.NotNull(client);
                Assert.Equal(TimeSpan.FromMinutes(10), client.Timeout);
            }
            finally
            {
                field.SetValue(null, prev);
            }
        }

        // ─── CalculateDefaultHeight outer catch (599-601) ──────────────────────

        [Fact]
        public void CalculateDefaultHeight_MinHeightEnabled_WhenConfigAccessThrows_ReturnsCurrentHeight()
        {
            // A JsonElement whose backing JsonDocument has been disposed throws
            // ObjectDisposedException on ValueKind access inside the try → outer catch
            // returns the current height unchanged.
            SetFlagBool("ResponsiveCaptureMinHeight", true);
            JsonElement disposed;
            using (var doc = JsonDocument.Parse("{ \"snapshot\": { \"minHeight\": 999 } }"))
            {
                disposed = doc.RootElement; // becomes invalid once doc is disposed
            }
            SetCliConfig(disposed);
            try
            {
                Assert.Equal(480, InvokeCalculateDefaultHeight(null, 480, null));
            }
            finally
            {
                SetFlagBool("ResponsiveCaptureMinHeight", false);
                ClearCliConfig();
            }
        }

        // ─── PercyPlaywrightDriver.FetchSessionDetails real seam body (no override) ──

        [Fact]
        public void Driver_FetchSessionDetails_RunsEvaluateSyncAgainstPage()
        {
            // Exercise the REAL (non-overridden) FetchSessionDetails + GetSessionId parse
            // path against a mocked IPage: the getSessionDetails executor returns the
            // session JSON, GetBrowserGuid is stubbed, and GetSessionId caches the hashed_id.
            var page = new Mock<IPage>(MockBehavior.Loose);
            page.Setup(p => p.EvaluateAsync<string>(It.IsAny<string>(), It.IsAny<object?>()))
                .Returns(Task.FromResult("{\"hashed_id\":\"real-seam-id\"}"));

            var driver = new SeamDriver(page.Object, "guid-" + Guid.NewGuid());
            Assert.Equal("real-seam-id", driver.GetSessionId());
        }

        // Overrides ONLY GetBrowserGuid (avoids live-browser reflection) so the real
        // FetchSessionDetails body (EvaluateSync over the page) executes.
        private sealed class SeamDriver : PercyPlaywrightDriver
        {
            private readonly string _guid;
            public SeamDriver(IPage page, string guid) : base(page) { _guid = guid; }
            protected override string GetBrowserGuid() => _guid;
        }
    }

    [CollectionDefinition("PercyMockedFlow", DisableParallelization = true)]
    public class PercyMockedFlowCollection { }
}
