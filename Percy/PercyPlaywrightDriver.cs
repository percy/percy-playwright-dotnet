using System;
using Microsoft.Playwright;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace PercyIO.Playwright
{
    internal class PercyPlaywrightDriver : IPercyPlaywrightDriver
    {
        private IPage page;
        private static Cache<string, object> cache = new Cache<string, object>();

        internal PercyPlaywrightDriver(IPage page)
        {
            this.page = page;
        }

        public string GetUrl() {
            return this.page.Url;
        }

        public string GetPageGUID() {
            Type pageImplType = this.page.GetType().BaseType;
            FieldInfo guidField = pageImplType.GetField("<Guid>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
            string pageGUID = (string)guidField.GetValue(this.page);
            return pageGUID;
        }

        public string GetFrameGUID() {
            Type frameImplType = this.page.MainFrame.GetType().BaseType;
            FieldInfo frameGuidField = frameImplType.GetField("<Guid>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
            string frameGuid = (string)frameGuidField.GetValue(this.page.MainFrame);
            return frameGuid;
        }
        public string GetSessionId()
        {
            // It is browser's guid maintained by playwright, considering it is unique for one automate session
            // will use it to cache the session details
            string browserGuid = GetBrowserGuid();
            if (cache.Get(browserGuid) == null)
            {
                string sessionDetailsJson = PercyPlaywrightDriver.EvaluateSync<string>(this.page, "_ => {}", "browserstack_executor: {\"action\":\"getSessionDetails\"}");
                var sessionDetails = JsonSerializer.Deserialize<JsonElement>(sessionDetailsJson);
                sessionDetails.TryGetProperty("hashed_id", out JsonElement hashedIdElement);
                cache.Store(browserGuid, hashedIdElement.GetString());
            }
            return (string)cache.Get(browserGuid);
        }

        private string GetBrowserGuid()
        {
            Type browserImplType = this.page.Context.Browser.GetType().BaseType;
            FieldInfo guidField = browserImplType.GetField("<Guid>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
            string browserGuid = (string)guidField.GetValue(this.page.Context.Browser);
            return browserGuid;
        }

        public static T EvaluateSync<T>(IPage page, string script, string arguments = null) {
            Task<T> scriptTask = page.EvaluateAsync<T>(script, arguments);
            scriptTask.Wait();
            return scriptTask.Result;
        }
    }
}
