using System;
using System.IO;
using System.Text;
using CodexBalanceWidget.Core;

namespace CodexBalanceWidget.Core.Tests
{
    internal static class Program
    {
        private static int _failed;

        private static int Main()
        {
            Run("window classification and clamping", TestWindowClassification);
            Run("optional window data", TestOptionalWindows);
            Run("reset-credit detail sorting", TestCreditDetailsAndSorting);
            Run("count-only reset credits", TestCountOnlyCredits);
            Run("empty credit details are disclosed", TestEmptyCreditDetails);
            Run("final-day countdown", TestCountdown);
            Run("MSIX path validation", TestPackagePathValidation);
            Run("malformed JSON rejection", TestMalformedJson);
            Run("oversized JSON rejection", TestOversizedJson);
            Run("bounded reset-credit payload", TestBoundedCreditPayload);

            if (_failed == 0)
            {
                Console.WriteLine("All core tests passed.");
                return 0;
            }

            Console.Error.WriteLine(
                _failed + " core test(s) failed.");
            return 1;
        }

        private static void TestWindowClassification()
        {
            const string json =
                "{\"id\":7,\"result\":{\"rateLimits\":{" +
                "\"primary\":{\"usedPercent\":-5,\"windowDurationMins\":10080," +
                "\"resetsAt\":1784246400}," +
                "\"secondary\":{\"usedPercent\":125,\"windowDurationMins\":300," +
                "\"resetsAt\":1781654400}}," +
                "\"rateLimitResetCredits\":null}}";
            var observed = new DateTimeOffset(
                2026,
                7,
                1,
                0,
                0,
                0,
                TimeSpan.Zero);
            var snapshot = RateLimitSnapshotParser.ParseResponse(json, observed);

            AssertEqual(2, snapshot.Windows.Count, "window count");
            AssertEqual(10080, snapshot.FindLongWindow().WindowDurationMinutes,
                "long window classification");
            AssertNear(100.0, snapshot.FindLongWindow().RemainingPercent,
                "upper remaining clamp");
            AssertEqual(300, snapshot.FindFiveHourWindow().WindowDurationMinutes,
                "five-hour classification");
            AssertNear(0.0, snapshot.FindFiveHourWindow().RemainingPercent,
                "lower remaining clamp");
            AssertEqual(observed, snapshot.ObservedAt, "observation time");
        }

        private static void TestOptionalWindows()
        {
            const string json =
                "{\"result\":{\"rateLimits\":{\"primary\":null," +
                "\"secondary\":{\"usedPercent\":40.5," +
                "\"windowDurationMins\":1440,\"resetsAt\":null}}}}";
            var snapshot = RateLimitSnapshotParser.ParseResponse(
                json,
                DateTimeOffset.UtcNow);

            AssertTrue(snapshot.FindFiveHourWindow() == null,
                "missing five-hour window must remain unavailable");
            AssertEqual(1440, snapshot.FindLongWindow().WindowDurationMinutes,
                "one-day long window");
            AssertTrue(!snapshot.FindLongWindow().ResetsAt.HasValue,
                "nullable reset time");
        }

        private static void TestCreditDetailsAndSorting()
        {
            const string json =
                "{\"result\":{\"rateLimits\":null," +
                "\"rateLimitResetCredits\":{\"availableCount\":4,\"credits\":[" +
                "{\"id\":\"unknown\",\"status\":\"available\",\"expiresAt\":null}," +
                "{\"id\":\"later\",\"title\":\"Later\",\"description\":\"B\"," +
                "\"status\":\"available\",\"grantedAt\":1781000000," +
                "\"expiresAt\":1784246400}," +
                "{\"id\":\"earlier\",\"title\":\"Earlier\",\"description\":\"A\"," +
                "\"status\":\"available\",\"grantedAt\":1780000000," +
                "\"expiresAt\":1781654400}]}}}";
            var snapshot = RateLimitSnapshotParser.ParseResponse(
                json,
                DateTimeOffset.UtcNow);

            AssertEqual(4, snapshot.AvailableCreditCount,
                "authoritative available count");
            AssertTrue(snapshot.CreditDetailsAvailable,
                "credit rows disclosed");
            AssertEqual(3, snapshot.Credits.Count, "credit detail row count");
            AssertEqual("earlier", snapshot.Credits[0].Id,
                "earliest expiry first");
            AssertEqual("later", snapshot.Credits[1].Id,
                "later expiry second");
            AssertEqual("unknown", snapshot.Credits[2].Id,
                "unknown expiry last");
        }

        private static void TestCountOnlyCredits()
        {
            const string json =
                "{\"result\":{\"rateLimits\":{}," +
                "\"rateLimitResetCredits\":{\"availableCount\":3," +
                "\"credits\":null}}}";
            var snapshot = RateLimitSnapshotParser.ParseResponse(
                json,
                DateTimeOffset.UtcNow);

            AssertEqual(3, snapshot.AvailableCreditCount, "count-only total");
            AssertTrue(!snapshot.CreditDetailsAvailable,
                "null rows mean undisclosed details");
            AssertEqual(0, snapshot.Credits.Count, "no invented rows");
        }

        private static void TestEmptyCreditDetails()
        {
            const string json =
                "{\"result\":{\"rateLimits\":{}," +
                "\"rateLimitResetCredits\":{\"availableCount\":0," +
                "\"credits\":[]}}}";
            var snapshot = RateLimitSnapshotParser.ParseResponse(
                json,
                DateTimeOffset.UtcNow);

            AssertTrue(snapshot.CreditDetailsAvailable,
                "empty array is an available detail result");
            AssertEqual(0, snapshot.Credits.Count, "empty details");
        }

        private static void TestCountdown()
        {
            var now = new DateTimeOffset(
                2026,
                7,
                1,
                12,
                0,
                0,
                TimeSpan.Zero);

            AssertEqual(
                string.Empty,
                QuotaPresentation.FormatFinalDayCountdown(
                    now.AddHours(24).AddSeconds(1),
                    now),
                "outside final day");
            AssertEqual(
                "23:59",
                QuotaPresentation.FormatFinalDayCountdown(
                    now.AddHours(23).AddMinutes(59).AddSeconds(59),
                    now),
                "minute countdown flooring");
            AssertEqual(
                "00:00",
                QuotaPresentation.FormatFinalDayCountdown(
                    now.AddSeconds(-1),
                    now),
                "expired countdown");
            AssertEqual(
                string.Empty,
                QuotaPresentation.FormatFinalDayCountdown(null, now),
                "unknown expiry");
        }

        private static void TestPackagePathValidation()
        {
            var programFiles = Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFiles);
            var root = Path.Combine(programFiles, "WindowsApps");
            var valid = Path.Combine(
                root,
                "OpenAI.Codex_26.721.4979.0_x64__2p2nqsd0c76g0",
                "app",
                "ChatGPT.exe");
            var prefixTrap = Path.Combine(
                root,
                "OpenAI.CodexEvil_1.0_x64__attacker",
                "ChatGPT.exe");
            var traversal = Path.Combine(
                root,
                "OpenAI.Codex_fake",
                "..",
                "Other.App_1.0",
                "ChatGPT.exe");

            AssertTrue(CodexRuntimeLocator.IsCodexPackagePath(valid),
                "valid Codex package path");
            AssertTrue(!CodexRuntimeLocator.IsCodexPackagePath(prefixTrap),
                "lookalike package prefix rejected");
            AssertTrue(!CodexRuntimeLocator.IsCodexPackagePath(traversal),
                "canonical traversal rejected");
            AssertTrue(!CodexRuntimeLocator.IsCodexPackagePath(
                Path.Combine(
                    Path.GetTempPath(),
                    "OpenAI.Codex_fake",
                    "ChatGPT.exe")),
                "outside WindowsApps rejected");
        }

        private static void TestMalformedJson()
        {
            var threw = false;
            try
            {
                RateLimitSnapshotParser.ParseResponse(
                    "{not json",
                    DateTimeOffset.UtcNow);
            }
            catch (FormatException)
            {
                threw = true;
            }

            AssertTrue(threw, "malformed JSON must fail closed");
        }

        private static void TestOversizedJson()
        {
            var json =
                "{\"padding\":\"" +
                new string('x', (1024 * 1024) + 1) +
                "\"}";
            var threw = false;
            try
            {
                RateLimitSnapshotParser.ParseResponse(
                    json,
                    DateTimeOffset.UtcNow);
            }
            catch (FormatException)
            {
                threw = true;
            }

            AssertTrue(threw, "oversized JSON must fail closed");
        }

        private static void TestBoundedCreditPayload()
        {
            var builder = new StringBuilder();
            builder.Append(
                "{\"result\":{\"rateLimits\":{}," +
                "\"rateLimitResetCredits\":{\"availableCount\":2147483647," +
                "\"credits\":[");
            for (var index = 0; index < 140; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                builder.Append("{\"id\":\"");
                builder.Append(new string('i', 180));
                builder.Append("\",\"title\":\"");
                builder.Append(new string('t', 180));
                builder.Append("\",\"description\":\"");
                builder.Append(new string('d', 600));
                builder.Append("\",\"status\":\"");
                builder.Append(new string('s', 100));
                builder.Append("\"}");
            }

            builder.Append("]}}}");
            var snapshot = RateLimitSnapshotParser.ParseResponse(
                builder.ToString(),
                DateTimeOffset.UtcNow);

            AssertEqual(1000000, snapshot.AvailableCreditCount,
                "available count upper bound");
            AssertEqual(128, snapshot.Credits.Count,
                "credit row upper bound");
            AssertEqual(128, snapshot.Credits[0].Id.Length,
                "identifier length bound");
            AssertEqual(128, snapshot.Credits[0].Title.Length,
                "title length bound");
            AssertEqual(512, snapshot.Credits[0].Description.Length,
                "description length bound");
            AssertEqual(64, snapshot.Credits[0].Status.Length,
                "status length bound");
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                Console.WriteLine("PASS " + name);
            }
            catch (Exception exception)
            {
                _failed++;
                Console.Error.WriteLine(
                    "FAIL " + name + ": " + exception.Message);
            }
        }

        private static void AssertTrue(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static void AssertEqual<T>(
            T expected,
            T actual,
            string message)
        {
            if (!object.Equals(expected, actual))
            {
                throw new InvalidOperationException(
                    message +
                    " (expected " +
                    expected +
                    ", actual " +
                    actual +
                    ")");
            }
        }

        private static void AssertNear(
            double expected,
            double actual,
            string message)
        {
            if (Math.Abs(expected - actual) > 0.0001)
            {
                throw new InvalidOperationException(
                    message +
                    " (expected " +
                    expected +
                    ", actual " +
                    actual +
                    ")");
            }
        }
    }
}
