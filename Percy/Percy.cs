using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Net.Http;
using System.Reflection;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Playwright;
using Newtonsoft.Json.Linq;

namespace PercyIO.Playwright
{
    public static class Percy
    {
        public static readonly bool DEBUG =
            Environment.GetEnvironmentVariable("PERCY_LOGLEVEL") == "debug";
        // Behavior-preserving seam: internal code reads debug-level through this mirror,
        // which defaults to the public env-derived DEBUG flag (unchanged behavior). Tests
        // can flip it to exercise the debug-only logging branches without setting env vars.
        internal static bool DebugEnabled = DEBUG;
        public static readonly string CLI_API =
            Environment.GetEnvironmentVariable("PERCY_CLI_API") ?? "http://localhost:5338";
        public static readonly string CLIENT_INFO =
            typeof(Percy).Assembly.GetCustomAttribute<ClientInfoAttribute>().ClientInfo;
        public static readonly string ENVIRONMENT_INFO = Regex.Replace(
            Regex.Replace(RuntimeInformation.FrameworkDescription, @"\s+", "-"),
            @"-([\d\.]+).*$", "/$1").Trim().ToLower();

        private static void Log<T>(T message, string lvl = "info")
        {
            string label = DebugEnabled ? "percy:dotnet" : "percy";
            string plainMessage = $"[{label}] {message}";
            string ansiMessage = $"[\u001b[35m{label}\u001b[39m] {message}";
            // Send log message to Percy CLI
            try
            {
                Dictionary<string, object> logPayload = new Dictionary<string, object>
                {
                    { "message", ansiMessage },
                    { "level", lvl }
                };
                Request("/percy/log", logPayload);
            }
            catch (Exception e)
            {
                if (DebugEnabled)
                    Console.Error.WriteLine($"[{label}] Sending log to CLI failed: {e.Message}");
            }
            finally
            {
                // Write to stderr (Console.Error) rather than stdout for two reasons:
                // 1. stderr is unbuffered by default, so output appears immediately even inside catch/finally blocks.
                // 2. Tools like `npx percy exec` forward stderr directly to the terminal, whereas stdout can be
                //    swallowed or delayed when the process exits before the buffer is flushed.
                if (lvl != "debug" || DebugEnabled)
                {
                    Console.Error.WriteLine(plainMessage);
                }
            }
        }

        // volatile so the unlocked read in getHttpClient sees a fully-published
        // HttpClient (Timeout set) before the field reference becomes visible
        // to other threads on weak memory models (e.g. ARM).
        private static volatile HttpClient? _http;

        private static string? sessionType = null;
        private static object? cliConfig;

        public static readonly string? RESPONSIVE_CAPTURE_SLEEP_TIME =
            Environment.GetEnvironmentVariable("RESPONSIVE_CAPTURE_SLEEP_TIME");
        public static readonly bool PERCY_RESPONSIVE_CAPTURE_MIN_HEIGHT =
            (Environment.GetEnvironmentVariable("PERCY_RESPONSIVE_CAPTURE_MIN_HEIGHT") ?? "")
                .Equals("true", StringComparison.OrdinalIgnoreCase);
        public static readonly bool PERCY_RESPONSIVE_CAPTURE_RELOAD_PAGE =
            (Environment.GetEnvironmentVariable("PERCY_RESPONSIVE_CAPTURE_RELOAD_PAGE") ?? "")
                .Equals("true", StringComparison.OrdinalIgnoreCase);

        // Behavior-preserving seams for the three responsive-capture feature flags.
        // The public readonly fields above remain the canonical, env-derived values
        // (unchanged public API). Production code reads through these internal mirrors,
        // which default to exactly those env values, so runtime behavior is identical.
        // Tests can flip a mirror to exercise an otherwise env-gated branch without
        // mutating process environment or relying on type-initialization ordering.
        internal static string? ResponsiveCaptureSleepTime = RESPONSIVE_CAPTURE_SLEEP_TIME;
        internal static bool ResponsiveCaptureMinHeight = PERCY_RESPONSIVE_CAPTURE_MIN_HEIGHT;
        internal static bool ResponsiveCaptureReloadPage = PERCY_RESPONSIVE_CAPTURE_RELOAD_PAGE;

        // Behavior-preserving seam for the readiness-JSON string that WaitForReady feeds
        // into JsonDocument.Parse. Defaults to null, so production uses the readinessJson
        // computed from options/cliConfig verbatim — runtime behavior is identical. A test
        // can install a transform to inject a malformed string and exercise the
        // malformed-JSON defensive catch, which is otherwise unreachable because
        // JsonSerializer.Serialize / JsonElement.GetRawText only ever yield valid JSON.
        internal static Func<string, string>? ReadinessJsonTransform = null;

        private static string PayloadParser(object? payload = null, bool alreadyJson = false)
        {
            if (alreadyJson)
            {
                return payload is null ? "" : payload.ToString();
            }
            return JsonSerializer.Serialize(payload).ToString();
        }

        public static void setHttpClient(HttpClient client)
        {
            _http = client;
        }

        private static readonly object _httpLock = new object();

        internal static HttpClient getHttpClient()
        {
            // Double-checked locking — concurrent first-callers must not observe an
            // _http with its default timeout (we set Timeout AFTER assigning the
            // field, so without the lock another thread could grab the half-built
            // client). Matches the pattern in Percy.Selenium.
            if (_http == null)
            {
                lock (_httpLock)
                {
                    if (_http == null)
                    {
                        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                        _http = client;
                    }
                }
            }
            return _http;
        }

        public static void setSessionType(string? type)
        {
            sessionType = type;
        }

        internal static void setCliConfig(object config)
        {
            cliConfig = config;
        }

        // Added isJson since current JSON parsing doesn’t support nested objects and thats why we using different lib
        private static dynamic Request(string endpoint, object? payload = null, bool isJson = false)
        {
            StringContent? body = payload == null ? null : new StringContent(
                PayloadParser(payload, isJson), Encoding.UTF8, "application/json");

            HttpClient httpClient = getHttpClient();
            Task<HttpResponseMessage> apiTask = body != null
                ? httpClient.PostAsync($"{CLI_API}{endpoint}", body)
                : httpClient.GetAsync($"{CLI_API}{endpoint}");
            apiTask.Wait();

            HttpResponseMessage response = apiTask.Result;
            response.EnsureSuccessStatusCode();

            Task<string> contentTask = response.Content.ReadAsStringAsync();
            contentTask.Wait();

            IEnumerable<string>? version = null;
            response.Headers.TryGetValues("x-percy-core-version", out version);

            return new
            {
                version = version == null ? null : version.First(),
                content = contentTask.Result
            };
        }

        private static string? _dom = null;
        private static string GetPercyDOM()
        {
            if (_dom != null) return (string)_dom;
            _dom = Request("/percy/dom.js").content;
            return (string)_dom;
        }

        private static bool? _enabled = null;
        public static Func<bool> Enabled = () =>
        {
            if (_enabled != null) return (bool)_enabled;

            try
            {
                dynamic res = Request("/percy/healthcheck");
                dynamic data = JsonSerializer.Deserialize<dynamic>(res.content);

                if (data.GetProperty("success").GetBoolean() != true)
                {
                    throw new Exception(data.error);
                }
                else if (res.version == null)
                {
                    Log("You may be using @percy/agent " +
                        "which is no longer supported by this SDK. " +
                        "Please uninstall @percy/agent and install @percy/cli instead. " +
                        "https://www.browserstack.com/docs/percy/migration/migrate-to-cli");
                    return (bool)(_enabled = false);
                }
                else if (res.version[0] != '1')
                {
                    Log($"Unsupported Percy CLI version, {res.version}");
                    return (bool)(_enabled = false);
                }
                else
                {
                    if (data.TryGetProperty("type", out JsonElement type))
                    {
                        setSessionType(type.ToString());
                    }

                    if (data.TryGetProperty("config", out JsonElement config))
                    {
                        setCliConfig(config);
                    }
                    return (bool)(_enabled = true);
                }
            }
            catch (Exception error)
            {
                Log("Percy is not running, disabling snapshots");
                if (DebugEnabled) Log<Exception>(error);
                return (bool)(_enabled = false);
            }
        };
        public class Region 
        { 
            public RegionElementSelector elementSelector { get; set; } 
            public RegionPadding padding { get; set; } 
            public string algorithm { get; set; } 
            public RegionConfiguration configuration { get; set; } 
            public RegionAssertion assertion { get; set; } 

            // Rename the nested class to RegionElementSelector or another unique name
            public class RegionElementSelector 
            { 
                public RegionBoundingBox boundingBox { get; set; } 
                public string elementXpath { get; set; } 
                public string elementCSS { get; set; } 
            } 

            public class RegionBoundingBox 
            { 
                public int top { get; set; } 
                public int left { get; set; } 
                public int width { get; set; } 
                public int height { get; set; } 
            } 

            public class RegionPadding 
            { 
                public int top { get; set; } 
                public int left { get; set; } 
                public int right { get; set; } 
                public int bottom { get; set; } 
            } 

            public class RegionConfiguration 
            { 
                public int? diffSensitivity { get; set; } 
                public double? imageIgnoreThreshold { get; set; } 
                public bool? carouselsEnabled { get; set; } 
                public bool? bannersEnabled { get; set; } 
                public bool? adsEnabled { get; set; } 
            } 

            public class RegionAssertion 
            { 
                public double? diffIgnoreThreshold { get; set; } 
            }
        }


        public static Region CreateRegion(
            Region.RegionBoundingBox? boundingBox = null,
            string? elementXpath = null,
            string? elementCSS = null,
            Region.RegionPadding? padding = null,
            string algorithm = "ignore",
            int? diffSensitivity = null,
            double? imageIgnoreThreshold = null,
            bool? carouselsEnabled = null,
            bool? bannersEnabled = null,
            bool? adsEnabled = null,
            double? diffIgnoreThreshold = null)
        {
            var elementSelector = new Region.RegionElementSelector
            {
                boundingBox = boundingBox,
                elementXpath = elementXpath,
                elementCSS = elementCSS
            };

            var region = new Region
            {
                algorithm = algorithm,
                elementSelector = elementSelector,
                padding = padding
            };

            if (new[] { "standard", "intelliignore" }.Contains(algorithm))
            {
                var configuration = new Region.RegionConfiguration
                {
                    diffSensitivity = diffSensitivity,
                    imageIgnoreThreshold = imageIgnoreThreshold,
                    carouselsEnabled = carouselsEnabled,
                    bannersEnabled = bannersEnabled,
                    adsEnabled = adsEnabled
                };

                // Check if any configuration value is set and add it to the region
                if (configuration.diffSensitivity.HasValue || 
                    configuration.imageIgnoreThreshold.HasValue || 
                    configuration.carouselsEnabled.HasValue || 
                    configuration.bannersEnabled.HasValue || 
                    configuration.adsEnabled.HasValue)
                {
                    region.configuration = configuration;
                }
            }

            if (diffIgnoreThreshold.HasValue)
            {
                region.assertion = new Region.RegionAssertion
                {
                    diffIgnoreThreshold = diffIgnoreThreshold
                };
            }

            return region;
        }


        public class Options : Dictionary<string, object> { }

        /// <summary>
        /// Use CDP to discover closed shadow roots and expose them to PercyDOM.serialize().
        /// </summary>
        private static void ExposeClosedShadowRoots(IPage page)
        {
            ICDPSession? cdpSession = null;
            try
            {
                cdpSession = page.Context.NewCDPSessionAsync(page).ConfigureAwait(false).GetAwaiter().GetResult();
            }
            catch (Exception err)
            {
                Log($"CDP session unavailable: {err.Message}", "debug");
                return;
            }

            try
            {
                cdpSession.SendAsync("DOM.enable").ConfigureAwait(false).GetAwaiter().GetResult();

                var docResult = cdpSession.SendAsync("DOM.getDocument", new Dictionary<string, object>
                {
                    { "depth", -1 },
                    { "pierce", true }
                }).ConfigureAwait(false).GetAwaiter().GetResult();

                var root = docResult.Value.GetProperty("root");

                var closedPairs = new List<(int hostId, int shadowId)>();
                // Pass the page URL so WalkNodes can decide whether to recurse INTO
                // an iframe's contentDocument: same-origin iframes share the parent
                // page's JS realm + WeakMap, so closed shadow roots inside them must
                // be collected too. Cross-origin iframes do NOT share the realm and
                // are skipped entirely (their closed shadow roots are handled by the
                // per-frame ProcessFrame path).
                string pageUrl = page.Url ?? string.Empty;
                WalkNodes(root, closedPairs, pageUrl);

                if (closedPairs.Count == 0) return;

                Log($"Found {closedPairs.Count} closed shadow root(s), exposing via CDP", "debug");

                page.EvaluateAsync("() => { window.__percyClosedShadowRoots = window.__percyClosedShadowRoots || new WeakMap(); }")
                    .ConfigureAwait(false).GetAwaiter().GetResult();

                foreach (var (hostId, shadowId) in closedPairs)
                {
                    // Per-pair try/catch: if a single host went detached between
                    // DOM.getDocument and resolveNode (TOCTOU), an unchecked
                    // GetProperty would throw KeyNotFoundException and abort
                    // exposure for every remaining pair. Skip the bad one and
                    // keep going so partial capture still wins.
                    try
                    {
                        var hostResult = cdpSession.SendAsync("DOM.resolveNode", new Dictionary<string, object>
                        {
                            { "backendNodeId", hostId }
                        }).ConfigureAwait(false).GetAwaiter().GetResult();
                        if (!hostResult.HasValue ||
                            !hostResult.Value.TryGetProperty("object", out JsonElement hostObj) ||
                            !hostObj.TryGetProperty("objectId", out JsonElement hostIdEl))
                            continue;
                        var hostObjectId = hostIdEl.GetString();

                        var shadowResult = cdpSession.SendAsync("DOM.resolveNode", new Dictionary<string, object>
                        {
                            { "backendNodeId", shadowId }
                        }).ConfigureAwait(false).GetAwaiter().GetResult();
                        if (!shadowResult.HasValue ||
                            !shadowResult.Value.TryGetProperty("object", out JsonElement shadowObj) ||
                            !shadowObj.TryGetProperty("objectId", out JsonElement shadowIdEl))
                            continue;
                        var shadowObjectId = shadowIdEl.GetString();

                        if (hostObjectId == null || shadowObjectId == null) continue;

                        cdpSession.SendAsync("Runtime.callFunctionOn", new Dictionary<string, object>
                        {
                            { "functionDeclaration", "function(shadowRoot) { window.__percyClosedShadowRoots.set(this, shadowRoot); }" },
                            { "objectId", hostObjectId },
                            { "arguments", new[] { new Dictionary<string, object> { { "objectId", shadowObjectId } } } }
                        }).ConfigureAwait(false).GetAwaiter().GetResult();
                    }
                    catch (Exception perPairErr)
                    {
                        Log($"Failed to expose one closed shadow root: {perPairErr.Message}", "debug");
                    }
                }
            }
            catch (Exception err)
            {
                Log($"Could not expose closed shadow roots via CDP: {err.Message}", "debug");
            }
            finally
            {
                if (cdpSession != null)
                {
                    try { cdpSession.DetachAsync().ConfigureAwait(false).GetAwaiter().GetResult(); } catch { }
                }
            }
        }

        private static void WalkNodes(JsonElement node, List<(int hostId, int shadowId)> closedPairs, string pageUrl)
        {
            // Same-origin iframe contentDocument trees: their closed shadow roots
            // live in the parent page's JS realm, so PercyDOM.serialize() at the
            // parent level needs them in window.__percyClosedShadowRoots too. We
            // recurse INTO contentDocument only when same-origin; cross-origin
            // frames have a separate realm and a separate WeakMap, so the per-frame
            // ProcessFrame path handles them.
            if (node.TryGetProperty("contentDocument", out var contentDoc))
            {
                string frameDocUrl = null!;
                if (contentDoc.ValueKind == JsonValueKind.Object &&
                    contentDoc.TryGetProperty("documentURL", out var docUrlEl) &&
                    docUrlEl.ValueKind == JsonValueKind.String)
                {
                    frameDocUrl = docUrlEl.GetString() ?? string.Empty;
                }

                // IsCrossOriginFrame returns false for empty / unsupported schemes
                // (about:blank, data:, blob:, etc.) — treat those as same-origin
                // and recurse, matching the JS SDK behaviour.
                if (!string.IsNullOrEmpty(pageUrl) &&
                    !IsCrossOriginFrame(frameDocUrl ?? string.Empty, pageUrl))
                {
                    WalkNodes(contentDoc, closedPairs, pageUrl);
                }
                return;
            }

            if (node.TryGetProperty("shadowRoots", out var shadowRoots))
            {
                foreach (var sr in shadowRoots.EnumerateArray())
                {
                    if (sr.TryGetProperty("shadowRootType", out var type) && type.GetString() == "closed")
                    {
                        closedPairs.Add((
                            node.GetProperty("backendNodeId").GetInt32(),
                            sr.GetProperty("backendNodeId").GetInt32()
                        ));
                    }
                    WalkNodes(sr, closedPairs, pageUrl);
                }
            }

            if (node.TryGetProperty("children", out var children))
            {
                foreach (var child in children.EnumerateArray())
                {
                    WalkNodes(child, closedPairs, pageUrl);
                }
            }
        }

        // Schemes whose iframe content can't be (or shouldn't be) captured —
        // matches Percy.Selenium's IsUnsupportedIframeSrc. Lower-case prefix match.
        private static readonly string[] _unsupportedIframeSchemes = new[]
        {
            "about:", "chrome:", "chrome-extension:", "devtools:", "edge:",
            "opera:", "view-source:", "data:", "javascript:", "vbscript:", "blob:"
        };

        private static bool IsCrossOriginFrame(string frameUrl, string pageUrl)
        {
            if (string.IsNullOrEmpty(frameUrl)) return false;
            var lower = frameUrl.ToLowerInvariant();
            foreach (var prefix in _unsupportedIframeSchemes)
            {
                if (lower.StartsWith(prefix)) return false;
            }

            try
            {
                var pageUri = new Uri(pageUrl);
                var frameUri = new Uri(frameUrl);
                return pageUri.Host != frameUri.Host;
            }
            catch
            {
                return false;
            }
        }

        private static object? ProcessFrame(IPage page, IFrame frame, Dictionary<string, object>? options, string percyDomScript)
        {
            string frameUrl = frame.Url;

            try
            {
                // Inject Percy DOM into the cross-origin frame
                // .GetAwaiter().GetResult() is used to block synchronously; EvaluateSync is not available for IFrame, only IPage
                frame.EvaluateAsync(percyDomScript).ConfigureAwait(false).GetAwaiter().GetResult();

                // enableJavaScript=True prevents the standard iframe serialization logic from running.
                // This is necessary because we're manually handling cross-origin iframe serialization here.
                var optionsForFrame = new Dictionary<string, object>(options ?? new Dictionary<string, object>());
                optionsForFrame["enableJavaScript"] = true;

                // Serialize the frame
                string serializeScript = $"PercyDOM.serialize({JsonSerializer.Serialize(optionsForFrame)})";
                // .GetAwaiter().GetResult() is used to block synchronously; EvaluateSync is not available for IFrame, only IPage
                var iframeSnapshot = frame.EvaluateAsync(serializeScript).ConfigureAwait(false).GetAwaiter().GetResult();

                // Get the iframe's element data from the main page context
                string getDataScript = "(fUrl) => {\n" +
                    "const iframes = Array.from(document.querySelectorAll('iframe'));\n" +
                    "const matchingIframe = iframes.find(iframe => iframe.src.startsWith(fUrl));\n" +
                    "if (matchingIframe) {\n" +
                    "return {\n" +
                    "percyElementId: matchingIframe.getAttribute('data-percy-element-id')\n" +
                    "};\n" +
                    "}\n" +
                    "}";

                var iframeData = PercyPlaywrightDriver.EvaluateSync<object>(page, getDataScript, frameUrl);

                if (iframeData == null)
                {
                    Log($"Skipping cross-origin frame {frameUrl}: no matching iframe element with percyElementId found on main page", "debug");
                    return null;
                }

                return new
                {
                    iframeData = iframeData,
                    iframeSnapshot = iframeSnapshot,
                    frameUrl = frameUrl
                };
            }
            catch (Exception e)
            {
                Log($"Failed to process cross-origin frame {frameUrl}: {e.Message}", "debug");
                return null;
            }
        }

        // Readiness gate. Runs PercyDOM.waitForReady via EvaluateSync;
        // Playwright auto-awaits the returned Promise. Checks typeof in-browser so
        // older CLI versions without the method are a graceful no-op. Returns
        // diagnostics to attach to the domSnapshot, or null.
        private static object? WaitForReady(IPage page, Dictionary<string, object>? options)
        {
            string readinessJson = "{}";
            if (options != null && options.TryGetValue("readiness", out var perSnapshot) && perSnapshot != null)
            {
                readinessJson = JsonSerializer.Serialize(perSnapshot);
            }
            else if (cliConfig is JsonElement configElement &&
                     configElement.ValueKind == JsonValueKind.Object &&
                     configElement.TryGetProperty("snapshot", out JsonElement snapshotElement) &&
                     snapshotElement.ValueKind == JsonValueKind.Object &&
                     snapshotElement.TryGetProperty("readiness", out JsonElement readinessElement) &&
                     readinessElement.ValueKind == JsonValueKind.Object)
            {
                readinessJson = readinessElement.GetRawText();
            }

            // No-op in production (ReadinessJsonTransform is null); a test seam can
            // substitute a malformed string to exercise the defensive parse-catch below.
            if (ReadinessJsonTransform != null) readinessJson = ReadinessJsonTransform(readinessJson);

            // Skip when preset is disabled
            try
            {
                using JsonDocument doc = JsonDocument.Parse(readinessJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("preset", out JsonElement presetElement) &&
                    presetElement.ValueKind == JsonValueKind.String &&
                    presetElement.GetString() == "disabled")
                {
                    return null;
                }
            }
            catch { /* fall through on malformed JSON */ }

            string script =
                "(() => {"
                + $"  var cfg = {readinessJson};"
                + "  if (typeof PercyDOM !== 'undefined' && typeof PercyDOM.waitForReady === 'function') {"
                + "    return PercyDOM.waitForReady(cfg);"
                + "  }"
                + "  return null;"
                + "})()";
            try
            {
                return PercyPlaywrightDriver.EvaluateSync<object>(page, script);
            }
            catch (Exception e)
            {
                Log($"waitForReady failed, proceeding to serialize: {e.Message}", "debug");
                return null;
            }
        }

        private static object GetSerializedDom(IPage page, Dictionary<string, object>? options, string cookiesJson, int? width = null)
        {
            // Readiness gate before serialize. Graceful on old CLI.
            object? readinessDiagnostics = WaitForReady(page, options);

            string opts = JsonSerializer.Serialize(options);
            string widthAssignment = width.HasValue ? $"dom.width = {width.Value};" : "";
            string script = $"(() => {{ const dom = PercyDOM.serialize({opts}); dom.cookies = {cookiesJson}; {widthAssignment} return dom; }})()";
            var domSnapshot = PercyPlaywrightDriver.EvaluateSync<object>(page, script);

            // Attach readiness diagnostics so the CLI can log timing and pass/fail
            if (readinessDiagnostics != null && domSnapshot is IDictionary<string, object> readinessDict)
            {
                readinessDict["readiness_diagnostics"] = readinessDiagnostics;
            }

            // Process CORS IFrames
            try
            {
                string percyDomScript = GetPercyDOM();
                var frames = page.Frames;

                // Filter for cross-origin frames (excluding about:blank)
                var crossOriginFrames = frames
                    .Where(f => IsCrossOriginFrame(f.Url, page.Url))
                    .ToList();

                if (crossOriginFrames.Count > 0 && !string.IsNullOrEmpty(percyDomScript))
                {
                    var processedFrames = new List<object>();
                    foreach (var frame in crossOriginFrames)
                    {
                        var result = ProcessFrame(page, frame, options, percyDomScript);
                        if (result != null)
                        {
                            processedFrames.Add(result);
                        }
                    }

                    if (processedFrames.Count > 0)
                    {   
                        // Cast domSnapshot to IDictionary to add the corsIframes property
                        // This preserves the ExpandoObject type and avoids serialization issues
                        if (domSnapshot is IDictionary<string, object> domDict)
                        {
                            domDict["corsIframes"] = processedFrames;
                        }
                        else
                        {
                            Log("Unexpected domSnapshot type, unable to add corsIframes property", "debug");    
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log($"Failed to process cross-origin iframes: {e.Message}", "debug");
            }

            return domSnapshot;
        }

        private class ResponsiveWidth
        {
            public int width { get; set; }
            public int? height { get; set; }
        }

        private static List<ResponsiveWidth> GetResponsiveWidths(List<int>? widths = null)
        {
            widths ??= new List<int>();

            try
            {
                string queryParam = widths.Count > 0 ? $"?widths={string.Join(",", widths)}" : "";
                dynamic res = Request($"/percy/widths-config{queryParam}");
                var data = JsonSerializer.Deserialize<JsonElement>(res.content);

                if (!data.TryGetProperty("widths", out JsonElement widthsElement) || widthsElement.ValueKind != JsonValueKind.Array)
                {
                    Log("Update Percy CLI to the latest version to use responsiveSnapshotCapture", "error");
                    throw new Exception("Update Percy CLI to the latest version to use responsiveSnapshotCapture");
                }

                return widthsElement.EnumerateArray().Select(widthItem =>
                {
                    if (widthItem.ValueKind == JsonValueKind.Number)
                    {
                        return new ResponsiveWidth { width = widthItem.GetInt32() };
                    }

                    int width = widthItem.GetProperty("width").GetInt32();
                    int? height = null;
                    if (widthItem.TryGetProperty("height", out JsonElement heightElement) && heightElement.ValueKind == JsonValueKind.Number)
                    {
                        height = heightElement.GetInt32();
                    }

                    return new ResponsiveWidth { width = width, height = height };
                }).ToList();
            }
            catch (Exception error)
            {
                Log("Update Percy CLI to the latest version to use responsiveSnapshotCapture", "error");
                Log($"Failed to get responsive widths: {error}", "debug");
                throw new Exception("Update Percy CLI to the latest version to use responsiveSnapshotCapture", error);
            }
        }

        private static List<int> ParseWidthsFromOptions(Dictionary<string, object>? options)
        {
            if (options == null || !options.TryGetValue("widths", out object widthsObj) || widthsObj == null)
            {
                return new List<int>();
            }

            if (widthsObj is IEnumerable<int> enumerableWidths)
            {
                return enumerableWidths.ToList();
            }

            if (widthsObj is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.Array)
            {
                return jsonElement.EnumerateArray().Select(x => x.GetInt32()).ToList();
            }

            return new List<int>();
        }

        private static int CalculateDefaultHeight(IPage page, int currentHeight, Dictionary<string, object>? options)
        {
            if (!ResponsiveCaptureMinHeight)
            {
                return currentHeight;
            }

            try
            {
                int minHeight = currentHeight;

                // Fallback order: options.minHeight -> config.snapshot.minHeight -> currentHeight.
                if (options != null &&
                    options.TryGetValue("minHeight", out object? minHeightFromOptions) &&
                    TryGetIntFromValue(minHeightFromOptions, out int parsedOptionsMinHeight))
                {
                    minHeight = parsedOptionsMinHeight;
                }
                else if (cliConfig is JsonElement configElement &&
                         configElement.ValueKind == JsonValueKind.Object &&
                         configElement.TryGetProperty("snapshot", out JsonElement snapshotElement) &&
                         snapshotElement.ValueKind == JsonValueKind.Object &&
                         snapshotElement.TryGetProperty("minHeight", out JsonElement minHeightElement) &&
                         TryGetIntFromValue(minHeightElement, out int parsedConfigMinHeight))
                {
                    minHeight = parsedConfigMinHeight;
                }
                return minHeight;
            }
            catch
            {
                return currentHeight;
            }
        }

        private static bool TryGetIntFromValue(object? value, out int result)
        {
            if (value is int intValue)
            {
                result = intValue;
                return true;
            }

            if (value is JsonElement element &&
                element.ValueKind == JsonValueKind.Number &&
                element.TryGetInt32(out int jsonInt))
            {
                result = jsonInt;
                return true;
            }

            result = default;
            return false;
        }

        private static void WaitForResizeCount(IPage page, int expectedCount, int width)
        {
            var start = DateTime.UtcNow;
            while (DateTime.UtcNow - start < TimeSpan.FromSeconds(1))
            {
                int current = PercyPlaywrightDriver.EvaluateSync<int>(page, "window.resizeCount");
                if (current == expectedCount)
                {
                    return;
                }
                Thread.Sleep(50);
            }
            Log($"Timed out waiting for window resize event for width {width}", "debug");
        }

        internal static List<object> CaptureResponsiveDom(IPage page, Dictionary<string, object>? options, string cookiesJson)
        {
            List<int> userWidths = ParseWidthsFromOptions(options);
            List<ResponsiveWidth> widthHeights = GetResponsiveWidths(userWidths);
            var domSnapshots = new List<object>();

            int currentWidth;
            int currentHeight;
            var viewportSize = page.ViewportSize;
            if (viewportSize != null)
            {
                currentWidth = viewportSize.Width;
                currentHeight = viewportSize.Height;
            }
            else
            {
                currentWidth = PercyPlaywrightDriver.EvaluateSync<int>(page, "window.innerWidth");
                currentHeight = PercyPlaywrightDriver.EvaluateSync<int>(page, "window.innerHeight");
            }

            int lastWindowWidth = currentWidth;
            int lastWindowHeight = currentHeight;
            int resizeCount = 0;
            int sleepTime = 0;
            int defaultHeight = CalculateDefaultHeight(page, currentHeight, options);

            PercyPlaywrightDriver.EvaluateSync<object>(page, "PercyDOM.waitForResize()");

            foreach (ResponsiveWidth widthHeight in widthHeights)
            {
                int width = widthHeight.width;
                int height = widthHeight.height ?? defaultHeight;

                if (lastWindowWidth != width || lastWindowHeight != height)
                {
                    resizeCount++;
                    try
                    {
                        PercyPlaywrightDriver.SetViewportSizeSync(page, width, height);
                    }
                    catch (Exception error)
                    {
                        Log($"Viewport resize failed for width {width}: {error.Message}", "debug");
                    }

                    WaitForResizeCount(page, resizeCount, width);
                    lastWindowWidth = width;
                    lastWindowHeight = height;
                }

                if (ResponsiveCaptureReloadPage)
                {
                    try
                    {
                        // ReloadAsync has no sync equivalent; block with .GetAwaiter().GetResult() to ensure page is fully loaded before capturing DOM
                        var reloadTask = page.ReloadAsync();
                        reloadTask.ConfigureAwait(false).GetAwaiter().GetResult();

                        if (!PercyPlaywrightDriver.EvaluateSync<bool>(page, "!!window.PercyDOM"))
                        {
                            PercyPlaywrightDriver.EvaluateSync<string>(page, GetPercyDOM());
                        }

                        PercyPlaywrightDriver.EvaluateSync<object>(page, "PercyDOM.waitForResize()");
                        resizeCount = 0;
                    }
                    catch (Exception error)
                    {
                        Log($"Page reload failed during responsive capture for width {width}: {error.Message}", "debug");
                    }
                }

                if (Int32.TryParse(ResponsiveCaptureSleepTime, out sleepTime))
                {
                    if (sleepTime > 0)
                    {
                        Thread.Sleep(TimeSpan.FromSeconds(sleepTime));
                    }
                }

                var domSnapshot = GetSerializedDom(page, options, cookiesJson, width);
                domSnapshots.Add(domSnapshot);
            }

            try
            {
                bool resetChangesViewport = lastWindowWidth != currentWidth || lastWindowHeight != currentHeight;
                PercyPlaywrightDriver.SetViewportSizeSync(page, currentWidth, currentHeight);
                if (resetChangesViewport)
                {
                    WaitForResizeCount(page, resizeCount + 1, currentWidth);
                }
            }
            catch (Exception error)
            {
                Log($"Viewport reset failed: {error.Message}", "debug");
            }

            return domSnapshots;
        }

        private static Dictionary<string, object> MergeSnapshotOptions(Dictionary<string, object>? options)
        {
            var merged = new Dictionary<string, object>();
            if (cliConfig is JsonElement configElement &&
                configElement.ValueKind == JsonValueKind.Object &&
                configElement.TryGetProperty("snapshot", out JsonElement snapshotElement) &&
                snapshotElement.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in snapshotElement.EnumerateObject())
                {
                    // Recursively convert config values so nested JSON
                    // objects become Dictionary<string, object> and can be
                    // deep-merged with per-call options below.
                    var converted = JsonElementToObjectDeep(prop.Value);
                    if (converted != null)
                        merged[prop.Name] = converted;
                }
            }
            if (options != null)
            {
                // Deep-merge: nested objects merge recursively (per-call wins at
                // leaves), arrays/scalars replace. Matches the JS sdk-utils fix.
                merged = DeepMerge(merged, options);
            }
            return merged;
        }

        // Recursively converts a JSON element into plain CLR objects:
        // Object -> Dictionary<string, object> (recursive),
        // Array  -> List<object> (recursive),
        // primitives -> bool / int / double / string.
        private static object? JsonElementToObjectDeep(JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    var dict = new Dictionary<string, object>();
                    foreach (JsonProperty prop in el.EnumerateObject())
                    {
                        var converted = JsonElementToObjectDeep(prop.Value);
                        if (converted != null)
                            dict[prop.Name] = converted;
                    }
                    return dict;
                case JsonValueKind.Array:
                    var items = el.EnumerateArray().ToList();
                    // All-integer arrays (e.g. config `widths`) must stay List<int> so
                    // ParseWidthsFromOptions' IEnumerable<int> path recognizes them.
                    if (items.Count > 0 && items.All(i => i.ValueKind == JsonValueKind.Number && i.TryGetInt32(out _)))
                        return items.Select(i => i.GetInt32()).ToList();
                    var list = new List<object>();
                    foreach (JsonElement item in items)
                    {
                        var converted = JsonElementToObjectDeep(item);
                        if (converted != null)
                            list.Add(converted);
                    }
                    return list;
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Number:
                    return el.TryGetInt32(out int intVal) ? (object)intVal : el.GetDouble();
                case JsonValueKind.String:
                    return el.GetString();
                default:
                    return null;
            }
        }

        // Deep-merges override into a copy of baseDict: when a key exists in both
        // and both values are Dictionary<string, object>, recurse; otherwise the
        // override value wins (arrays and scalars replace).
        private static Dictionary<string, object> DeepMerge(
            Dictionary<string, object> baseDict, Dictionary<string, object> overrideDict)
        {
            var result = new Dictionary<string, object>(baseDict);
            foreach (var kvp in overrideDict)
            {
                if (result.TryGetValue(kvp.Key, out var existing) &&
                    existing is Dictionary<string, object> existingDict &&
                    kvp.Value is Dictionary<string, object> overrideNested)
                {
                    result[kvp.Key] = DeepMerge(existingDict, overrideNested);
                }
                else
                {
                    result[kvp.Key] = kvp.Value;
                }
            }
            return result;
        }

        private static bool IsResponsiveSnapshotCapture(Dictionary<string, object>? options)
        {
            if (cliConfig is JsonElement configElement)
            {
                if (configElement.ValueKind == JsonValueKind.Object &&
                    configElement.TryGetProperty("percy", out JsonElement percyElement) &&
                    percyElement.ValueKind == JsonValueKind.Object &&
                    percyElement.TryGetProperty("deferUploads", out JsonElement deferUploadsProperty) &&
                    (deferUploadsProperty.ValueKind == JsonValueKind.True || deferUploadsProperty.ValueKind == JsonValueKind.False))
                {
                    if (deferUploadsProperty.GetBoolean())
                    {
                        return false;
                    }
                }

                if (options != null &&
                    options.TryGetValue("responsiveSnapshotCapture", out var responsiveOption) &&
                    responsiveOption is bool responsiveFromOptions &&
                    responsiveFromOptions)
                {
                    return true;
                }

                if (configElement.ValueKind == JsonValueKind.Object &&
                    configElement.TryGetProperty("snapshot", out JsonElement snapshotElement) &&
                    snapshotElement.ValueKind == JsonValueKind.Object &&
                    snapshotElement.TryGetProperty("responsiveSnapshotCapture", out JsonElement responsiveProperty) &&
                    (responsiveProperty.ValueKind == JsonValueKind.True || responsiveProperty.ValueKind == JsonValueKind.False))
                {
                    return responsiveProperty.GetBoolean();
                }

                return false;
            }

            return options != null &&
                   options.TryGetValue("responsiveSnapshotCapture", out var responsiveOptionNoConfig) &&
                   responsiveOptionNoConfig is bool responsiveFromOptionsNoConfig &&
                   responsiveFromOptionsNoConfig;
        }

        // To take percy snapshot
        public static JObject? Snapshot(
            IPage page, string name,
            IEnumerable<KeyValuePair<string, object>>? options = null)
        {
            if (!Enabled()) return null;
            if (sessionType == "automate")
                throw new Exception("Invalid function call - Snapshot(). Please use Screenshot() function while using Percy with Automate. For more information on usage of Screenshot, refer https://www.browserstack.com/docs/percy/integrate/functional-and-visual");

            try
            {
                if (PercyPlaywrightDriver.EvaluateSync<bool>(page, "!!window.PercyDOM") == false)
                    PercyPlaywrightDriver.EvaluateSync<string>(page, GetPercyDOM());

                // Expose closed shadow roots via CDP before serialization
                ExposeClosedShadowRoots(page);

                // Convert IEnumerable to Dictionary for proper JSON serialization
                var optionsDict = options?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

                // Merge .percy.yml config options with snapshot options (snapshot options take priority)
                var mergedOptions = MergeSnapshotOptions(optionsDict);

                // CookiesAsync has no sync equivalent; block with .GetAwaiter().GetResult() to fetch cookies before serializing the DOM
                var cookies = page.Context.CookiesAsync().ConfigureAwait(false).GetAwaiter().GetResult();
                string cookiesJson = JsonSerializer.Serialize(cookies);
                object domSnapshot = IsResponsiveSnapshotCapture(mergedOptions)
                    ? CaptureResponsiveDom(page, mergedOptions, cookiesJson)
                    : GetSerializedDom(page, mergedOptions, cookiesJson);

                Options snapshotOptions = new Options {
                    { "clientInfo", CLIENT_INFO },
                    { "environmentInfo", ENVIRONMENT_INFO },
                    { "domSnapshot", domSnapshot },
                    { "url", page.Url },
                    { "name", name }
                };

                if (options != null)
                    foreach (KeyValuePair<string, object> o in options)
                        snapshotOptions.Add(o.Key, o.Value);

                dynamic res = Request("/percy/snapshot", snapshotOptions);
                dynamic data = JsonSerializer.Deserialize<object>(res.content);

                if (data.GetProperty("success").GetBoolean() != true)
                    throw new Exception(data.GetProperty("error").GetString());
                if (data.TryGetProperty("data", out JsonElement results))
                {
                    return JObject.Parse(results.GetRawText());
                }
                return null;
            }
            catch (Exception error)
            {
                Log($"Could not take DOM snapshot \"{name}\"");
                Log(error);
                return null;
            }
        }

        public static JObject? Screenshot(IPage page, string name, IEnumerable<KeyValuePair<string, object>>? options = null)
        {
            PercyPlaywrightDriver percyPage = new PercyPlaywrightDriver(page);
            return Screenshot(percyPage, name, options);
        }

        // To take percy screenshot
        public static JObject? Screenshot(
            IPercyPlaywrightDriver percyPage, string name,
            IEnumerable<KeyValuePair<string, object>>? options = null)
        {
            if (!Enabled()) return null;
            if (sessionType != "automate")
                throw new Exception("Invalid function call - Screenshot(). Please use Snapshot() function for taking screenshot. Screenshot() should be used only while using Percy with Automate. For more information on usage of PercySnapshot(), refer doc for your language https://www.browserstack.com/docs/percy/integrate/overview");
            try
            {
                Options screenshotOptions = new Options {
                    { "sessionId", percyPage.GetSessionId()},
                    { "frameGuid", percyPage.GetFrameGUID()},
                    { "pageGuid", percyPage.GetPageGUID()},
                    { "framework", "playwright"},
                    { "clientInfo", CLIENT_INFO },
                    { "environmentInfo", ENVIRONMENT_INFO },
                    { "url", percyPage.GetUrl() },
                    { "snapshotName", name }
                };

                if (options != null)
                {
                    // Convert IEnumerable to Dictionary for proper JSON serialization
                    var optionsDict = options.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                    screenshotOptions.Add("options", optionsDict);
                }

                dynamic res = Request("/percy/automateScreenshot", JObject.FromObject(screenshotOptions), true);
                dynamic data = JsonSerializer.Deserialize<object>(res.content);
                if (data.GetProperty("success").GetBoolean() != true)
                    throw new Exception(data.GetProperty("error").GetString());

                if (data.TryGetProperty("data", out JsonElement results))
                {
                    return JObject.Parse(results.GetRawText());
                }
                return null;
            }
            catch (Exception error)
            {
                Log($"Could not take Percy Screenshot \"{name}\"");
                Log(error);
                return null;
            }
        }

        public static JObject? Snapshot(IPage page, string name, object opts)
        {
            Options options = new Options();

            foreach (PropertyDescriptor prop in TypeDescriptor.GetProperties(opts))
                options.Add(prop.Name, prop.GetValue(opts));

            return Snapshot(page, name, options);
        }

        public static JObject? Screenshot(IPage page, string name, object opts)
        {
            Options options = new Options();

            foreach (PropertyDescriptor prop in TypeDescriptor.GetProperties(opts))
                options.Add(prop.Name, prop.GetValue(opts));

            return Screenshot(page, name, options);
        }

        public static void ResetInternalCaches()
        {
            _enabled = null;
            _dom = null;
        }
    }

    [AttributeUsage(AttributeTargets.Assembly, Inherited = false, AllowMultiple = false)]
    sealed class ClientInfoAttribute : Attribute
    {
        public string ClientInfo { get; }
        public ClientInfoAttribute(string info)
        {
            this.ClientInfo = info;
        }
    }
}
