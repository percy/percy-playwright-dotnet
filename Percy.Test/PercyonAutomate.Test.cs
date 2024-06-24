using Moq;
using Xunit;
using System.Collections.Generic;
using Microsoft.Playwright; 
using Newtonsoft.Json;
using RichardSzalay.MockHttp;
using System.Net.Http;
using System;
using Newtonsoft.Json.Linq;

namespace PercyIO.Playwright.Tests
{
  public class PercyDriverTest
  {
        private readonly Mock<IPercyPlaywrightDriver> percyPageMock;

        public PercyDriverTest() {
            percyPageMock = new Mock<IPercyPlaywrightDriver>();
            string url = "http://hub-cloud.browserstack.com/wd/hub/";
            percyPageMock.Setup(x => x.GetPageGUID()).Returns("page_123");
            percyPageMock.Setup(x => x.GetFrameGUID()).Returns("frame_123");
            percyPageMock.Setup(x => x.GetSessionId()).Returns("session_123");
            percyPageMock.Setup(x => x.GetUrl()).Returns(url);
        }

        [Fact]
        public void postScreenshot()
        {
            Func<bool> oldEnabledFn = Percy.Enabled;
            Percy.Enabled = () => true;
            Percy.setSessionType("automate");

            var mockHttp = new MockHttpMessageHandler();
            var obj = new
            {
                success = true,
                version = "1.0",
            };
            mockHttp.Fallback.Respond(new HttpClient());
            mockHttp.Expect(HttpMethod.Post, "http://localhost:5338/percy/automateScreenshot")
                .WithPartialContent("Screenshot 1")
                .WithPartialContent("page_123")
                .WithPartialContent("frame_123")
                .WithPartialContent("session_123")
                .WithPartialContent("http://hub-cloud.browserstack.com/wd/hub/")
                .WithPartialContent("playwright")
                .Respond("application/json", JsonConvert.SerializeObject(obj));
            Percy.setHttpClient(new HttpClient(mockHttp));
            Percy.Screenshot(percyPageMock.Object, "Screenshot 1");
            percyPageMock.Verify(b => b.GetPageGUID(), Times.Once);
            percyPageMock.Verify(b => b.GetFrameGUID(), Times.Once);
            percyPageMock.Verify(b => b.GetSessionId(), Times.Once);
            mockHttp.VerifyNoOutstandingExpectation();
            Percy.Enabled = oldEnabledFn;
        }

        [Fact]
        public void postScreenshotWithSync()
        {
            Func<bool> oldEnabledFn = Percy.Enabled;
            Percy.Enabled = () => true;
            Percy.setSessionType("automate");

            var syncData = JObject.Parse("{'name': 'snapshot'}");
            var mockHttp = new MockHttpMessageHandler();
            var obj = new
            {
                success = true,
                version = "1.0",
                data = syncData
            };
            mockHttp.Fallback.Respond(new HttpClient());
            mockHttp.Expect(HttpMethod.Post, "http://localhost:5338/percy/automateScreenshot")
                .WithPartialContent("Screenshot 1")
                .WithPartialContent("sync")
                .Respond("application/json", JsonConvert.SerializeObject(obj));
            Percy.setHttpClient(new HttpClient(mockHttp));
            Dictionary<string, object> options = new Dictionary<string, object>();
            options["sync"] = true;
            Assert.Equal(Percy.Screenshot(percyPageMock.Object, "Screenshot 1", options), syncData);
            percyPageMock.Verify(b => b.GetPageGUID(), Times.Once);
            percyPageMock.Verify(b => b.GetFrameGUID(), Times.Once);
            percyPageMock.Verify(b => b.GetSessionId(), Times.Once);
            mockHttp.VerifyNoOutstandingExpectation();
            Percy.Enabled = oldEnabledFn;
        }

        [Fact]
        public void postScreenshotWithOptions()
        {
            // Since mockHttp doesn't have functionality to mock response header,
            // Which is causing version check to break
            // Overiding function to return true and set Session Type
            Func<bool> oldEnabledFn = Percy.Enabled;
            Percy.Enabled = () => true;
            Percy.setSessionType("automate");
            var mockHttp = new MockHttpMessageHandler();
            var obj = new
            {
                success = true,
                version = "1.0",
            };
            mockHttp.Fallback.Respond(new HttpClient());
            mockHttp.Expect(HttpMethod.Post, "http://localhost:5338/percy/automateScreenshot")
                .WithPartialContent("Screenshot 2")
                .WithPartialContent("fullpage")
                .Respond("application/json", JsonConvert.SerializeObject(obj));
            Percy.setHttpClient(new HttpClient(mockHttp));
            Dictionary<string, object> options = new Dictionary<string, object>();
            options["fullpage"] = true;
            Percy.Screenshot(percyPageMock.Object, "Screenshot 2", options);
            percyPageMock.Verify(b => b.GetPageGUID(), Times.Once);
            percyPageMock.Verify(b => b.GetFrameGUID(), Times.Once);
            percyPageMock.Verify(b => b.GetSessionId(), Times.Once);
            mockHttp.VerifyNoOutstandingExpectation();
            Percy.Enabled = oldEnabledFn;
        }

        [Fact]
        public void postScreenshotThrowExceptionWithWeb()
        {
            Func<bool> oldEnabledFn = Percy.Enabled;
            Percy.Enabled = () => true;
            Percy.setSessionType("web");
            try {
                Percy.Screenshot(percyPageMock.Object, "Screenshot 1");
                Assert.Fail("Exception not raised");
            } catch (Exception error) {
                Assert.Equal("Invalid function call - Screenshot(). Please use Snapshot() function for taking screenshot. Screenshot() should be used only while using Percy with Automate. For more information on usage of PercySnapshot(), refer doc for your language https://www.browserstack.com/docs/percy/integrate/overview", error.Message);
            }
            Percy.Enabled = oldEnabledFn;
        }
    }
}
