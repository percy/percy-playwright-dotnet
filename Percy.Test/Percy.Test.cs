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
using Microsoft.Playwright;


namespace PercyIO.Playwright.Tests
{
    public class TestsFixture : IAsyncDisposable
    {
        private IPlaywright playwright;
        private IBrowser browser;
        public IPage Page { get; private set; }

        public TestsFixture ()
        {
            // The actual initialization will be done in InitializeAsync
        }

        public async Task InitializeAsync()
        {
            playwright = await Microsoft.Playwright.Playwright.CreateAsync();
            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = false });
            Page = await browser.NewPageAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Page.CloseAsync();
            await browser.CloseAsync();
            playwright.Dispose();
        }
    }

    public class UnitTests : IAsyncLifetime
    {
        private readonly TestsFixture _fixture;
        private readonly StringWriter _stdout;

        public UnitTests()
        {
            _stdout = new StringWriter();
            Console.SetOut(_stdout);

            _fixture = new TestsFixture();

            Percy.ResetInternalCaches();
            Request("/test/api/reset");
        }

        public async Task InitializeAsync()
        {
            await _fixture.InitializeAsync();
            await _fixture.Page.GotoAsync($"{Percy.CLI_API}/test/snapshot");
        }

        public Task DisposeAsync()
        {
            return _fixture.DisposeAsync().AsTask();
        }


        public string Stdout()
        {
            return Regex.Replace(_stdout.ToString(), @"\e\[(\d+;)*(\d+)?[ABCDHJKfmsu]", "");
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

            Assert.Equal("[percy] Percy is not running, disabling snapshots\n", Stdout());
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
                "https://docs.percy.io/docs/migrating-to-percy-cli\n",
                Stdout()
            );
        }

        [Fact]
        public void DisablesSnapshotsWhenHealthcheckVersionIsUnsupported()
        {
            Request("/test/api/version", "0.0.1");

            Percy.Snapshot(_fixture.Page, "Snapshot 1");
            Percy.Snapshot(_fixture.Page, "Snapshot 2");

            Assert.Equal("[percy] Unsupported Percy CLI version, 0.0.1\n", Stdout());
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
            List<string> logs = new List<string>();

            foreach (JsonElement log in data.GetProperty("logs").EnumerateArray())
            {
                string? msg = log.GetProperty("message").GetString();
                if (msg != null) logs.Add(msg);
            }

            List<string> expected = new List<string> {
                "---------",
                "Received snapshot: Snapshot 1",
                "- url: http://localhost:5338/test/snapshot",
                "- widths: 375px, 1280px",
                "- minHeight: 1024px",
                "- enableJavaScript: false",
                "- cliEnableJavaScript: true",
                "- disableShadowDOM: false",
                "- discovery.allowedHostnames: localhost",
                "- discovery.captureMockedServiceWorker: false",
                $"- clientInfo: {Percy.CLIENT_INFO}",
                $"- environmentInfo: {Percy.ENVIRONMENT_INFO}",
                "- domSnapshot: true",
                "Snapshot found: Snapshot 1",
                "---------",
                "Received snapshot: Snapshot 2",
                "- url: http://localhost:5338/test/snapshot",
                "- widths: 375px, 1280px",
                "- minHeight: 1024px",
                "- enableJavaScript: true",
                "- cliEnableJavaScript: true",
                "- disableShadowDOM: false",
                "- discovery.allowedHostnames: localhost",
                "- discovery.captureMockedServiceWorker: false",
                $"- clientInfo: {Percy.CLIENT_INFO}",
                $"- environmentInfo: {Percy.ENVIRONMENT_INFO}",
                "- domSnapshot: true",
                "Snapshot found: Snapshot 2",
                "---------",
                "Received snapshot: Snapshot 3",
                "- url: http://localhost:5338/test/snapshot",
                "- widths: 375px, 1280px",
                "- minHeight: 1024px",
                "- enableJavaScript: true",
                "- cliEnableJavaScript: true",
                "- disableShadowDOM: false",
                "- discovery.allowedHostnames: localhost",
                "- discovery.captureMockedServiceWorker: false",
                $"- clientInfo: {Percy.CLIENT_INFO}",
                $"- environmentInfo: {Percy.ENVIRONMENT_INFO}",
                "- domSnapshot: true",
                "Snapshot found: Snapshot 3",
            };

            foreach (int i in expected.Select((v, i) => i))
                Assert.Equal(expected[i], logs[i]);
        }

        [Fact]
        public void PostsSnapshotWithSync()
        {
            Percy.Snapshot(_fixture.Page, "Snapshot 1", new {
                    sync = true
                });

            JsonElement data = Request("/test/logs");
            List<string> logs = new List<string>();

            foreach (JsonElement log in data.GetProperty("logs").EnumerateArray())
            {
                string? msg = log.GetProperty("message").GetString();
                if (msg != null) logs.Add(msg);
            }
            List<string> expected = new List<string> {
                "---------",
                "Received snapshot: Snapshot 1",
                "- url: http://localhost:5338/test/snapshot",
                "- widths: 375px, 1280px",
                "- minHeight: 1024px",
                "- enableJavaScript: false",
                "- cliEnableJavaScript: true",
                "- disableShadowDOM: false",
                "- discovery.allowedHostnames: localhost",
                "- discovery.captureMockedServiceWorker: false",
                $"- clientInfo: {Percy.CLIENT_INFO}",
                $"- environmentInfo: {Percy.ENVIRONMENT_INFO}",
                "- domSnapshot: true",
                "The Synchronous CLI functionality is not compatible with skipUploads option.",
                "Snapshot found: Snapshot 1",
            };

            foreach (int i in expected.Select((v, i) => i))
                Assert.Equal(expected[i], logs[i]);
        }

        [Fact]
        public void HandlesExceptionsDuringSnapshot()
        {
            Request("/test/api/error", "/percy/snapshot");

            Percy.Snapshot(_fixture.Page, "Snapshot 1");

            Assert.Contains(
                "[percy] Could not take DOM snapshot \"Snapshot 1\"\n" +
                "[percy] System.Net.Http.HttpRequestException:",
                Stdout()
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
                Assert.Equal("Invalid function call - Snapshot(). Please use Screenshot() function while using Percy with Automate. For more information on usage of Screenshot, refer https://docs.percy.io/docs/integrate-functional-testing-with-visual-testing", error.Message);
            }
            Percy.Enabled = oldEnabledFn;
        }
    }
}
