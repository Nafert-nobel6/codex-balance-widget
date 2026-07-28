using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace CodexBalanceWidget.App.Controls
{
    public sealed class QuotaRing : FrameworkElement
    {
        public static readonly DependencyProperty RemainingPercentProperty =
            DependencyProperty.Register(
                "RemainingPercent",
                typeof(double),
                typeof(QuotaRing),
                new FrameworkPropertyMetadata(
                    double.NaN,
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty HeadingProperty =
            DependencyProperty.Register(
                "Heading",
                typeof(string),
                typeof(QuotaRing),
                new FrameworkPropertyMetadata(
                    string.Empty,
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty DetailProperty =
            DependencyProperty.Register(
                "Detail",
                typeof(string),
                typeof(QuotaRing),
                new FrameworkPropertyMetadata(
                    string.Empty,
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty AccentProperty =
            DependencyProperty.Register(
                "Accent",
                typeof(Brush),
                typeof(QuotaRing),
                new FrameworkPropertyMetadata(
                    new SolidColorBrush(Color.FromRgb(112, 225, 178)),
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public double RemainingPercent
        {
            get { return (double)GetValue(RemainingPercentProperty); }
            set { SetValue(RemainingPercentProperty, value); }
        }

        public string Heading
        {
            get { return (string)GetValue(HeadingProperty); }
            set { SetValue(HeadingProperty, value); }
        }

        public string Detail
        {
            get { return (string)GetValue(DetailProperty); }
            set { SetValue(DetailProperty, value); }
        }

        public Brush Accent
        {
            get { return (Brush)GetValue(AccentProperty); }
            set { SetValue(AccentProperty, value); }
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            return new Size(132.0, 146.0);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);

            var width = ActualWidth;
            var center = new Point(width / 2.0, 54.0);
            const double radius = 45.5;
            const double strokeWidth = 8.0;
            var track = new SolidColorBrush(Color.FromRgb(48, 52, 63));
            var muted = new SolidColorBrush(Color.FromRgb(132, 138, 151));
            var text = new SolidColorBrush(Color.FromRgb(245, 246, 248));
            var subtle = new SolidColorBrush(Color.FromRgb(115, 121, 134));

            track.Freeze();
            muted.Freeze();
            text.Freeze();
            subtle.Freeze();

            var trackPen = new Pen(track, strokeWidth);
            trackPen.Freeze();
            drawingContext.DrawEllipse(null, trackPen, center, radius, radius);

            var isAvailable = !double.IsNaN(RemainingPercent) &&
                              !double.IsInfinity(RemainingPercent);
            var displayValue = isAvailable
                ? Math.Max(0.0, Math.Min(100.0, RemainingPercent))
                : 0.0;

            if (isAvailable && displayValue > 0.0)
            {
                var accent = Accent ?? muted;
                var progressPen = new Pen(accent, strokeWidth);
                progressPen.StartLineCap = PenLineCap.Round;
                progressPen.EndLineCap = PenLineCap.Round;
                progressPen.Freeze();

                if (displayValue >= 99.95)
                {
                    drawingContext.DrawEllipse(null, progressPen, center, radius, radius);
                }
                else
                {
                    var sweep = displayValue * 3.6;
                    var start = new Point(center.X, center.Y - radius);
                    var radians = (sweep - 90.0) * Math.PI / 180.0;
                    var end = new Point(
                        center.X + radius * Math.Cos(radians),
                        center.Y + radius * Math.Sin(radians));
                    var figure = new PathFigure();
                    figure.StartPoint = start;
                    figure.IsClosed = false;
                    figure.IsFilled = false;
                    figure.Segments.Add(
                        new ArcSegment(
                            end,
                            new Size(radius, radius),
                            0.0,
                            sweep > 180.0,
                            SweepDirection.Clockwise,
                            true));
                    var geometry = new PathGeometry();
                    geometry.Figures.Add(figure);
                    geometry.Freeze();
                    drawingContext.DrawGeometry(null, progressPen, geometry);
                }
            }

            DrawCenteredText(
                drawingContext,
                isAvailable
                    ? displayValue.ToString("0", CultureInfo.InvariantCulture) + "%"
                    : "—",
                26.0,
                FontWeights.SemiBold,
                isAvailable ? text : muted,
                center.Y - 17.0,
                width);
            DrawCenteredText(
                drawingContext,
                Heading ?? string.Empty,
                13.5,
                FontWeights.SemiBold,
                text,
                111.0,
                width);
            DrawCenteredText(
                drawingContext,
                Detail ?? string.Empty,
                10.5,
                FontWeights.Normal,
                subtle,
                131.0,
                width);
        }

        private static void DrawCenteredText(
            DrawingContext drawingContext,
            string value,
            double size,
            FontWeight weight,
            Brush brush,
            double top,
            double availableWidth)
        {
            var formatted = new FormattedText(
                value,
                CultureInfo.GetCultureInfo("zh-CN"),
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI, Microsoft YaHei UI"), FontStyles.Normal, weight, FontStretches.Normal),
                size,
                brush,
                1.0);
            formatted.MaxTextWidth = Math.Max(1.0, availableWidth - 4.0);
            formatted.Trimming = TextTrimming.CharacterEllipsis;
            drawingContext.DrawText(
                formatted,
                new Point(Math.Max(0.0, (availableWidth - formatted.Width) / 2.0), top));
        }
    }
}
