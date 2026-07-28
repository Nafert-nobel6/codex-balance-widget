using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using CodexBalanceWidget.Core;

namespace CodexBalanceWidget.App.ViewModels
{
    public sealed class WidgetViewModel : NotifyObject
    {
        private double _weeklyRemaining = double.NaN;
        private double _fiveHourRemaining = double.NaN;
        private string _weeklyDetail = "等待官方数据";
        private string _fiveHourDetail = "暂未启用";
        private string _statusText = "等待 Codex 启动";
        private Brush _statusBrush = CreateFrozenBrush(225, 70, 70);
        private string _lastUpdatedText = "尚未同步";
        private string _cardsSummary = "重置卡";
        private string _cardsCompactSummary = "0 张";
        private string _emptyCardsText = "正在等待重置卡数据";
        private bool _hasCredits;
        private DateTimeOffset? _lastObservedAt;
        private bool _lastSnapshotWasStale;

        public WidgetViewModel()
        {
            Credits = new ObservableCollection<ResetCreditViewModel>();
        }

        public double WeeklyRemaining
        {
            get { return _weeklyRemaining; }
            private set { SetField(ref _weeklyRemaining, value); }
        }

        public double FiveHourRemaining
        {
            get { return _fiveHourRemaining; }
            private set { SetField(ref _fiveHourRemaining, value); }
        }

        public string WeeklyDetail
        {
            get { return _weeklyDetail; }
            private set { SetField(ref _weeklyDetail, value); }
        }

        public string FiveHourDetail
        {
            get { return _fiveHourDetail; }
            private set { SetField(ref _fiveHourDetail, value); }
        }

        public string StatusText
        {
            get { return _statusText; }
            private set { SetField(ref _statusText, value); }
        }

        public Brush StatusBrush
        {
            get { return _statusBrush; }
            private set { SetField(ref _statusBrush, value); }
        }

        public string LastUpdatedText
        {
            get { return _lastUpdatedText; }
            private set { SetField(ref _lastUpdatedText, value); }
        }

        public string CardsSummary
        {
            get { return _cardsSummary; }
            private set { SetField(ref _cardsSummary, value); }
        }

        public string CardsCompactSummary
        {
            get { return _cardsCompactSummary; }
            private set { SetField(ref _cardsCompactSummary, value); }
        }

        public string EmptyCardsText
        {
            get { return _emptyCardsText; }
            private set { SetField(ref _emptyCardsText, value); }
        }

        public bool HasCredits
        {
            get { return _hasCredits; }
            private set { SetField(ref _hasCredits, value); }
        }

        public ObservableCollection<ResetCreditViewModel> Credits { get; private set; }

        public void ApplySnapshot(RateLimitSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            var weekly = snapshot.FindLongWindow();
            var fiveHour = snapshot.FindFiveHourWindow();
            WeeklyRemaining = weekly == null ? double.NaN : weekly.RemainingPercent;
            FiveHourRemaining = fiveHour == null ? double.NaN : fiveHour.RemainingPercent;
            WeeklyDetail = DescribeWindow(weekly, "等待官方数据");
            FiveHourDetail = DescribeWindow(fiveHour, "暂未启用");

            _lastObservedAt = snapshot.ObservedAt;
            _lastSnapshotWasStale = snapshot.IsStale;

            if (snapshot.IsStale)
            {
                StatusText = string.IsNullOrWhiteSpace(snapshot.StatusMessage)
                    ? "数据可能已过期"
                    : snapshot.StatusMessage;
                StatusBrush = CreateFrozenBrush(225, 70, 70);
            }
            else
            {
                StatusText = string.IsNullOrWhiteSpace(snapshot.StatusMessage)
                    ? "额度已同步"
                    : snapshot.StatusMessage;
                StatusBrush = CreateFrozenBrush(36, 177, 106);
            }

            var sorted = snapshot.Credits
                .OrderBy(delegate(ResetCredit item) { return item.ExpiresAt.HasValue ? 0 : 1; })
                .ThenBy(delegate(ResetCredit item)
                {
                    return item.ExpiresAt ?? DateTimeOffset.MaxValue;
                })
                .ToList();

            Credits.Clear();
            foreach (var credit in sorted)
            {
                Credits.Add(new ResetCreditViewModel(credit));
            }

            var undisclosedCount = Math.Max(0, snapshot.AvailableCreditCount - sorted.Count);
            if (!snapshot.CreditDetailsAvailable)
            {
                Credits.Clear();
                if (snapshot.AvailableCreditCount > 0)
                {
                    Credits.Add(
                        ResetCreditViewModel.CreateUndisclosed(
                            snapshot.AvailableCreditCount,
                            false));
                }
            }
            else if (undisclosedCount > 0)
            {
                Credits.Add(ResetCreditViewModel.CreateUndisclosed(undisclosedCount, true));
            }

            HasCredits = Credits.Count > 0;
            CardsSummary = snapshot.AvailableCreditCount > 0
                ? "重置卡  ·  " + snapshot.AvailableCreditCount + " 张可用"
                : "重置卡";
            CardsCompactSummary = snapshot.AvailableCreditCount + " 张";
            EmptyCardsText = snapshot.AvailableCreditCount > 0
                ? "官方暂未提供重置卡明细"
                : "当前没有可用的重置卡";
            UpdateClock();
        }

        public void SetWaitingForCodex()
        {
            StatusText = "等待 Codex 启动";
            StatusBrush = CreateFrozenBrush(225, 70, 70);
        }

        public void SetConnecting()
        {
            StatusText = "正在连接 Codex";
            StatusBrush = CreateFrozenBrush(225, 70, 70);
        }

        public void SetRuntimeError(string message)
        {
            StatusText = string.IsNullOrWhiteSpace(message) ? "同步暂时中断" : message;
            StatusBrush = CreateFrozenBrush(225, 70, 70);
            _lastSnapshotWasStale = true;
            UpdateClock();
        }

        public void UpdateClock()
        {
            var now = DateTimeOffset.Now;
            foreach (var credit in Credits)
            {
                credit.UpdateClock(now);
            }

            if (_lastObservedAt.HasValue)
            {
                var local = _lastObservedAt.Value.ToLocalTime();
                var age = now - local;
                if (age < TimeSpan.Zero)
                {
                    age = TimeSpan.Zero;
                }

                LastUpdatedText = "最近更新 " + local.ToString("HH:mm:ss");
                if (_lastSnapshotWasStale && age >= TimeSpan.FromMinutes(1.0))
                {
                    LastUpdatedText += "  ·  " + FormatAge(age);
                }
            }
        }

        private static string DescribeWindow(QuotaWindow window, string unavailableText)
        {
            if (window == null)
            {
                return unavailableText;
            }

            if (!window.ResetsAt.HasValue)
            {
                return "剩余额度";
            }

            var local = window.ResetsAt.Value.ToLocalTime();
            if (local <= DateTimeOffset.Now)
            {
                return "即将刷新";
            }

            return local.ToString("M月d日 HH:mm") + " 重置";
        }

        private static string FormatAge(TimeSpan age)
        {
            if (age.TotalHours >= 1.0)
            {
                return ((int)age.TotalHours) + " 小时前";
            }

            return Math.Max(1, (int)age.TotalMinutes) + " 分钟前";
        }

        private static Brush CreateFrozenBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
