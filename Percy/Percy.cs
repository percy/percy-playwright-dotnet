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
        public static readonly string CLI_API =
            Environment.GetEnvironmentVariable("PERCY_CLI_API") ?? "http://localhost:5338";
        public static readonly string CLIENT_INFO =
            typeof(Percy).Assembly.GetCustomAttribute<ClientInfoAttribute>().ClientInfo;
        public static readonly string ENVIRONMENT_INFO = Regex.Replace(
            Regex.Replace(RuntimeInformation.FrameworkDescription, @"\s+", "-"),
            @"-([\d\.]+).*$", "/$1").Trim().ToLower();

        private static void Log<T>(T message, string lvl = "info")
        {
            string label = DEBUG ? "percy:dotnet" : "percy";
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
                if (DEBUG)
                    Console.Error.WriteLine($"[{label}] Sending log to CLI failed: {e.Message}");
            }
            finally
            {
                // Write to stderr (Console.Error) rather than stdout for two reasons:
                // 1. stderr is unbuffered by default, so output appears immediately even inside catch/finally blocks.
                // 2. Tools like `npx percy exec` forward stderr directly to the terminal, whereas stdout can be
                //    swallowed or delayed when the process exits before the buffer is flushed.
                if (lvl != "debug" || DEBUG)
                {
                    Console.Error.WriteLine(plainMessage);
                }
            }
        }

        private static HttpClient? _http;

        private static string? sessionType = null;
        private static object? cliConfig;

        public static readonly string RESPONSIVE_CAPTURE_SLEEP_TIME =
            Environment.GetEnvironmentVariable("RESPONSIVE_CAPTURE_SLEEP_TIME");
        public static readonly bool PERCY_RESPONSIVE_CAPTURE_MIN_HEIGHT =
            (Environment.GetEnvironmentVariable("PERCY_RESPONSIVE_CAPTURE_MIN_HEIGHT") ?? "")
                .Equals("true", StringComparison.OrdinalIgnoreCase);
        public static readonly bool PERCY_RESPONSIVE_CAPTURE_RELOAD_PAGE =
            (Environment.GetEnvironmentVariable("PERCY_RESPONSIVE_CAPTURE_RELOAD_PAGE") ?? "")
                .Equals("true", StringComparison.OrdinalIgnoreCase);

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

        internal static HttpClient getHttpClient()
        {
            if (_http == null)
            {
                setHttpClient(new HttpClient());
                _http.Timeout = TimeSpan.FromMinutes(10);
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
                if (DEBUG) Log<Exception>(error);
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

        private static bool IsCrossOriginFrame(string frameUrl, string pageUrl)
        {
            if (frameUrl == "about:blank")
                return false;

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
                var injectTask = frame.EvaluateAsync(percyDomScript);
                injectTask.Wait();

                // enableJavaScript=True prevents the standard iframe serialization logic from running.
                // This is necessary because we're manually handling cross-origin iframe serialization here.
                var optionsForFrame = new Dictionary<string, object>(options ?? new Dictionary<string, object>())
                {
                    { "enableJavaScript", true }
                };

                // Serialize the frame
                string serializeScript = $"PercyDOM.serialize({JsonSerializer.Serialize(optionsForFrame)})";
                var serializeTask = frame.EvaluateAsync(serializeScript);
                serializeTask.Wait();
                var iframeSnapshot = serializeTask.Result;

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

                var getDataTask = page.EvaluateAsync(getDataScript, frameUrl);
                getDataTask.Wait();
                var iframeData = getDataTask.Result;

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

        private static object GetSerializedDom(IPage page, Dictionary<string, object>? options, string cookiesJson, int? width = null)
        {
            string opts = JsonSerializer.Serialize(options);
            string widthAssignment = width.HasValue ? $"dom.width = {width.Value};" : "";
            string script = $"(() => {{ const dom = PercyDOM.serialize({opts}); dom.cookies = {cookiesJson}; {widthAssignment} return dom; }})()";
            var domSnapshot = PercyPlaywrightDriver.EvaluateSync<object>(page, script);

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
                throw new Exception("Update Percy CLI to the latest version to use responsiveSnapshotCapture");
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
            if (!PERCY_RESPONSIVE_CAPTURE_MIN_HEIGHT)
            {
                return currentHeight;
            }

            try
            {
                int minHeight = currentHeight;

                if (options != null && options.TryGetValue("minHeight", out object? minHeightValue))
                {
                    if (minHeightValue is int intValue)
                    {
                        minHeight = intValue;
                    }
                    else if (minHeightValue is JsonElement element &&
                             element.ValueKind == JsonValueKind.Number &&
                             element.TryGetInt32(out int jsonInt))
                    {
                        minHeight = jsonInt;
                    }
                }

                return PercyPlaywrightDriver.EvaluateSync<int>(
                    page,
                    $"(() => window.outerHeight - window.innerHeight + {minHeight})()"
                );
            }
            catch
            {
                return currentHeight;
            }
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

        public static List<object> CaptureResponsiveDom(IPage page, Dictionary<string, object>? options, string cookiesJson)
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
            int resizeCount = 0;
            int sleepTime = 0;
            int defaultHeight = CalculateDefaultHeight(page, currentHeight, options);

            PercyPlaywrightDriver.EvaluateSync<object>(page, "PercyDOM.waitForResize()");

            foreach (ResponsiveWidth widthHeight in widthHeights)
            {
                int width = widthHeight.width;
                int height = widthHeight.height ?? defaultHeight;

                if (lastWindowWidth != width)
                {
                    resizeCount++;
                    try
                    {
                        var resizeTask = page.SetViewportSizeAsync(width, height);
                        resizeTask.Wait();
                    }
                    catch (Exception error)
                    {
                        Log($"Viewport resize failed for width {width}: {error.Message}", "debug");
                    }

                    WaitForResizeCount(page, resizeCount, width);
                    lastWindowWidth = width;
                }

                if (PERCY_RESPONSIVE_CAPTURE_RELOAD_PAGE)
                {
                    try
                    {
                        var reloadTask = page.ReloadAsync();
                        reloadTask.Wait();

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

                if (Int32.TryParse(RESPONSIVE_CAPTURE_SLEEP_TIME, out sleepTime))
                {
                    Thread.Sleep(sleepTime * 1000);
                }

                var domSnapshot = GetSerializedDom(page, options, cookiesJson, width);
                domSnapshots.Add(domSnapshot);
            }

            try
            {
                var resetTask = page.SetViewportSizeAsync(currentWidth, currentHeight);
                resetTask.Wait();
                WaitForResizeCount(page, resizeCount + 1, currentWidth);
            }
            catch (Exception error)
            {
                Log($"Viewport reset failed: {error.Message}", "debug");
            }

            return domSnapshots;
        }

        private static bool isResponsiveSnapshotCapture(Dictionary<string, object>? options)
        {
            if (cliConfig is JsonElement configElement)
            {
                if (configElement.GetProperty("percy").TryGetProperty("deferUploads", out JsonElement deferUploadsProperty))
                {
                    if (deferUploadsProperty.GetBoolean()) { return false; }
                }

                if (options != null && options.ContainsKey("responsiveSnapshotCapture") && (bool)options["responsiveSnapshotCapture"])
                {
                    return true;
                }

                return configElement.GetProperty("snapshot").GetProperty("responsiveSnapshotCapture").GetBoolean();
            }

            return options != null && options.ContainsKey("responsiveSnapshotCapture") && (bool)options["responsiveSnapshotCapture"];
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

                // Convert IEnumerable to Dictionary for proper JSON serialization
                var optionsDict = options?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                var cookiesTask = page.Context.CookiesAsync();
                cookiesTask.Wait();
                string cookiesJson = JsonSerializer.Serialize(cookiesTask.Result);
                object domSnapshot = isResponsiveSnapshotCapture(optionsDict)
                    ? CaptureResponsiveDom(page, optionsDict, cookiesJson)
                    : GetSerializedDom(page, optionsDict, cookiesJson);

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
