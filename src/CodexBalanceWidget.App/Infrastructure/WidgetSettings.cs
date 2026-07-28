using System;

namespace CodexBalanceWidget.App.Infrastructure
{
    public static class WidgetThemes
    {
        public const string System = "System";
        public const string Light = "Light";
        public const string Dark = "Dark";

        public static bool IsSupported(string value)
        {
            return string.Equals(value, System, StringComparison.Ordinal) ||
                string.Equals(value, Light, StringComparison.Ordinal) ||
                string.Equals(value, Dark, StringComparison.Ordinal);
        }
    }

    public sealed class WidgetSettings
    {
        public WidgetSettings()
        {
            Theme = WidgetThemes.Light;
            PanelOpacity = 0.94;
            ArchiveDelay = TimeSpan.FromSeconds(30.0);
            WindowWidth = 300.0;
            WindowHeight = 320.0;
            WindowOffsetX = 0.0;
            WindowOffsetY = 0.0;
            BubbleY = 0.0;
            AvatarPath = string.Empty;
        }

        public string Theme { get; set; }
        public double PanelOpacity { get; set; }
        public TimeSpan ArchiveDelay { get; set; }
        public double WindowWidth { get; set; }
        public double WindowHeight { get; set; }
        public double WindowOffsetX { get; set; }
        public double WindowOffsetY { get; set; }
        public double BubbleY { get; set; }
        public string AvatarPath { get; set; }

        public WidgetSettings Clone()
        {
            return new WidgetSettings
            {
                Theme = Theme,
                PanelOpacity = PanelOpacity,
                ArchiveDelay = ArchiveDelay,
                WindowWidth = WindowWidth,
                WindowHeight = WindowHeight,
                WindowOffsetX = WindowOffsetX,
                WindowOffsetY = WindowOffsetY,
                BubbleY = BubbleY,
                AvatarPath = AvatarPath
            };
        }
    }
}
