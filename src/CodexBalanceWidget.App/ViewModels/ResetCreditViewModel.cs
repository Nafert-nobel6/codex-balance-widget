using System;
using CodexBalanceWidget.Core;

namespace CodexBalanceWidget.App.ViewModels
{
    public sealed class ResetCreditViewModel : NotifyObject
    {
        private readonly DateTimeOffset? _expiresAt;
        private string _expiryText;
        private bool _isUrgent;

        public ResetCreditViewModel(ResetCredit credit)
        {
            // The product surface uses one stable, user-facing name even when
            // protocol payloads contain historical or internal card labels.
            Title = "重置卡";
            Description = string.IsNullOrWhiteSpace(credit.Description)
                ? NormalizeStatus(credit.Status)
                : credit.Description.Trim();
            _expiresAt = credit.ExpiresAt;
            UpdateClock(DateTimeOffset.Now);
        }

        private ResetCreditViewModel(string title, string description)
        {
            Title = title;
            Description = description;
            _expiresAt = null;
            UpdateClock(DateTimeOffset.Now);
        }

        public string Title { get; private set; }
        public string Description { get; private set; }
        public DateTimeOffset? ExpiresAt { get { return _expiresAt; } }

        public string ExpiryText
        {
            get { return _expiryText; }
            private set { SetField(ref _expiryText, value); }
        }

        public bool IsUrgent
        {
            get { return _isUrgent; }
            private set { SetField(ref _isUrgent, value); }
        }

        public static ResetCreditViewModel CreateUndisclosed(int count, bool detailsPartiallyAvailable)
        {
            var title = detailsPartiallyAvailable
                ? "另有 " + count + " 张重置卡"
                : "可用 " + count + " 张重置卡";
            return new ResetCreditViewModel(title, "官方暂未提供到期明细");
        }

        public void UpdateClock(DateTimeOffset now)
        {
            if (!_expiresAt.HasValue)
            {
                IsUrgent = false;
                ExpiryText = "到期时间未公开";
                return;
            }

            var remaining = _expiresAt.Value - now;
            if (remaining <= TimeSpan.Zero)
            {
                IsUrgent = true;
                ExpiryText = "已到期";
                return;
            }

            if (remaining <= TimeSpan.FromHours(24.0))
            {
                IsUrgent = true;
                var totalHours = Math.Max(0, (int)Math.Floor(remaining.TotalHours));
                var minutes = Math.Max(0, remaining.Minutes);
                ExpiryText = "剩余 " + totalHours.ToString("00") + ":" + minutes.ToString("00");
                return;
            }

            IsUrgent = false;
            ExpiryText = _expiresAt.Value.ToLocalTime().ToString("M月d日 HH:mm") + " 到期";
        }

        private static string NormalizeStatus(string status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return "可用于恢复额度";
            }

            return status.Trim();
        }
    }
}
