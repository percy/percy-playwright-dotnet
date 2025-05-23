﻿using System;
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

        private static void Log<T>(T message)
        {
            string label = DEBUG ? "percy:dotnet" : "percy";
            Console.WriteLine($"[\u001b[35m{label}\u001b[39m] {message}");
        }

        private static HttpClient? _http;

        private static string? sessionType = null;

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
                    data.TryGetProperty("type", out JsonElement type);
                    setSessionType(type.ToString());
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

                string opts = JsonSerializer.Serialize(options);
                var domSnapshot = PercyPlaywrightDriver.EvaluateSync<object>(page, $"PercyDOM.serialize({opts})");

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
                    screenshotOptions.Add("options", options);

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
