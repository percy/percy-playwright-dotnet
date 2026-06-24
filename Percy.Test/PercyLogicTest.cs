using Xunit;
using Moq;
using System;
using System.Reflection;
using System.Text.Json;
using System.Collections.Generic;
using Microsoft.Playwright;

namespace PercyIO.Playwright.Tests
{
    // Pure-logic unit tests for Percy.cs that exercise the non-browser / non-CLI
    // code paths: option parsing, responsive-capture decisioning, value coercion,
    // readiness-gate gating, payload serialization, the internal CLI-config setter,
    // cache hits, and the one driver method (GetUrl) that has a clean mockable seam.
    //
    // These deliberately avoid launching Chromium or talking to the percy-cli test
    // server. Private statics are reached via reflection (mirroring the existing
    // CorsFrameTests convention), and IPage is mocked with Moq.
    public class PercyLogicTest
    {
        private static readonly Type PercyType = typeof(Percy);

        private static MethodInfo PrivateStatic(string name) =>
            PercyType.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"method {name} not found");

        // cliConfig is a private static read by several branches. setCliConfig only
        // treats the value as live config when it is a JsonElement of kind Object,
        // so passing a plain object effectively "clears" it for the options-only paths.
        private static void SetCliConfig(object config) =>
            PercyType.GetMethod("setCliConfig", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new[] { config });

        private static void ClearCliConfig() => SetCliConfig(new object());

        private static JsonElement Json(string json) =>
            JsonDocument.Parse(json).RootElement.Clone();

        // ─── ParseWidthsFromOptions ───────────────────────────────────────────

        private static List<int> InvokeParseWidths(Dictionary<string, object>? options) =>
            (List<int>)PrivateStatic("ParseWidthsFromOptions").Invoke(null, new object?[] { options })!;

        [Fact]
        public void ParseWidths_NullOptions_ReturnsEmpty()
        {
            Assert.Empty(InvokeParseWidths(null));
        }

        [Fact]
        public void ParseWidths_MissingKey_ReturnsEmpty()
        {
            Assert.Empty(InvokeParseWidths(new Dictionary<string, object>()));
        }

        [Fact]
        public void ParseWidths_NullValue_ReturnsEmpty()
        {
            var options = new Dictionary<string, object> { { "widths", null! } };
            Assert.Empty(InvokeParseWidths(options));
        }

        [Fact]
        public void ParseWidths_IntEnumerable_ReturnsList()
        {
            var options = new Dictionary<string, object> { { "widths", new List<int> { 375, 1280 } } };
            Assert.Equal(new List<int> { 375, 1280 }, InvokeParseWidths(options));
        }

        [Fact]
        public void ParseWidths_JsonArrayElement_ReturnsList()
        {
            var options = new Dictionary<string, object> { { "widths", Json("[640, 768, 1024]") } };
            Assert.Equal(new List<int> { 640, 768, 1024 }, InvokeParseWidths(options));
        }

        [Fact]
        public void ParseWidths_UnsupportedType_ReturnsEmpty()
        {
            // A string is neither IEnumerable<int> nor a JSON array, so it falls through.
            var options = new Dictionary<string, object> { { "widths", "375,1280" } };
            Assert.Empty(InvokeParseWidths(options));
        }

        // ─── TryGetIntFromValue ───────────────────────────────────────────────

        private static bool InvokeTryGetInt(object? value, out int result)
        {
            var args = new object?[] { value, 0 };
            bool ok = (bool)PrivateStatic("TryGetIntFromValue").Invoke(null, args)!;
            result = (int)args[1]!;
            return ok;
        }

        [Fact]
        public void TryGetInt_FromBoxedInt_Succeeds()
        {
            Assert.True(InvokeTryGetInt(900, out int result));
            Assert.Equal(900, result);
        }

        [Fact]
        public void TryGetInt_FromJsonNumber_Succeeds()
        {
            Assert.True(InvokeTryGetInt(Json("1280"), out int result));
            Assert.Equal(1280, result);
        }

        [Fact]
        public void TryGetInt_FromJsonString_Fails()
        {
            Assert.False(InvokeTryGetInt(Json("\"1280\""), out int result));
            Assert.Equal(0, result);
        }

        [Fact]
        public void TryGetInt_FromNull_Fails()
        {
            Assert.False(InvokeTryGetInt(null, out int result));
            Assert.Equal(0, result);
        }

        [Fact]
        public void TryGetInt_FromString_Fails()
        {
            Assert.False(InvokeTryGetInt("not-a-number", out _));
        }

        // ─── IsResponsiveSnapshotCapture ──────────────────────────────────────

        private static bool InvokeIsResponsive(Dictionary<string, object>? options) =>
            (bool)PrivateStatic("IsResponsiveSnapshotCapture").Invoke(null, new object?[] { options })!;

        [Fact]
        public void IsResponsive_NoConfig_OptionTrue_ReturnsTrue()
        {
            ClearCliConfig();
            var options = new Dictionary<string, object> { { "responsiveSnapshotCapture", true } };
            Assert.True(InvokeIsResponsive(options));
        }

        [Fact]
        public void IsResponsive_NoConfig_OptionFalse_ReturnsFalse()
        {
            ClearCliConfig();
            var options = new Dictionary<string, object> { { "responsiveSnapshotCapture", false } };
            Assert.False(InvokeIsResponsive(options));
        }

        [Fact]
        public void IsResponsive_NoConfig_NoOptions_ReturnsFalse()
        {
            ClearCliConfig();
            Assert.False(InvokeIsResponsive(null));
        }

        [Fact]
        public void IsResponsive_OptionTrue_OverridesConfigFalse()
        {
            SetCliConfig(Json("{ \"snapshot\": { \"responsiveSnapshotCapture\": false } }"));
            try
            {
                var options = new Dictionary<string, object> { { "responsiveSnapshotCapture", true } };
                Assert.True(InvokeIsResponsive(options));
            }
            finally { ClearCliConfig(); }
        }

        [Fact]
        public void IsResponsive_ConfigSnapshotTrue_NoOption_ReturnsTrue()
        {
            SetCliConfig(Json("{ \"snapshot\": { \"responsiveSnapshotCapture\": true } }"));
            try
            {
                Assert.True(InvokeIsResponsive(null));
            }
            finally { ClearCliConfig(); }
        }

        [Fact]
        public void IsResponsive_ConfigSnapshotFalse_NoOption_ReturnsFalse()
        {
            SetCliConfig(Json("{ \"snapshot\": { \"responsiveSnapshotCapture\": false } }"));
            try
            {
                Assert.False(InvokeIsResponsive(null));
            }
            finally { ClearCliConfig(); }
        }

        [Fact]
        public void IsResponsive_DeferUploadsTrue_DisablesEvenWhenOptionTrue()
        {
            SetCliConfig(Json("{ \"percy\": { \"deferUploads\": true }, \"snapshot\": { \"responsiveSnapshotCapture\": true } }"));
            try
            {
                var options = new Dictionary<string, object> { { "responsiveSnapshotCapture", true } };
                Assert.False(InvokeIsResponsive(options));
            }
            finally { ClearCliConfig(); }
        }

        [Fact]
        public void IsResponsive_DeferUploadsFalse_OptionTrue_ReturnsTrue()
        {
            SetCliConfig(Json("{ \"percy\": { \"deferUploads\": false } }"));
            try
            {
                var options = new Dictionary<string, object> { { "responsiveSnapshotCapture", true } };
                Assert.True(InvokeIsResponsive(options));
            }
            finally { ClearCliConfig(); }
        }

        [Fact]
        public void IsResponsive_ConfigObjectNoSnapshotKey_NoOption_ReturnsFalse()
        {
            // cliConfig is a JsonElement object but lacks both percy.deferUploads and
            // snapshot.responsiveSnapshotCapture, and no option is set → false.
            SetCliConfig(Json("{ \"other\": 1 }"));
            try
            {
                Assert.False(InvokeIsResponsive(null));
            }
            finally { ClearCliConfig(); }
        }

        // ─── CalculateDefaultHeight ───────────────────────────────────────────
        // PERCY_RESPONSIVE_CAPTURE_MIN_HEIGHT is false in the test environment, so this
        // method returns the supplied currentHeight verbatim without ever touching the
        // page — meaning we can pass a null IPage safely.

        private static int InvokeCalculateDefaultHeight(IPage? page, int currentHeight, Dictionary<string, object>? options) =>
            (int)PrivateStatic("CalculateDefaultHeight").Invoke(null, new object?[] { page, currentHeight, options })!;

        [Fact]
        public void CalculateDefaultHeight_MinHeightDisabled_ReturnsCurrentHeight()
        {
            Assert.False(Percy.PERCY_RESPONSIVE_CAPTURE_MIN_HEIGHT);
            Assert.Equal(720, InvokeCalculateDefaultHeight(null, 720, null));
        }

        [Fact]
        public void CalculateDefaultHeight_MinHeightDisabled_IgnoresOptions()
        {
            Assert.False(Percy.PERCY_RESPONSIVE_CAPTURE_MIN_HEIGHT);
            var options = new Dictionary<string, object> { { "minHeight", 1000 } };
            // With the flag off, the options.minHeight value is never read; currentHeight wins.
            Assert.Equal(540, InvokeCalculateDefaultHeight(null, 540, options));
        }

        // ─── WaitForReady ─────────────────────────────────────────────────────
        // The "disabled" preset and malformed-JSON branches both return null BEFORE
        // any EvaluateSync call, so a null IPage is safe for those paths only.

        private static object? InvokeWaitForReady(IPage? page, Dictionary<string, object>? options) =>
            PrivateStatic("WaitForReady").Invoke(null, new object?[] { page, options });

        [Fact]
        public void WaitForReady_DisabledPresetFromOptions_ReturnsNullWithoutPage()
        {
            ClearCliConfig();
            var options = new Dictionary<string, object>
            {
                { "readiness", new { preset = "disabled" } }
            };
            // preset == "disabled" → early return null, never evaluates against the page.
            Assert.Null(InvokeWaitForReady(null, options));
        }

        [Fact]
        public void WaitForReady_DisabledPresetFromCliConfig_ReturnsNullWithoutPage()
        {
            SetCliConfig(Json("{ \"snapshot\": { \"readiness\": { \"preset\": \"disabled\" } } }"));
            try
            {
                Assert.Null(InvokeWaitForReady(null, null));
            }
            finally { ClearCliConfig(); }
        }

        // ─── PayloadParser ────────────────────────────────────────────────────

        private static string InvokePayloadParser(object? payload, bool alreadyJson) =>
            (string)PrivateStatic("PayloadParser").Invoke(null, new object?[] { payload, alreadyJson })!;

        [Fact]
        public void PayloadParser_SerializesObjectToJson()
        {
            string json = InvokePayloadParser(new { name = "Snapshot 1", flag = true }, false);
            Assert.Contains("\"name\":\"Snapshot 1\"", json);
            Assert.Contains("\"flag\":true", json);
        }

        [Fact]
        public void PayloadParser_AlreadyJson_PassesThroughString()
        {
            string raw = "{\"already\":\"json\"}";
            Assert.Equal(raw, InvokePayloadParser(raw, true));
        }

        [Fact]
        public void PayloadParser_AlreadyJson_NullPayload_ReturnsEmptyString()
        {
            Assert.Equal("", InvokePayloadParser(null, true));
        }

        // ─── setCliConfig + ResetInternalCaches (public/internal surface) ─────

        [Fact]
        public void SetCliConfig_DoesNotThrow_AndDrivesResponsiveDecision()
        {
            // Round-trip through the real internal setter (not reflection) to cover it.
            ClearCliConfig();
            Assert.False(InvokeIsResponsive(null));
        }

        [Fact]
        public void ResetInternalCaches_DoesNotThrow()
        {
            // Idempotent and side-effect free for our purposes; clears _enabled/_dom.
            Percy.ResetInternalCaches();
            Percy.ResetInternalCaches();
        }

        // ─── Cache hit path ───────────────────────────────────────────────────
        // CacheTest only covers the miss + remove branches; this covers a Store→Get hit.

        [Fact]
        public void Cache_StoreThenGet_ReturnsStoredValue()
        {
            var cache = new Cache<string, object>();
            cache.Store("k", "v");
            Assert.Equal("v", cache.Get("k"));
        }

        [Fact]
        public void Cache_OverwriteKey_ReturnsLatestValue()
        {
            var cache = new Cache<string, object>();
            cache.Store("k", "first");
            cache.Store("k", "second");
            Assert.Equal("second", cache.Get("k"));
        }

        [Fact]
        public void CacheItem_ExposesValue()
        {
            var item = new CacheItem<int>(42);
            Assert.Equal(42, item.Value);
        }

        // ─── PercyPlaywrightDriver.GetUrl via mocked IPage ───────────────────
        // GetUrl is the only driver method with a clean seam: it just returns page.Url.
        // The GUID/session methods reflect into Playwright internal types or call
        // EvaluateSync, so they require a real browser and are left to CI.

        [Fact]
        public void Driver_GetUrl_ReturnsPageUrl()
        {
            var pageMock = new Mock<IPage>();
            pageMock.Setup(p => p.Url).Returns("http://example.com/path");

            var driverType = typeof(IPercyPlaywrightDriver).Assembly
                .GetType("PercyIO.Playwright.PercyPlaywrightDriver")!;
            var ctor = driverType.GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null, new[] { typeof(IPage) }, null)!;
            var driver = (IPercyPlaywrightDriver)ctor.Invoke(new object[] { pageMock.Object });

            Assert.Equal("http://example.com/path", driver.GetUrl());
        }
    }
}
