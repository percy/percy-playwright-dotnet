using System;
using Microsoft.Playwright;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Percy.Test")]

namespace PercyIO.Playwright
{
    internal class PercyPlaywrightDriver : IPercyPlaywrightDriver
    {
        protected IPage page;
        private static Cache<string, object> cache = new Cache<string, object>();

        internal PercyPlaywrightDriver(IPage page)
        {
            this.page = page;
        }

        public string GetUrl() {
            return this.page.Url;
        }

        public string GetPageGUID() {
            object pageImpl = GetPageGuidSource();
            Type pageImplType = pageImpl.GetType().BaseType;
            FieldInfo guidField = pageImplType.GetField("<Guid>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
            string pageGUID = (string)guidField.GetValue(pageImpl);
            return pageGUID;
        }

        public string GetFrameGUID() {
            object frameImpl = GetFrameGuidSource();
            Type frameImplType = frameImpl.GetType().BaseType;
            FieldInfo frameGuidField = frameImplType.GetField("<Guid>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
            string frameGuid = (string)frameGuidField.GetValue(frameImpl);
            return frameGuid;
        }

        // Behavior-preserving seams: the GUID reflection above reads the internal
        // <Guid>k__BackingField off the object returned here. In production these
        // return exactly this.page / this.page.MainFrame (the Playwright impl types),
        // so the reflected type, field lookup, value read and return are identical to
        // before. Marked protected virtual so a test subclass can supply a stand-in
        // object that exposes the same backing field, exercising the reflection/parse
        // lines without a live Chromium page.
        protected virtual object GetPageGuidSource() => this.page;
        protected virtual object GetFrameGuidSource() => this.page.MainFrame;
        public string GetSessionId()
        {
            // It is browser's guid maintained by playwright, considering it is unique for one automate session
            // will use it to cache the session details
            string browserGuid = GetBrowserGuid();
            if (cache.Get(browserGuid) == null)
            {
                string sessionDetailsJson = FetchSessionDetails();
                var sessionDetails = JsonSerializer.Deserialize<JsonElement>(sessionDetailsJson);
                sessionDetails.TryGetProperty("hashed_id", out JsonElement hashedIdElement);
                cache.Store(browserGuid, hashedIdElement.GetString());
            }
            return (string)cache.Get(browserGuid);
        }

        // Executes the BrowserStack Automate "getSessionDetails" executor over the
        // live CDP/WebSocket bridge. Extracted into a protected virtual method so the
        // session-id caching/parsing logic in GetSessionId can be exercised without a
        // real Automate session. Production behavior is unchanged: GetSessionId still
        // calls EvaluateSync against the page exactly as before.
        protected virtual string FetchSessionDetails()
        {
            return PercyPlaywrightDriver.EvaluateSync<string>(this.page, "_ => {}", "browserstack_executor: {\"action\":\"getSessionDetails\"}");
        }

        // Reflects into Playwright's internal browser implementation to read its Guid.
        // Marked protected virtual so tests can supply a deterministic guid without a
        // live browser; production callers reach the same reflection path as before.
        protected virtual string GetBrowserGuid()
        {
            object browserImpl = GetBrowserGuidSource();
            Type browserImplType = browserImpl.GetType().BaseType;
            FieldInfo guidField = browserImplType.GetField("<Guid>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
            string browserGuid = (string)guidField.GetValue(browserImpl);
            return browserGuid;
        }

        // Behavior-preserving seam: in production this returns exactly
        // this.page.Context.Browser, so GetBrowserGuid reflects over the same
        // Playwright impl type as before. A test subclass can override it to return a
        // stand-in object exposing the same <Guid>k__BackingField, covering the
        // browser-reflection lines without a live browser.
        protected virtual object GetBrowserGuidSource() => this.page.Context.Browser;

        public static T EvaluateSync<T>(IPage page, string script, string arguments = null) {
            Task<T> scriptTask = page.EvaluateAsync<T>(script, arguments);
            scriptTask.Wait();
            return scriptTask.Result;
        }

        // SetViewportSizeAsync has no sync equivalent in Playwright; this helper blocks synchronously
        // to ensure the viewport resize is complete before proceeding
        public static void SetViewportSizeSync(IPage page, int width, int height) {
            page.SetViewportSizeAsync(width, height).GetAwaiter().GetResult();
        }
    }
}
