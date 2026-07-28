using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CodexBalanceWidget.Core
{
    public sealed class QuotaWindow
    {
        public QuotaWindow(double usedPercent, int windowDurationMinutes, DateTimeOffset? resetsAt)
        {
            UsedPercent = usedPercent;
            WindowDurationMinutes = windowDurationMinutes;
            ResetsAt = resetsAt;
        }

        public double UsedPercent { get; private set; }
        public double RemainingPercent
        {
            get
            {
                var remaining = 100.0 - UsedPercent;
                return Math.Max(0.0, Math.Min(100.0, remaining));
            }
        }

        public int WindowDurationMinutes { get; private set; }
        public DateTimeOffset? ResetsAt { get; private set; }
    }

    public sealed class ResetCredit
    {
        public ResetCredit(
            string id,
            string title,
            string description,
            string status,
            DateTimeOffset? grantedAt,
            DateTimeOffset? expiresAt)
        {
            Id = id ?? string.Empty;
            Title = title ?? string.Empty;
            Description = description ?? string.Empty;
            Status = status ?? string.Empty;
            GrantedAt = grantedAt;
            ExpiresAt = expiresAt;
        }

        public string Id { get; private set; }
        public string Title { get; private set; }
        public string Description { get; private set; }
        public string Status { get; private set; }
        public DateTimeOffset? GrantedAt { get; private set; }
        public DateTimeOffset? ExpiresAt { get; private set; }
    }

    public sealed class RateLimitSnapshot
    {
        public RateLimitSnapshot(
            IList<QuotaWindow> windows,
            int availableCreditCount,
            bool creditDetailsAvailable,
            IList<ResetCredit> credits,
            DateTimeOffset observedAt,
            string statusMessage,
            bool isStale)
        {
            Windows = windows ?? new List<QuotaWindow>();
            AvailableCreditCount = Math.Max(0, availableCreditCount);
            CreditDetailsAvailable = creditDetailsAvailable;
            Credits = credits ?? new List<ResetCredit>();
            ObservedAt = observedAt;
            StatusMessage = statusMessage ?? string.Empty;
            IsStale = isStale;
        }

        public IList<QuotaWindow> Windows { get; private set; }
        public int AvailableCreditCount { get; private set; }
        public bool CreditDetailsAvailable { get; private set; }
        public IList<ResetCredit> Credits { get; private set; }
        public DateTimeOffset ObservedAt { get; private set; }
        public string StatusMessage { get; private set; }
        public bool IsStale { get; private set; }

        public QuotaWindow FindFiveHourWindow()
        {
            foreach (var window in Windows)
            {
                if (window.WindowDurationMinutes == 300)
                {
                    return window;
                }
            }

            return null;
        }

        public QuotaWindow FindLongWindow()
        {
            QuotaWindow result = null;
            foreach (var window in Windows)
            {
                if (window.WindowDurationMinutes < 1440)
                {
                    continue;
                }

                if (result == null ||
                    window.WindowDurationMinutes > result.WindowDurationMinutes)
                {
                    result = window;
                }
            }

            return result;
        }
    }

    public sealed class RateLimitSnapshotEventArgs : EventArgs
    {
        public RateLimitSnapshotEventArgs(RateLimitSnapshot snapshot)
        {
            Snapshot = snapshot;
        }

        public RateLimitSnapshot Snapshot { get; private set; }
    }

    public interface IRateLimitSource : IDisposable
    {
        event EventHandler<RateLimitSnapshotEventArgs> SnapshotUpdated;

        Task StartAsync(CancellationToken cancellationToken);
        Task<RateLimitSnapshot> RefreshAsync(CancellationToken cancellationToken);
        Task StopAsync();
    }

    public interface IWidgetLogger
    {
        void Info(string message);
        void Warn(string message);
        void Error(string message, Exception exception);
    }
}
