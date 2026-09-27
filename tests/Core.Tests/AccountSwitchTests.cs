using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;
using CodexBalanceWidget.Core;

namespace CodexBalanceWidget.Core.Tests
{
    internal static class AccountSwitchTests
    {
        public static int RunFakeServer()
        {
            var root = Environment.GetEnvironmentVariable("CODEX_HOME");
            // Deliberately cache the account at startup, like a long-lived server.
            var state = File.ReadAllText(Path.Combine(root, "account-state.txt"));
            var serializer = new JavaScriptSerializer();
            string line;
            while ((line = Console.ReadLine()) != null)
            {
                var message = serializer.Deserialize<Dictionary<string, object>>(line);
                if (!message.ContainsKey("id")) { continue; }
                var method = (string)message["method"];
                object result = new Dictionary<string, object>();
                if (method == "account/read")
                {
                    var parameters = (Dictionary<string, object>)message["params"];
                    if ((bool)parameters["refreshToken"]) { return 2; }
                    Console.WriteLine(serializer.Serialize(new {
                        method = "account/updated", @params = new {
                            authMode = state == "logout" ? null : "chatgpt", planType = "plus" } }));
                    result = new { account = state == "logout" ? null : new { type = "chatgpt" } };
                }
                else if (method == "account/rateLimits/read")
                {
                    if (state == "delay")
                    {
                        File.WriteAllText(Path.Combine(root, "pending.txt"), "pending");
                        Thread.Sleep(10000);
                    }
                    result = new { rateLimits = new { primary = new {
                        usedPercent = state == "delay" ? 99 : int.Parse(state),
                        windowDurationMins = 10080 } } };
                }
                Console.WriteLine(serializer.Serialize(new { id = message["id"], result = result }));
                Console.Out.Flush();
            }
            return 0;
        }

        public static void Run()
        {
            var previousHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            var root = Path.Combine(Path.GetTempPath(), "widget-account-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var gate = new object();
            RateLimitSnapshot latest = null;
            var invalidOldResponse = false;
            try
            {
                Environment.SetEnvironmentVariable("CODEX_HOME", root);
                SetAccount(root, "20");
                using (var source = new CodexAppServerRateLimitSource(Assembly.GetExecutingAssembly().Location, null))
                {
                    source.SnapshotUpdated += delegate(object sender, RateLimitSnapshotEventArgs e)
                    {
                        lock (gate)
                        {
                            latest = e.Snapshot;
                            if (latest.FindLongWindow() != null && latest.FindLongWindow().RemainingPercent == 1)
                            { invalidOldResponse = true; }
                        }
                    };
                    Func<double, bool> remaining = delegate(double expected)
                    {
                        lock (gate) { return latest != null && !latest.IsStale && latest.FindLongWindow() != null &&
                            latest.FindLongWindow().RemainingPercent == expected; }
                    };
                    source.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
                    WaitUntil(delegate { return remaining(80); }, "initial account");
                    source.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
                    source.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
                    if (!remaining(80)) { throw new Exception("Unchanged account notifications invalidated the quota."); }
                    SetAccount(root, "40");
                    WaitUntil(delegate { return remaining(60); }, "switch without stopping Codex");
                    SetAccount(root, "logout");
                    File.Delete(Path.Combine(root, "auth.json"));
                    WaitUntil(delegate { lock (gate) { return latest != null && latest.IsStale &&
                        latest.Windows.Count == 0 && latest.Credits.Count == 0 &&
                        latest.StatusMessage.Contains("登录"); } }, "logout clears previous data");
                    SetAccount(root, "delay");
                    WaitUntil(delegate { return File.Exists(Path.Combine(root, "pending.txt")); }, "in-flight old request");
                    SetAccount(root, "65");
                    WaitUntil(delegate { return remaining(35); }, "new account replaces in-flight request");
                    lock (gate) { if (invalidOldResponse) { throw new Exception("Old account response leaked."); } }
                    source.StopAsync().GetAwaiter().GetResult();
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("CODEX_HOME", previousHome);
                Directory.Delete(root, true);
            }
        }

        private static void SetAccount(string root, string state)
        {
            File.WriteAllText(Path.Combine(root, "account-state.txt"), state);
            // Synthetic non-secret fixture; the production code only sees metadata.
            File.WriteAllText(Path.Combine(root, "auth.json"), Guid.NewGuid().ToString("N"));
        }

        private static void WaitUntil(Func<bool> condition, string description)
        {
            var timer = Stopwatch.StartNew();
            while (!condition())
            {
                if (timer.Elapsed > TimeSpan.FromSeconds(12)) { throw new Exception("Timed out: " + description); }
                Thread.Sleep(25);
            }
        }
    }
}
