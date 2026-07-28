using System;
using System.Globalization;

namespace CodexBalanceWidget.Core
{
    public static class QuotaPresentation
    {
        /// <summary>
        /// Returns an HH:mm countdown during the final 24 hours. An empty string
        /// means that minute-level countdown presentation is not yet applicable.
        /// </summary>
        public static string FormatFinalDayCountdown(
            DateTimeOffset? expiresAt,
            DateTimeOffset now)
        {
            if (!expiresAt.HasValue)
            {
                return string.Empty;
            }

            var remaining = expiresAt.Value - now;
            if (remaining.TotalSeconds <= 0)
            {
                return "00:00";
            }

            if (remaining.TotalHours > 24.0)
            {
                return string.Empty;
            }

            var totalMinutes = (int)Math.Floor(remaining.TotalMinutes);
            var hours = totalMinutes / 60;
            var minutes = totalMinutes % 60;
            return hours.ToString("00", CultureInfo.InvariantCulture) +
                ":" +
                minutes.ToString("00", CultureInfo.InvariantCulture);
        }
    }
}
