using Microsoft.Playwright;
using System;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;


namespace PercyIO.Playwright
{
    public class SessionDetails
    {
        public string hashed_id {get; set;}
        public string frameGuid {get; set;}
        public string pageGuid {get; set;}
    }

    public static class Utils
    {
        public static Cache<string, object> cache = new Cache<string, object>();

        public static SessionDetails GetSessionDetails(IPage page) {
            SessionDetails sessionDetails = new SessionDetails();
            sessionDetails.hashed_id = GetSessionId(page);
            sessionDetails.frameGuid = GetFrameGUID(page);
            sessionDetails.pageGuid = GetPageGUID(page);
            return sessionDetails;
        }

        private static string GetPageGUID(IPage page) {
            Type pageImplType = page.GetType().BaseType;
            FieldInfo guidField = pageImplType.GetField("<Guid>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
            string pageGUID = (string)guidField.GetValue(page);
            return pageGUID;
        }

        private static string GetFrameGUID(IPage page) {
            Type frameImplType = page.MainFrame.GetType().BaseType;
            FieldInfo frameGuidField = frameImplType.GetField("<Guid>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
            string frame_guid = (string)frameGuidField.GetValue(page.MainFrame);
            return frame_guid;
        }
        public static string GetSessionId(IPage page)
        {
            // It is browser's guid maintained by playwright, considering it is unique for one automate session
            // will use it to cache the session details
            string browserId = GetBrowserGuid(page);
            if (cache.Get(browserId) == null)
            {
                string sessionDetailsJson = EvaluateSync<string>(page, "_ => {}", "browserstack_executor: {\"action\":\"getSessionDetails\"}");
                var sessionDetails = JsonSerializer.Deserialize<JsonElement>(sessionDetailsJson);
                sessionDetails.TryGetProperty("hashed_id", out JsonElement hashedIdElement);
                cache.Store(browserId, hashedIdElement.GetString());
            }
            return (string)cache.Get(browserId);
        }

        public static string GetBrowserGuid(IPage page)
        {
            Type browserImplType = page.Context.Browser.GetType().BaseType;
            FieldInfo guidField = browserImplType.GetField("<Guid>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
            string guid = (string)guidField.GetValue(page.Context.Browser);
            return guid;
        }

        public static T EvaluateSync<T>(IPage page, string script, string arguments = null) {
            Task<T> scriptTask = page.EvaluateAsync<T>(script, arguments);
            scriptTask.Wait();
            return scriptTask.Result;
        }
    }
}
