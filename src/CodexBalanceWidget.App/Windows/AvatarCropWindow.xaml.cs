using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexBalanceWidget.App.Infrastructure;
using Microsoft.Win32;

namespace CodexBalanceWidget.App.Windows
{
    public partial class AvatarCropWindow : Window
    {
        private readonly AvatarImageService _imageService;
        private string _sourcePath;
        private bool _updatingZoom;

        public AvatarCropWindow(
            AvatarImageService imageService,
            string sourcePath)
        {
            if (imageService == null)
            {
                throw new ArgumentNullException("imageService");
            }

            _imageService = imageService;
            InitializeComponent();
            LoadSource(sourcePath);
        }

        public string AvatarPath { get; private set; }

        private void LoadSource(string sourcePath)
        {
            try
            {
                var information = _imageService.Inspect(sourcePath);
                var source = LoadOrientedBitmap(sourcePath, information.ExifOrientation);
                _sourcePath = sourcePath;
                ErrorText.Text = string.Empty;
                CropViewport.SetSource(
                    source,
                    information.OrientedWidth,
                    information.OrientedHeight);
                _updatingZoom = true;
                ZoomSlider.Value = 1.0;
                ZoomValueText.Text = "1.0×";
                _updatingZoom = false;
                UpdatePreviews();
            }
            catch (AvatarImageException exception)
            {
                ErrorText.Text = TranslateImageError(exception.Message);
                UseAvatarButton.IsEnabled = false;
            }
            catch (Exception)
            {
                ErrorText.Text = "无法读取这张图片，请重新选择 PNG 或 JPEG。";
                UseAvatarButton.IsEnabled = false;
            }
        }

        private void OnCropParametersChanged(object sender, EventArgs e)
        {
            if (ZoomSlider == null ||
                ZoomValueText == null ||
                WindowAvatarPreview == null ||
                BubbleAvatarPreview == null ||
                UseAvatarButton == null)
            {
                return;
            }

            _updatingZoom = true;
            ZoomSlider.Value = CropViewport.Zoom;
            ZoomValueText.Text = CropViewport.Zoom.ToString("0.0") + "×";
            _updatingZoom = false;
            UpdatePreviews();
        }

        private void OnZoomSliderValueChanged(
            object sender,
            RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updatingZoom || CropViewport == null)
            {
                return;
            }

            CropViewport.Zoom = e.NewValue;
            if (ZoomValueText != null)
            {
                ZoomValueText.Text = e.NewValue.ToString("0.0") + "×";
            }
        }

        private void UpdatePreviews()
        {
            if (CropViewport == null ||
                WindowAvatarPreview == null ||
                BubbleAvatarPreview == null ||
                UseAvatarButton == null)
            {
                return;
            }

            var preview = CropViewport.CreatePreview(96);
            if (preview == null)
            {
                WindowAvatarPreview.Fill = null;
                BubbleAvatarPreview.Fill = null;
                UseAvatarButton.IsEnabled = false;
                return;
            }

            var windowBrush = new ImageBrush(preview);
            windowBrush.Stretch = Stretch.UniformToFill;
            WindowAvatarPreview.Fill = windowBrush;

            var bubbleBrush = new ImageBrush(preview);
            bubbleBrush.Stretch = Stretch.UniformToFill;
            BubbleAvatarPreview.Fill = bubbleBrush;
            UseAvatarButton.IsEnabled = true;
        }

        private void OnReselectClick(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog();
            dialog.Title = "重新选择头像";
            dialog.Filter = "图片文件|*.png;*.jpg;*.jpeg";
            dialog.CheckFileExists = true;
            if (dialog.ShowDialog(this) == true)
            {
                LoadSource(dialog.FileName);
            }
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            CompleteDialog(false);
        }

        private void OnUseAvatarClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_sourcePath))
            {
                return;
            }

            UseAvatarButton.IsEnabled = false;
            ErrorText.Text = string.Empty;
            try
            {
                var parameters = new AvatarCropParameters(
                    CropViewport.CenterX,
                    CropViewport.CenterY,
                    CropViewport.Zoom);
                AvatarPath = _imageService.ImportAndCrop(_sourcePath, parameters);
                CompleteDialog(true);
            }
            catch (AvatarImageException exception)
            {
                ErrorText.Text = TranslateImageError(exception.Message);
                UseAvatarButton.IsEnabled = true;
            }
            catch (Exception)
            {
                ErrorText.Text = "保存头像失败，原头像未被更改。";
                UseAvatarButton.IsEnabled = true;
            }
        }

        private void OnWindowKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CompleteDialog(false);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && UseAvatarButton.IsEnabled)
            {
                OnUseAvatarClick(UseAvatarButton, new RoutedEventArgs());
                e.Handled = true;
            }
        }

        private void CompleteDialog(bool result)
        {
            try
            {
                DialogResult = result;
            }
            catch (InvalidOperationException)
            {
                Close();
            }
        }

        private void OnTitleAreaMouseLeftButtonDown(
            object sender,
            MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed &&
                e.OriginalSource is TextBlock)
            {
                DragMove();
            }
        }

        private static BitmapSource LoadOrientedBitmap(
            string path,
            int exifOrientation)
        {
            BitmapSource source;
            using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            {
                var decoder = BitmapDecoder.Create(
                    stream,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                source = decoder.Frames[0];
            }

            switch (exifOrientation)
            {
                case 2:
                    source = Transform(source, new ScaleTransform(-1.0, 1.0));
                    break;
                case 3:
                    source = Transform(source, new RotateTransform(180.0));
                    break;
                case 4:
                    source = Transform(source, new ScaleTransform(1.0, -1.0));
                    break;
                case 5:
                    source = Transform(source, new ScaleTransform(-1.0, 1.0));
                    source = Transform(source, new RotateTransform(270.0));
                    break;
                case 6:
                    source = Transform(source, new RotateTransform(90.0));
                    break;
                case 7:
                    source = Transform(source, new ScaleTransform(-1.0, 1.0));
                    source = Transform(source, new RotateTransform(90.0));
                    break;
                case 8:
                    source = Transform(source, new RotateTransform(270.0));
                    break;
            }

            if (source.CanFreeze)
            {
                source.Freeze();
            }

            return source;
        }

        private static BitmapSource Transform(
            BitmapSource source,
            Transform transform)
        {
            var result = new TransformedBitmap(source, transform);
            if (result.CanFreeze)
            {
                result.Freeze();
            }

            return result;
        }

        private static string TranslateImageError(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return "无法处理这张图片，请重新选择。";
            }

            if (message.IndexOf("20 MB", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "图片不能超过 20 MB。";
            }

            if (message.IndexOf("8192", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "图片尺寸不能超过 8192 像素。";
            }

            if (message.IndexOf("PNG", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("JPEG", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "只支持 PNG 与 JPEG 图片。";
            }

            return "无法处理这张图片，请重新选择。";
        }
    }

    public sealed class AvatarCropViewport : FrameworkElement
    {
        public static readonly DependencyProperty AccentProperty =
            DependencyProperty.Register(
                "Accent",
                typeof(Brush),
                typeof(AvatarCropViewport),
                new FrameworkPropertyMetadata(
                    Brushes.MediumAquamarine,
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty TrackProperty =
            DependencyProperty.Register(
                "Track",
                typeof(Brush),
                typeof(AvatarCropViewport),
                new FrameworkPropertyMetadata(
                    Brushes.DimGray,
                    FrameworkPropertyMetadataOptions.AffectsRender));

        private BitmapSource _source;
        private int _orientedWidth;
        private int _orientedHeight;
        private double _centerX = 0.5;
        private double _centerY = 0.5;
        private double _zoom = 1.0;
        private Point _dragStart;
        private double _dragStartCenterX;
        private double _dragStartCenterY;

        public AvatarCropViewport()
        {
            Focusable = true;
            ClipToBounds = true;
            Cursor = Cursors.SizeAll;
            MouseLeftButtonDown += OnMouseLeftButtonDown;
            MouseLeftButtonUp += OnMouseLeftButtonUp;
            MouseMove += OnMouseMove;
            MouseWheel += OnMouseWheel;
            SizeChanged += delegate { InvalidateVisual(); };
        }

        public event EventHandler ParametersChanged;

        public Brush Accent
        {
            get { return (Brush)GetValue(AccentProperty); }
            set { SetValue(AccentProperty, value); }
        }

        public Brush Track
        {
            get { return (Brush)GetValue(TrackProperty); }
            set { SetValue(TrackProperty, value); }
        }

        public double CenterX { get { return _centerX; } }
        public double CenterY { get { return _centerY; } }

        public double Zoom
        {
            get { return _zoom; }
            set
            {
                var next = Clamp(value, 1.0, 8.0);
                if (Math.Abs(next - _zoom) < 0.0001)
                {
                    return;
                }

                _zoom = next;
                ClampCenter();
                InvalidateVisual();
                RaiseParametersChanged();
            }
        }

        public void SetSource(
            BitmapSource source,
            int orientedWidth,
            int orientedHeight)
        {
            _source = source;
            _orientedWidth = orientedWidth;
            _orientedHeight = orientedHeight;
            _centerX = 0.5;
            _centerY = 0.5;
            _zoom = 1.0;
            InvalidateVisual();
            RaiseParametersChanged();
        }

        public BitmapSource CreatePreview(int pixels)
        {
            if (_source == null || pixels <= 0)
            {
                return null;
            }

            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.PushClip(
                    new EllipseGeometry(
                        new Point(pixels / 2.0, pixels / 2.0),
                        pixels / 2.0,
                        pixels / 2.0));
                DrawCrop(
                    context,
                    new Rect(0.0, 0.0, pixels, pixels),
                    false);
                context.Pop();
            }

            var bitmap = new RenderTargetBitmap(
                pixels,
                pixels,
                96.0,
                96.0,
                PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var side = Math.Min(availableSize.Width, availableSize.Height);
            if (double.IsInfinity(side))
            {
                side = 232.0;
            }

            side = Clamp(side, 176.0, 232.0);
            return new Size(side, side);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            var side = Math.Min(ActualWidth, ActualHeight);
            var crop = new Rect(
                (ActualWidth - side) / 2.0,
                (ActualHeight - side) / 2.0,
                side,
                side);
            drawingContext.DrawRoundedRectangle(
                Brushes.Black,
                null,
                crop,
                side / 2.0,
                side / 2.0);
            if (_source == null)
            {
                return;
            }

            drawingContext.PushClip(new RectangleGeometry(crop));
            DrawCrop(drawingContext, crop, true);
            drawingContext.Pop();

            drawingContext.PushClip(new EllipseGeometry(
                crop.TopLeft + new Vector(side / 2.0, side / 2.0),
                side / 2.0,
                side / 2.0));
            DrawCrop(drawingContext, crop, false);
            drawingContext.Pop();

            var border = new Pen(Accent, 2.0);
            drawingContext.DrawEllipse(
                null,
                border,
                crop.TopLeft + new Vector(side / 2.0, side / 2.0),
                side / 2.0 - 1.0,
                side / 2.0 - 1.0);
        }

        private void DrawCrop(
            DrawingContext context,
            Rect target,
            bool dimmed)
        {
            if (_source == null || _orientedWidth <= 0 || _orientedHeight <= 0)
            {
                return;
            }

            var sourceSide = Math.Min(_orientedWidth, _orientedHeight) / _zoom;
            var halfSide = sourceSide / 2.0;
            var centerPixelsX = Clamp(
                _centerX * _orientedWidth,
                halfSide,
                _orientedWidth - halfSide);
            var centerPixelsY = Clamp(
                _centerY * _orientedHeight,
                halfSide,
                _orientedHeight - halfSide);
            var scale = target.Width / sourceSide;
            var imageRect = new Rect(
                target.Left - (centerPixelsX - halfSide) * scale,
                target.Top - (centerPixelsY - halfSide) * scale,
                _orientedWidth * scale,
                _orientedHeight * scale);
            context.DrawImage(_source, imageRect);
            if (dimmed)
            {
                context.DrawRectangle(
                    new SolidColorBrush(Color.FromArgb(118, 0, 0, 0)),
                    null,
                    target);
            }
        }

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_source == null)
            {
                return;
            }

            _dragStart = e.GetPosition(this);
            _dragStartCenterX = _centerX;
            _dragStartCenterY = _centerY;
            CaptureMouse();
            e.Handled = true;
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            var current = e.GetPosition(this);
            var delta = current - _dragStart;
            var side = Math.Max(1.0, Math.Min(ActualWidth, ActualHeight));
            var sourceSide = Math.Min(_orientedWidth, _orientedHeight) / _zoom;
            var sourceDeltaX = delta.X * sourceSide / side;
            var sourceDeltaY = delta.Y * sourceSide / side;
            _centerX = _dragStartCenterX - sourceDeltaX / _orientedWidth;
            _centerY = _dragStartCenterY - sourceDeltaY / _orientedHeight;
            ClampCenter();
            InvalidateVisual();
            RaiseParametersChanged();
            e.Handled = true;
        }

        private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            Zoom = _zoom * Math.Pow(1.12, e.Delta / 120.0);
            e.Handled = true;
        }

        private void ClampCenter()
        {
            if (_orientedWidth <= 0 || _orientedHeight <= 0)
            {
                _centerX = 0.5;
                _centerY = 0.5;
                return;
            }

            var side = Math.Min(_orientedWidth, _orientedHeight) / _zoom;
            var half = side / 2.0;
            _centerX = Clamp(
                _centerX,
                half / _orientedWidth,
                1.0 - half / _orientedWidth);
            _centerY = Clamp(
                _centerY,
                half / _orientedHeight,
                1.0 - half / _orientedHeight);
        }

        private void RaiseParametersChanged()
        {
            var handler = ParametersChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }
}
