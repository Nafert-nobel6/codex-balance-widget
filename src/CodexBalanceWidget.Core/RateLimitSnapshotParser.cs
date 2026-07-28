using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace CodexBalanceWidget.Core
{
    /// <summary>
    /// Converts the stable account/rateLimits/read response into the UI contract.
    /// The parser deliberately ignores unknown fields so newer app-server versions
    /// remain compatible.
    /// </summary>
    public static class RateLimitSnapshotParser
    {
        private const int MaxJsonCharacters = 1024 * 1024;
        private const int MaxJsonRecursionDepth = 64;
        private const int MaxCreditRows = 128;
        private const int MaxIdentifierCharacters = 128;
        private const int MaxTitleCharacters = 128;
        private const int MaxDescriptionCharacters = 512;
        private const int MaxStatusCharacters = 64;
        private const int MaxAvailableCreditCount = 1000000;

        private static readonly DateTimeOffset UnixEpoch =
            new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public static RateLimitSnapshot ParseResponse(
            string json,
            DateTimeOffset observedAt)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new FormatException("The app-server response was empty.");
            }

            if (json.Length > MaxJsonCharacters)
            {
                throw new FormatException(
                    "The app-server response exceeded the safe size limit.");
            }

            object parsed;
            try
            {
                var serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = MaxJsonCharacters;
                serializer.RecursionLimit = MaxJsonRecursionDepth;
                parsed = serializer.DeserializeObject(json);
            }
            catch (Exception exception)
            {
                throw new FormatException(
                    "The app-server response was not valid JSON.",
                    exception);
            }

            var response = parsed as IDictionary<string, object>;
            if (response == null)
            {
                throw new FormatException("The app-server response was not an object.");
            }

            return ParseResponseObject(response, observedAt);
        }

        public static RateLimitSnapshot ParseResponseObject(
            IDictionary<string, object> response,
            DateTimeOffset observedAt)
        {
            if (response == null)
            {
                throw new ArgumentNullException("response");
            }

            IDictionary<string, object> result = response;
            object resultValue;
            if (response.TryGetValue("result", out resultValue))
            {
                result = resultValue as IDictionary<string, object>;
                if (result == null)
                {
                    throw new FormatException(
                        "The app-server response did not contain a result object.");
                }
            }

            var windows = new List<QuotaWindow>();
            var rateLimits = GetObject(result, "rateLimits");
            if (rateLimits != null)
            {
                AddWindow(windows, GetObject(rateLimits, "primary"));
                AddWindow(windows, GetObject(rateLimits, "secondary"));
            }

            var availableCount = 0;
            var creditDetailsAvailable = false;
            var credits = new List<ResetCredit>();
            var resetCredits = GetObject(result, "rateLimitResetCredits");
            if (resetCredits != null)
            {
                int parsedCount;
                if (TryGetInt(resetCredits, "availableCount", out parsedCount))
                {
                    availableCount = Math.Min(
                        MaxAvailableCreditCount,
                        Math.Max(0, parsedCount));
                }

                object creditRows;
                if (resetCredits.TryGetValue("credits", out creditRows) &&
                    creditRows != null)
                {
                    var enumerable = creditRows as IEnumerable;
                    if (enumerable != null && !(creditRows is string))
                    {
                        creditDetailsAvailable = true;
                        foreach (var row in enumerable)
                        {
                            if (credits.Count >= MaxCreditRows)
                            {
                                break;
                            }

                            var credit = ParseCredit(
                                row as IDictionary<string, object>);
                            if (credit != null)
                            {
                                credits.Add(credit);
                            }
                        }
                    }
                }
            }

            credits.Sort(CompareCredits);

            return new RateLimitSnapshot(
                windows,
                availableCount,
                creditDetailsAvailable,
                credits,
                observedAt,
                rateLimits == null
                    ? "Rate limit data is unavailable."
                    : string.Empty,
                false);
        }

        private static void AddWindow(
            IList<QuotaWindow> windows,
            IDictionary<string, object> value)
        {
            if (value == null)
            {
                return;
            }

            double usedPercent;
            int duration;
            if (!TryGetDouble(value, "usedPercent", out usedPercent) ||
                !TryGetInt(value, "windowDurationMins", out duration) ||
                duration <= 0 ||
                double.IsNaN(usedPercent) ||
                double.IsInfinity(usedPercent))
            {
                return;
            }

            windows.Add(
                new QuotaWindow(
                    usedPercent,
                    duration,
                    GetUnixTimestamp(value, "resetsAt")));
        }

        private static ResetCredit ParseCredit(
            IDictionary<string, object> value)
        {
            if (value == null)
            {
                return null;
            }

            return new ResetCredit(
                GetString(value, "id", MaxIdentifierCharacters),
                GetString(value, "title", MaxTitleCharacters),
                GetString(
                    value,
                    "description",
                    MaxDescriptionCharacters),
                GetString(value, "status", MaxStatusCharacters),
                GetUnixTimestamp(value, "grantedAt"),
                GetUnixTimestamp(value, "expiresAt"));
        }

        private static int CompareCredits(ResetCredit left, ResetCredit right)
        {
            if (left == null)
            {
                return right == null ? 0 : 1;
            }

            if (right == null)
            {
                return -1;
            }

            if (left.ExpiresAt.HasValue && !right.ExpiresAt.HasValue)
            {
                return -1;
            }

            if (!left.ExpiresAt.HasValue && right.ExpiresAt.HasValue)
            {
                return 1;
            }

            if (left.ExpiresAt.HasValue && right.ExpiresAt.HasValue)
            {
                var comparison = left.ExpiresAt.Value.CompareTo(right.ExpiresAt.Value);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return string.Compare(left.Id, right.Id, StringComparison.Ordinal);
        }

        private static IDictionary<string, object> GetObject(
            IDictionary<string, object> value,
            string key)
        {
            object child;
            if (!value.TryGetValue(key, out child) || child == null)
            {
                return null;
            }

            return child as IDictionary<string, object>;
        }

        private static string GetString(
            IDictionary<string, object> value,
            string key,
            int maximumCharacters)
        {
            object child;
            if (!value.TryGetValue(key, out child) || child == null)
            {
                return string.Empty;
            }

            var result = Convert.ToString(
                child,
                CultureInfo.InvariantCulture) ?? string.Empty;
            return result.Length <= maximumCharacters
                ? result
                : result.Substring(0, maximumCharacters);
        }

        private static bool TryGetInt(
            IDictionary<string, object> value,
            string key,
            out int result)
        {
            result = 0;
            object child;
            if (!value.TryGetValue(key, out child) || child == null)
            {
                return false;
            }

            try
            {
                result = Convert.ToInt32(child, CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool TryGetDouble(
            IDictionary<string, object> value,
            string key,
            out double result)
        {
            result = 0.0;
            object child;
            if (!value.TryGetValue(key, out child) || child == null)
            {
                return false;
            }

            try
            {
                result = Convert.ToDouble(child, CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static DateTimeOffset? GetUnixTimestamp(
            IDictionary<string, object> value,
            string key)
        {
            object child;
            if (!value.TryGetValue(key, out child) || child == null)
            {
                return null;
            }

            try
            {
                var seconds = Convert.ToInt64(child, CultureInfo.InvariantCulture);
                return UnixEpoch.AddSeconds(seconds);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
