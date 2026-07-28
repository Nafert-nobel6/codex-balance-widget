using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexBalanceWidget.App.Infrastructure;
using CodexBalanceWidget.App.ViewModels;
using CodexBalanceWidget.App.Windows;
using Microsoft.Win32;

namespace CodexBalanceWidget.App
{
    public partial class WidgetWindow : Window
    {
        private const int GwlExStyle = -20;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExNoActivate = 0x08000000;
        private const int WmMouseActivate = 0x0021;
        private const int WmDisplayChange = 0x007E;
        private const int WmSettingChange = 0x001A;
        private const int WmDpiChanged = 0x02E0;
        private const int WmNcHitTest = 0x0084;
        private const int WmEnterSizeMove = 0x0231;
        private const int WmExitSizeMove = 0x0232;
        private const int MaNoActivate = 3;
        private const int HtLeft = 10;
        private const int HtRight = 11;
        private const int HtTop = 12;
        private const int HtTopLeft = 13;
        private const int HtTopRight = 14;
        private const int HtBottom = 15;
        private const int HtBottomLeft = 16;
        private const int HtBottomRight = 17;
        private const uint MonitorDefaultToNearest = 2;
        private const double ExpandedMinWidth = 240.0;
        private const double ExpandedMaxWidth = 300.0;
        private const double ExpandedMinHeight = 148.0;
        private const double ExpandedMaxHeight = 320.0;
        private const double BubbleWindowSize = 56.0;
        private const double RightEdgeGap = 24.0;
        private const double BubbleBottomGap = 48.0;
        private const double ExpandedDefaultBottomGap = 64.0;
        private const double ExpandedMinimumBottomGap = 48.0;
        private const double ExpandedMinimumRightGap = 12.0;
        private const double ExpandedMoveMinimumX = -96.0;
        private const double ExpandedMoveMaximumX = 12.0;
        private const double ExpandedMoveMinimumY = -96.0;
        private const double ExpandedMoveMaximumY = 16.0;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpNoOwnerZOrder = 0x0200;
        private static readonly IntPtr HwndBottom = new IntPtr(1);

        private readonly WidgetViewModel _viewModel;
        private readonly CodexProcessWindowMonitor _windowMonitor;
        private readonly DispatcherTimer _visibilityTimer;
        private readonly DispatcherTimer _clockTimer;
        private readonly DispatcherTimer _archiveTimer;
        private readonly DispatcherTimer _bubbleHoverTimer;
        private readonly DispatcherTimer _settingsCloseTimer;
        private readonly DispatcherTimer _quickLongPressTimer;
        private readonly DispatcherTimer _settingsSaveTimer;
        private readonly WidgetSettingsStore _settingsStore;
        private readonly AvatarImageService _avatarImageService;
        private readonly WidgetSettings _settings;

        private bool _codexRunning;
        private bool _hasBeenShown;
        private bool _isArchived;
        private bool _isTransitioning;
        private bool _isUserResizing;
        private bool _settingsLoaded;
        private bool _quickSliderCaptured;
        private bool _bubbleDragged;
        private bool _isExpandedDragging;
        private IntPtr _handle;
        private DateTime _lastInteractionUtc;
        private Rect _expandedBounds;
        private Point _bubbleDragStartScreen;
        private Point _bubbleDragStartWindow;
        private Point _expandedDragStartScreen;
        private Point _expandedDragStartOffset;
        private string _selectedTheme = WidgetThemes.Light;
        private double _backgroundOpacity = 0.94;
        private int _archiveSeconds = 30;
        private string _avatarPath = string.Empty;
        private double _savedBubbleTop = double.NaN;
        private double _expandedOffsetX;
        private double _expandedOffsetY;
        private LayoutMode _layoutMode;

        public WidgetWindow(
            WidgetViewModel viewModel,
            CodexProcessWindowMonitor windowMonitor,
            WidgetSettingsStore settingsStore,
            AvatarImageService avatarImageService,
            WidgetSettings settings)
        {
            if (viewModel == null) throw new ArgumentNullException("viewModel");
            if (windowMonitor == null) throw new ArgumentNullException("windowMonitor");
            if (settingsStore == null) throw new ArgumentNullException("settingsStore");
            if (avatarImageService == null) throw new ArgumentNullException("avatarImageService");
            if (settings == null) throw new ArgumentNullException("settings");

            _viewModel = viewModel;
            _windowMonitor = windowMonitor;
            _settingsStore = settingsStore;
            _avatarImageService = avatarImageService;
            _settings = settingsStore.Sanitize(settings);
            _selectedTheme = _settings.Theme;
            _backgroundOpacity = _settings.PanelOpacity;
            _archiveSeconds = (int)Math.Round(_settings.ArchiveDelay.TotalSeconds);
            _avatarPath = _settings.AvatarPath;
            _expandedOffsetX = _settings.WindowOffsetX;
            _expandedOffsetY = _settings.WindowOffsetY;
            _savedBubbleTop = _settings.BubbleY > 0.0
                ? _settings.BubbleY
                : double.NaN;
            DataContext = viewModel;
            InitializeComponent();

            Width = _settings.WindowWidth;
            Height = _settings.WindowHeight;
            SourceInitialized += OnSourceInitialized;
            Loaded += OnLoaded;

            _visibilityTimer = new DispatcherTimer(DispatcherPriority.Background);
            _visibilityTimer.Interval = TimeSpan.FromMilliseconds(200.0);
            _visibilityTimer.Tick += OnVisibilityTimerTick;
            _visibilityTimer.Start();

            _clockTimer = new DispatcherTimer(DispatcherPriority.Background);
            _clockTimer.Interval = TimeSpan.FromMinutes(1.0);
            _clockTimer.Tick += delegate { _viewModel.UpdateClock(); };
            _clockTimer.Start();

            _archiveTimer = new DispatcherTimer(DispatcherPriority.Background);
            _archiveTimer.Interval = TimeSpan.FromSeconds(1.0);
            _archiveTimer.Tick += OnArchiveTimerTick;
            _archiveTimer.Start();

            _bubbleHoverTimer = new DispatcherTimer(DispatcherPriority.Input);
            _bubbleHoverTimer.Interval = TimeSpan.FromMilliseconds(1500.0);
            _bubbleHoverTimer.Tick += OnBubbleHoverTimerTick;

            _settingsCloseTimer = new DispatcherTimer(DispatcherPriority.Input);
            _settingsCloseTimer.Interval = TimeSpan.FromMilliseconds(240.0);
            _settingsCloseTimer.Tick += OnSettingsCloseTimerTick;

            _quickLongPressTimer = new DispatcherTimer(DispatcherPriority.Input);
            _quickLongPressTimer.Interval = TimeSpan.FromMilliseconds(360.0);
            _quickLongPressTimer.Tick += OnQuickLongPressTimerTick;

            _settingsSaveTimer = new DispatcherTimer(DispatcherPriority.Background);
            _settingsSaveTimer.Interval = TimeSpan.FromMilliseconds(450.0);
            _settingsSaveTimer.Tick += OnSettingsSaveTimerTick;

            _lastInteractionUtc = DateTime.UtcNow;
            LoadSettings();
            ApplyTheme();
            ApplyAvatar();
            UpdateSelectionVisuals();
            PrepareInitialBubble();
            _settingsLoaded = true;
        }

        public void SetCodexRunning(bool running)
        {
            _codexRunning = running;
            if (!running)
            {
                HideWithoutChangingLifetime();
            }
            else
            {
                UpdateVisibility();
            }
        }

        public void StopTimers()
        {
            _visibilityTimer.Stop();
            _clockTimer.Stop();
            _archiveTimer.Stop();
            _bubbleHoverTimer.Stop();
            _settingsCloseTimer.Stop();
            _quickLongPressTimer.Stop();
            _settingsSaveTimer.Stop();
        }

        protected override void OnClosed(EventArgs e)
        {
            StopTimers();
            SaveSettings();
            base.OnClosed(e);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_isArchived)
            {
                ConstrainBubbleToAllowedArea();
            }
            else
            {
                ApplyResponsiveLayout();
                AnchorToPrimaryWorkArea();
            }
            UpdateQuickSliderPosition();
            KeepAtBottom();
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            _handle = new WindowInteropHelper(this).Handle;
            var extendedStyle = GetWindowLongPtr(_handle, GwlExStyle).ToInt64();
            extendedStyle |= WsExToolWindow;
            extendedStyle |= WsExNoActivate;
            SetWindowLongPtr(_handle, GwlExStyle, new IntPtr(extendedStyle));

            var source = HwndSource.FromHwnd(_handle);
            if (source != null)
            {
                source.AddHook(WindowProcedure);
            }
        }

        private IntPtr WindowProcedure(
            IntPtr hwnd,
            int message,
            IntPtr wParam,
            IntPtr lParam,
            ref bool handled)
        {
            if (message == WmMouseActivate)
            {
                handled = true;
                return new IntPtr(MaNoActivate);
            }

            if (message == WmNcHitTest && !_isArchived && !_isTransitioning)
            {
                var hit = HitTestResizeBorder(lParam);
                if (hit != 0)
                {
                    handled = true;
                    return new IntPtr(hit);
                }
            }

            if (message == WmEnterSizeMove)
            {
                _isUserResizing = true;
            }
            else if (message == WmExitSizeMove)
            {
                _isUserResizing = false;
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Loaded,
                    new Action(AnchorToPrimaryWorkArea));
            }

            if (message == WmDisplayChange ||
                message == WmSettingChange ||
                message == WmDpiChanged)
            {
                if (message == WmSettingChange &&
                    string.Equals(_selectedTheme, "System", StringComparison.Ordinal))
                {
                    Dispatcher.BeginInvoke(
                        DispatcherPriority.Background,
                        new Action(ApplyTheme));
                }

                Dispatcher.BeginInvoke(
                    DispatcherPriority.Loaded,
                    new Action(
                        delegate
                        {
                            if (!_isArchived)
                            {
                                AnchorToPrimaryWorkArea();
                            }
                            else
                            {
                                ConstrainBubbleToAllowedArea();
                            }
                        }));
            }

            return IntPtr.Zero;
        }

        private int HitTestResizeBorder(IntPtr lParam)
        {
            var screenX = unchecked((short)(long)lParam);
            var screenY = unchecked((short)((long)lParam >> 16));
            var local = PointFromScreen(new Point(screenX, screenY));
            const double edge = 7.0;
            var left = local.X >= 0.0 && local.X <= edge;
            var right = local.X >= ActualWidth - edge && local.X <= ActualWidth;
            var top = local.Y >= 0.0 && local.Y <= edge;
            var bottom = local.Y >= ActualHeight - edge && local.Y <= ActualHeight;

            if (top && left) return HtTopLeft;
            if (top && right) return HtTopRight;
            if (bottom && left) return HtBottomLeft;
            if (bottom && right) return HtBottomRight;
            if (left) return HtLeft;
            if (right) return HtRight;
            if (top) return HtTop;
            if (bottom) return HtBottom;
            return 0;
        }

        private void OnVisibilityTimerTick(object sender, EventArgs e)
        {
            UpdateVisibility();
        }

        private void UpdateVisibility()
        {
            if (!_codexRunning)
            {
                HideWithoutChangingLifetime();
                return;
            }

            if (_windowMonitor.IsAnyPrimaryCodexWindowMaximized())
            {
                HideWithoutChangingLifetime();
                return;
            }

            if (!_isArchived && !_isUserResizing && !_isTransitioning)
            {
                AnchorToPrimaryWorkArea();
            }

            ShowWithoutActivation();
            KeepAtBottom();
        }

        private void ShowWithoutActivation()
        {
            if (!_hasBeenShown)
            {
                _hasBeenShown = true;
                Show();
                if (!_isArchived)
                {
                    AnchorToPrimaryWorkArea();
                }
                KeepAtBottom();

                return;
            }

            if (!IsVisible)
            {
                Show();
                if (!_isArchived)
                {
                    AnchorToPrimaryWorkArea();
                }
                KeepAtBottom();
            }
        }

        private void HideWithoutChangingLifetime()
        {
            SettingsPopup.IsOpen = false;
            _bubbleHoverTimer.Stop();
            if (!_isArchived && !_isTransitioning)
            {
                CollapseToBubbleWithoutAnimation(true);
            }
            if (_hasBeenShown && IsVisible)
            {
                Hide();
            }
        }

        private void AnchorToPrimaryWorkArea()
        {
            if (_isArchived || _isTransitioning || _isUserResizing)
            {
                return;
            }

            var width = ActualWidth > 0.0 ? ActualWidth : Width;
            var height = ActualHeight > 0.0 ? ActualHeight : Height;
            var target = GetExpandedTargetBounds(width, height);
            var desiredLeft = Math.Round(target.Left);
            var desiredTop = Math.Round(target.Top);

            if (Math.Abs(Left - desiredLeft) > 0.5) Left = desiredLeft;
            if (Math.Abs(Top - desiredTop) > 0.5) Top = desiredTop;
            _expandedBounds = new Rect(desiredLeft, desiredTop, target.Width, target.Height);
        }

        private void KeepAtBottom()
        {
            if (_handle == IntPtr.Zero || !IsVisible)
            {
                return;
            }

            SetWindowPos(
                _handle,
                HwndBottom,
                0,
                0,
                0,
                0,
                SwpNoMove |
                SwpNoSize |
                SwpNoActivate |
                SwpNoOwnerZOrder);
        }

        private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_isArchived && !_isTransitioning)
            {
                ApplyResponsiveLayout();
                if (_settingsLoaded)
                {
                    ScheduleSaveSettings();
                }
            }

            UpdateQuickSliderPosition();
        }

        private void ApplyResponsiveLayout()
        {
            if (_isArchived)
            {
                return;
            }

            var width = ActualWidth > 0.0 ? ActualWidth : Width;
            var height = ActualHeight > 0.0 ? ActualHeight : Height;

            if (height <= 169.0 || width <= 224.0)
            {
                _layoutMode = LayoutMode.Minimal;
            }
            else if (height <= 218.0)
            {
                _layoutMode = LayoutMode.Horizontal;
            }
            else
            {
                _layoutMode = LayoutMode.Vertical;
            }

            VerticalLayout.Visibility =
                _layoutMode == LayoutMode.Vertical ? Visibility.Visible : Visibility.Collapsed;
            HorizontalLayout.Visibility =
                _layoutMode == LayoutMode.Horizontal ? Visibility.Visible : Visibility.Collapsed;
            MinimalLayout.Visibility =
                _layoutMode == LayoutMode.Minimal ? Visibility.Visible : Visibility.Collapsed;
            QuickSliderLane.Visibility =
                _layoutMode == LayoutMode.Minimal ? Visibility.Collapsed : Visibility.Visible;

            if (_layoutMode == LayoutMode.Minimal)
            {
                ExpandedShell.RowDefinitions[0].Height = new GridLength(30.0);
                ExpandedShell.RowDefinitions[2].Height = new GridLength(0.0);
                MainPanel.Padding = new Thickness(8.0, 7.0, 8.0, 7.0);
            }
            else
            {
                ExpandedShell.RowDefinitions[0].Height = new GridLength(34.0);
                ExpandedShell.RowDefinitions[2].Height = new GridLength(0.0);
                MainPanel.Padding = new Thickness(11.0, 10.0, 11.0, 7.0);
            }

            Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                new Action(UpdateQuickSliderPosition));
        }

        private void OnWindowPreviewMouseMove(object sender, MouseEventArgs e)
        {
            MarkInteraction();
        }

        private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            MarkInteraction();
        }

        private void MarkInteraction()
        {
            _lastInteractionUtc = DateTime.UtcNow;
        }

        private void OnExpandedDragMouseDown(
            object sender,
            MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left ||
                _isArchived ||
                _isTransitioning ||
                _isUserResizing)
            {
                return;
            }

            _isExpandedDragging = true;
            _expandedDragStartScreen = PointToScreen(e.GetPosition(this));
            _expandedDragStartOffset = new Point(
                _expandedOffsetX,
                _expandedOffsetY);
            ExpandedDragHandle.CaptureMouse();
            MarkInteraction();
            e.Handled = true;
        }

        private void OnExpandedDragMouseMove(
            object sender,
            MouseEventArgs e)
        {
            if (!_isExpandedDragging ||
                !ExpandedDragHandle.IsMouseCaptured ||
                e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            var currentScreen = PointToScreen(e.GetPosition(this));
            var deviceDelta = currentScreen - _expandedDragStartScreen;
            var logicalDelta = DeviceVectorToLogical(deviceDelta);
            _expandedOffsetX = Clamp(
                _expandedDragStartOffset.X + logicalDelta.X,
                ExpandedMoveMinimumX,
                ExpandedMoveMaximumX);
            _expandedOffsetY = Clamp(
                _expandedDragStartOffset.Y + logicalDelta.Y,
                ExpandedMoveMinimumY,
                ExpandedMoveMaximumY);
            AnchorToPrimaryWorkArea();
            MarkInteraction();
            e.Handled = true;
        }

        private void OnExpandedDragMouseUp(
            object sender,
            MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left ||
                !ExpandedDragHandle.IsMouseCaptured)
            {
                return;
            }

            ExpandedDragHandle.ReleaseMouseCapture();
            _isExpandedDragging = false;
            AnchorToPrimaryWorkArea();
            ScheduleSaveSettings();
            e.Handled = true;
        }

        private void OnExpandedDragLostCapture(
            object sender,
            MouseEventArgs e)
        {
            _isExpandedDragging = false;
        }

        private void PrepareInitialBubble()
        {
            var width = Clamp(
                _settings.WindowWidth,
                ExpandedMinWidth,
                ExpandedMaxWidth);
            var height = Clamp(
                _settings.WindowHeight,
                ExpandedMinHeight,
                ExpandedMaxHeight);
            _expandedBounds = GetExpandedTargetBounds(width, height);
            CollapseToBubbleWithoutAnimation(false);
        }

        private void CollapseToBubbleWithoutAnimation(bool captureExpandedBounds)
        {
            if (captureExpandedBounds)
            {
                var width = ActualWidth > 0.0 ? ActualWidth : Width;
                var height = ActualHeight > 0.0 ? ActualHeight : Height;
                _expandedBounds = new Rect(Left, Top, width, height);
            }

            _isArchived = true;
            _isTransitioning = false;
            _isUserResizing = false;
            MinWidth = 0.0;
            MinHeight = 0.0;
            MaxWidth = double.PositiveInfinity;
            MaxHeight = double.PositiveInfinity;
            ResizeMode = ResizeMode.NoResize;
            ResetTransitionVisuals();
            MainPanel.Visibility = Visibility.Collapsed;
            MainPanel.Opacity = 1.0;
            ArchiveBubbleSurface.Visibility = Visibility.Visible;
            ArchiveBubbleSurface.Opacity = 1.0;
            SettingsHandle.Visibility = Visibility.Collapsed;

            Rect target;
            if (_handle == IntPtr.Zero)
            {
                var workArea = SystemParameters.WorkArea;
                target = ConstrainBubbleBounds(
                    new Rect(
                        workArea.Right - BubbleWindowSize - RightEdgeGap,
                        double.IsNaN(_savedBubbleTop)
                            ? workArea.Bottom - BubbleWindowSize - BubbleBottomGap
                            : _savedBubbleTop,
                        BubbleWindowSize,
                        BubbleWindowSize),
                    workArea);
            }
            else
            {
                target = GetPreferredBubbleBounds();
            }

            Left = target.Left;
            Top = target.Top;
            Width = target.Width;
            Height = target.Height;
            _savedBubbleTop = Top;
        }

        private void OnArchiveTimerTick(object sender, EventArgs e)
        {
            if (_isArchived ||
                _isTransitioning ||
                SettingsPopup.IsOpen ||
                !_codexRunning ||
                !IsVisible)
            {
                return;
            }

            if (DateTime.UtcNow - _lastInteractionUtc >=
                TimeSpan.FromSeconds(_archiveSeconds))
            {
                BeginArchive();
            }
        }

        private void BeginArchive()
        {
            if (_isArchived || _isTransitioning || !IsVisible)
            {
                return;
            }

            SettingsPopup.IsOpen = false;
            _expandedBounds = new Rect(Left, Top, ActualWidth, ActualHeight);
            _isArchived = true;
            _isTransitioning = true;
            _isUserResizing = false;

            MinWidth = 0.0;
            MinHeight = 0.0;
            MaxWidth = double.PositiveInfinity;
            MaxHeight = double.PositiveInfinity;
            ResizeMode = ResizeMode.NoResize;

            ArchiveBubbleSurface.Visibility = Visibility.Visible;
            ArchiveBubbleSurface.Opacity = 0.0;
            SettingsHandle.Visibility = Visibility.Collapsed;

            var width = Math.Max(ExpandedMinWidth, ActualWidth);
            var height = Math.Max(ExpandedMinHeight, ActualHeight);
            var targetScaleX = Clamp(BubbleWindowSize / width, 0.12, 0.34);
            var targetScaleY = Clamp(BubbleWindowSize / height, 0.12, 0.40);
            var mainScale = new ScaleTransform(1.0, 1.0);
            MainPanel.RenderTransformOrigin = new Point(1.0, 1.0);
            MainPanel.RenderTransform = mainScale;

            var bubbleScale = new ScaleTransform(0.86, 0.86);
            ArchiveBubble.RenderTransformOrigin = new Point(0.5, 0.5);
            ArchiveBubble.RenderTransform = bubbleScale;
            var sharedEase = new CubicEase { EasingMode = EasingMode.EaseInOut };
            AnimateDouble(
                mainScale,
                ScaleTransform.ScaleXProperty,
                1.0,
                targetScaleX,
                360.0,
                sharedEase,
                CompleteArchive);
            AnimateDouble(
                mainScale,
                ScaleTransform.ScaleYProperty,
                1.0,
                targetScaleY,
                360.0,
                sharedEase);
            AnimateDouble(bubbleScale, ScaleTransform.ScaleXProperty, 0.86, 1.0, 320.0, sharedEase);
            AnimateDouble(bubbleScale, ScaleTransform.ScaleYProperty, 0.86, 1.0, 320.0, sharedEase);
            AnimateDouble(ArchiveBubbleSurface, OpacityProperty, 0.0, 1.0, 300.0, sharedEase);
            AnimateDouble(MainPanel, OpacityProperty, 1.0, 0.0, 300.0, sharedEase);
        }

        private void CompleteArchive()
        {
            ResetTransitionVisuals();
            var target = GetPreferredBubbleBounds();
            Left = target.Left;
            Top = target.Top;
            Width = target.Width;
            Height = target.Height;
            MainPanel.Visibility = Visibility.Collapsed;
            MainPanel.Opacity = 1.0;
            ArchiveBubbleSurface.Opacity = 1.0;
            _isTransitioning = false;
            _savedBubbleTop = Top;
            KeepAtBottom();
            ScheduleSaveSettings();
        }

        private void BeginRestore()
        {
            if (!_isArchived || _isTransitioning)
            {
                return;
            }

            _bubbleHoverTimer.Stop();
            _isTransitioning = true;
            var target = ClampExpandedBounds(_expandedBounds);
            ApplyResponsiveLayoutForSize(target.Width, target.Height);
            Left = target.Left;
            Top = target.Top;
            Width = target.Width;
            Height = target.Height;
            MainPanel.Visibility = Visibility.Visible;
            MainPanel.Opacity = 0.0;
            SettingsHandle.Visibility = Visibility.Collapsed;

            var startScaleX = Clamp(
                BubbleWindowSize / Math.Max(ExpandedMinWidth, target.Width),
                0.12,
                0.34);
            var startScaleY = Clamp(
                BubbleWindowSize / Math.Max(ExpandedMinHeight, target.Height),
                0.12,
                0.40);
            var mainScale = new ScaleTransform(startScaleX, startScaleY);
            MainPanel.RenderTransformOrigin = new Point(1.0, 1.0);
            MainPanel.RenderTransform = mainScale;
            var bubbleScale = new ScaleTransform(1.0, 1.0);
            ArchiveBubble.RenderTransformOrigin = new Point(0.5, 0.5);
            ArchiveBubble.RenderTransform = bubbleScale;

            var sharedEase = new CubicEase { EasingMode = EasingMode.EaseInOut };
            AnimateDouble(
                mainScale,
                ScaleTransform.ScaleXProperty,
                startScaleX,
                1.0,
                380.0,
                sharedEase,
                CompleteRestore);
            AnimateDouble(
                mainScale,
                ScaleTransform.ScaleYProperty,
                startScaleY,
                1.0,
                380.0,
                sharedEase);
            AnimateDouble(MainPanel, OpacityProperty, 0.0, 1.0, 330.0, sharedEase);
            AnimateDouble(bubbleScale, ScaleTransform.ScaleXProperty, 1.0, 1.08, 280.0, sharedEase);
            AnimateDouble(bubbleScale, ScaleTransform.ScaleYProperty, 1.0, 1.08, 280.0, sharedEase);
            AnimateDouble(ArchiveBubbleSurface, OpacityProperty, 1.0, 0.0, 280.0, sharedEase);
            KeepAtBottom();
        }

        private void CompleteRestore()
        {
            ResetTransitionVisuals();
            var target = ClampExpandedBounds(_expandedBounds);
            Left = target.Left;
            Top = target.Top;
            Width = target.Width;
            Height = target.Height;
            MinWidth = ExpandedMinWidth;
            MaxWidth = ExpandedMaxWidth;
            MinHeight = ExpandedMinHeight;
            MaxHeight = ExpandedMaxHeight;
            ResizeMode = ResizeMode.CanResize;
            _isArchived = false;
            _isTransitioning = false;
            ArchiveBubbleSurface.Visibility = Visibility.Collapsed;
            ArchiveBubbleSurface.Opacity = 0.0;
            SettingsHandle.Visibility = Visibility.Visible;
            MainPanel.Opacity = 1.0;
            ApplyResponsiveLayout();
            MarkInteraction();
            KeepAtBottom();
        }

        private void ApplyResponsiveLayoutForSize(double width, double height)
        {
            var oldWidth = Width;
            var oldHeight = Height;
            var wasArchived = _isArchived;
            try
            {
                _isArchived = false;
                Width = width;
                Height = height;
                ApplyResponsiveLayout();
            }
            finally
            {
                Width = oldWidth;
                Height = oldHeight;
                _isArchived = wasArchived;
            }
        }

        private Rect GetPreferredBubbleBounds()
        {
            var screenPoint = PointToScreen(
                new Point(Math.Max(0.0, ActualWidth / 2.0), Math.Max(0.0, ActualHeight / 2.0)));
            var workArea = GetMonitorWorkAreaInDips(screenPoint);
            var left = workArea.Right - BubbleWindowSize - RightEdgeGap;
            var top = double.IsNaN(_savedBubbleTop)
                ? workArea.Bottom - BubbleWindowSize - BubbleBottomGap
                : _savedBubbleTop;
            return ConstrainBubbleBounds(
                new Rect(left, top, BubbleWindowSize, BubbleWindowSize),
                workArea);
        }

        private Rect ClampExpandedBounds(Rect bounds)
        {
            var width = Clamp(
                bounds.Width > 0.0 ? bounds.Width : _settings.WindowWidth,
                ExpandedMinWidth,
                ExpandedMaxWidth);
            var height = Clamp(
                bounds.Height > 0.0 ? bounds.Height : _settings.WindowHeight,
                ExpandedMinHeight,
                ExpandedMaxHeight);
            return GetExpandedTargetBounds(width, height);
        }

        private Rect GetExpandedTargetBounds(double width, double height)
        {
            var work = GetActiveMonitorWorkArea();
            width = Clamp(width, ExpandedMinWidth, ExpandedMaxWidth);
            height = Clamp(height, ExpandedMinHeight, ExpandedMaxHeight);
            var baseLeft = work.Right - width - RightEdgeGap;
            var baseTop = work.Bottom - height - ExpandedDefaultBottomGap;
            var left = Clamp(
                baseLeft + _expandedOffsetX,
                work.Left,
                work.Right - width - ExpandedMinimumRightGap);
            var top = Clamp(
                baseTop + _expandedOffsetY,
                work.Top,
                work.Bottom - height - ExpandedMinimumBottomGap);
            return new Rect(left, top, width, height);
        }

        private Rect GetActiveMonitorWorkArea()
        {
            if (_handle == IntPtr.Zero ||
                PresentationSource.FromVisual(this) == null)
            {
                return SystemParameters.WorkArea;
            }

            try
            {
                var center = PointToScreen(
                    new Point(
                        Math.Max(0.0, ActualWidth / 2.0),
                        Math.Max(0.0, ActualHeight / 2.0)));
                return GetMonitorWorkAreaInDips(center);
            }
            catch
            {
                return SystemParameters.WorkArea;
            }
        }

        private void ResetTransitionVisuals()
        {
            MainPanel.BeginAnimation(OpacityProperty, null);
            ArchiveBubbleSurface.BeginAnimation(OpacityProperty, null);
            MainPanel.RenderTransform = Transform.Identity;
            ArchiveBubble.RenderTransform = Transform.Identity;
        }

        private static void AnimateDouble(
            IAnimatable target,
            DependencyProperty property,
            double from,
            double to,
            double milliseconds,
            IEasingFunction easing)
        {
            AnimateDouble(
                target,
                property,
                from,
                to,
                milliseconds,
                easing,
                null);
        }

        private static void AnimateDouble(
            IAnimatable target,
            DependencyProperty property,
            double from,
            double to,
            double milliseconds,
            IEasingFunction easing,
            Action completed)
        {
            var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds));
            animation.FillBehavior = FillBehavior.HoldEnd;
            animation.EasingFunction = easing;
            if (completed != null)
            {
                animation.Completed += delegate { completed(); };
            }

            target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        }

        private void OnBubbleMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || !_isArchived || _isTransitioning)
            {
                return;
            }

            _bubbleHoverTimer.Stop();
            _bubbleDragged = false;
            _bubbleDragStartScreen = PointToScreen(e.GetPosition(this));
            _bubbleDragStartWindow = new Point(Left, Top);
            ArchiveBubbleSurface.CaptureMouse();
            e.Handled = true;
        }

        private void OnBubbleMouseMove(object sender, MouseEventArgs e)
        {
            if (!ArchiveBubbleSurface.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            var currentScreen = PointToScreen(e.GetPosition(this));
            var deviceDelta = currentScreen - _bubbleDragStartScreen;
            var logicalDelta = DeviceVectorToLogical(deviceDelta);
            if (Math.Abs(logicalDelta.X) > 3.0 || Math.Abs(logicalDelta.Y) > 3.0)
            {
                _bubbleDragged = true;
            }

            if (!_bubbleDragged)
            {
                return;
            }

            var workArea = GetMonitorWorkAreaInDips(currentScreen);
            var desired = new Rect(
                workArea.Right - ActualWidth - RightEdgeGap,
                _bubbleDragStartWindow.Y + logicalDelta.Y,
                ActualWidth,
                ActualHeight);
            desired = ConstrainBubbleBounds(desired, workArea);
            Left = desired.Left;
            Top = desired.Top;
            e.Handled = true;
        }

        private void OnBubbleMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || !ArchiveBubbleSurface.IsMouseCaptured)
            {
                return;
            }

            ArchiveBubbleSurface.ReleaseMouseCapture();
            if (_bubbleDragged)
            {
                _savedBubbleTop = Top;
                ScheduleSaveSettings();
            }
            else
            {
                BeginRestore();
            }

            e.Handled = true;
        }

        private void OnBubbleMouseEnter(object sender, MouseEventArgs e)
        {
            if (!_isArchived || _isTransitioning ||
                ArchiveBubbleSurface.IsMouseCaptured)
            {
                return;
            }

            _bubbleHoverTimer.Stop();
            _bubbleHoverTimer.Start();
        }

        private void OnBubbleMouseLeave(object sender, MouseEventArgs e)
        {
            if (!ArchiveBubbleSurface.IsMouseCaptured)
            {
                _bubbleHoverTimer.Stop();
            }
        }

        private void OnBubbleHoverTimerTick(object sender, EventArgs e)
        {
            _bubbleHoverTimer.Stop();
            if (_isArchived &&
                !_isTransitioning &&
                ArchiveBubbleSurface.IsMouseOver)
            {
                BeginRestore();
            }
        }

        private void ConstrainBubbleToAllowedArea()
        {
            if (!_isArchived || !IsVisible)
            {
                return;
            }

            var center = PointToScreen(new Point(ActualWidth / 2.0, ActualHeight / 2.0));
            var constrained = ConstrainBubbleBounds(
                new Rect(Left, Top, ActualWidth, ActualHeight),
                GetMonitorWorkAreaInDips(center));
            Left = constrained.Left;
            Top = constrained.Top;
        }

        private static Rect ConstrainBubbleBounds(Rect desired, Rect workArea)
        {
            var minTop = workArea.Top + workArea.Height / 2.0;
            var fixedLeft = workArea.Right - desired.Width - RightEdgeGap;
            var maxTop = Math.Max(
                minTop,
                workArea.Bottom - desired.Height - BubbleBottomGap);
            return new Rect(
                fixedLeft,
                Clamp(desired.Top, minTop, maxTop),
                desired.Width,
                desired.Height);
        }

        private Vector DeviceVectorToLogical(Vector deviceVector)
        {
            var source = PresentationSource.FromVisual(this);
            if (source == null || source.CompositionTarget == null)
            {
                return deviceVector;
            }

            return source.CompositionTarget.TransformFromDevice.Transform(deviceVector);
        }

        private Rect GetMonitorWorkAreaInDips(Point devicePoint)
        {
            var point = new NativePoint(
                (int)Math.Round(devicePoint.X),
                (int)Math.Round(devicePoint.Y));
            var monitor = MonitorFromPoint(point, MonitorDefaultToNearest);
            var info = new MonitorInformation();
            info.Size = Marshal.SizeOf(typeof(MonitorInformation));
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            {
                return SystemParameters.WorkArea;
            }

            var topLeft = new Point(info.Work.Left, info.Work.Top);
            var bottomRight = new Point(info.Work.Right, info.Work.Bottom);
            var source = PresentationSource.FromVisual(this);
            if (source != null && source.CompositionTarget != null)
            {
                topLeft = source.CompositionTarget.TransformFromDevice.Transform(topLeft);
                bottomRight = source.CompositionTarget.TransformFromDevice.Transform(bottomRight);
            }

            return new Rect(topLeft, bottomRight);
        }

        private void OnSettingsHandleMouseEnter(object sender, MouseEventArgs e)
        {
            MarkInteraction();
            _settingsCloseTimer.Stop();
            SettingsPopup.IsOpen = true;
            AnimateSettingsTriangle(180.0);
        }

        private void OnSettingsPanelMouseEnter(object sender, MouseEventArgs e)
        {
            MarkInteraction();
            _settingsCloseTimer.Stop();
        }

        private void OnSettingsAreaMouseLeave(object sender, MouseEventArgs e)
        {
            _settingsCloseTimer.Stop();
            _settingsCloseTimer.Start();
        }

        private void OnSettingsCloseTimerTick(object sender, EventArgs e)
        {
            _settingsCloseTimer.Stop();
            if (SettingsHandle.IsMouseOver || SettingsPanel.IsMouseOver)
            {
                return;
            }

            SettingsPopup.IsOpen = false;
        }

        private void OnSettingsHandleClick(object sender, MouseButtonEventArgs e)
        {
            SettingsPopup.IsOpen = !SettingsPopup.IsOpen;
            AnimateSettingsTriangle(SettingsPopup.IsOpen ? 180.0 : 0.0);
            MarkInteraction();
            e.Handled = true;
        }

        private void OnSettingsPopupClosed(object sender, EventArgs e)
        {
            AnimateSettingsTriangle(0.0);
        }

        private void AnimateSettingsTriangle(double angle)
        {
            var animation = new DoubleAnimation(
                SettingsTriangleRotation.Angle,
                angle,
                TimeSpan.FromMilliseconds(160.0));
            animation.EasingFunction = new QuadraticEase
            {
                EasingMode = EasingMode.EaseOut
            };
            SettingsTriangleRotation.BeginAnimation(
                RotateTransform.AngleProperty,
                animation,
                HandoffBehavior.SnapshotAndReplace);
        }

        private void OnThemeToggleClick(object sender, MouseButtonEventArgs e)
        {
            _selectedTheme = string.Equals(
                _selectedTheme,
                WidgetThemes.Dark,
                StringComparison.OrdinalIgnoreCase)
                ? WidgetThemes.Light
                : WidgetThemes.Dark;
            ApplyTheme();
            UpdateSelectionVisuals();
            ScheduleSaveSettings();
            MarkInteraction();
            e.Handled = true;
        }

        private void OnBackgroundOpacityChanged(
            object sender,
            RoutedPropertyChangedEventArgs<double> e)
        {
            _backgroundOpacity = Clamp(e.NewValue / 100.0, 0.70, 1.0);
            if (OpacityValueText != null)
            {
                OpacityValueText.Text =
                    Math.Round(_backgroundOpacity * 100.0).ToString("0") + "%";
            }

            if (_settingsLoaded)
            {
                ApplyTheme();
                ScheduleSaveSettings();
            }
        }

        private void OnArchiveDelayClick(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            int value;
            if (button == null ||
                button.Tag == null ||
                !int.TryParse(
                    Convert.ToString(button.Tag, CultureInfo.InvariantCulture),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out value))
            {
                return;
            }

            if (value != 10 && value != 15 && value != 30 && value != 60)
            {
                return;
            }

            _archiveSeconds = value;
            MarkInteraction();
            UpdateSelectionVisuals();
            ScheduleSaveSettings();
        }

        private void OnChangeAvatarClick(object sender, RoutedEventArgs e)
        {
            MarkInteraction();
            var dialog = new OpenFileDialog();
            dialog.Title = "选择头像";
            dialog.Filter = "PNG 或 JPEG 图片|*.png;*.jpg;*.jpeg";
            dialog.CheckFileExists = true;
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            try
            {
                var cropWindow = new AvatarCropWindow(
                    _avatarImageService,
                    dialog.FileName);
                cropWindow.Owner = this;
                if (cropWindow.ShowDialog() == true)
                {
                    _avatarPath = cropWindow.AvatarPath;
                    ApplyAvatar();
                    ScheduleSaveSettings();
                }
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    this,
                    "头像未能更新：" + exception.Message,
                    "Codex 额度",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void ApplyAvatar()
        {
            ImageSource source = null;
            if (!string.IsNullOrWhiteSpace(_avatarPath) && File.Exists(_avatarPath))
            {
                try
                {
                    using (var stream = new FileStream(
                        _avatarPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete))
                    {
                        var decoder = BitmapDecoder.Create(
                            stream,
                            BitmapCreateOptions.PreservePixelFormat |
                            BitmapCreateOptions.IgnoreImageCache,
                            BitmapCacheOption.OnLoad);
                        if (decoder.Frames.Count == 1)
                        {
                            var frame = decoder.Frames[0];
                            frame.Freeze();
                            source = frame;
                        }
                    }
                }
                catch
                {
                    source = null;
                }
            }

            ExpandedAvatarImage.Source = source;
            SettingsAvatarImage.Source = source;
            ArchiveBubble.AvatarSource = source;
            var fallbackVisibility =
                source == null ? Visibility.Visible : Visibility.Collapsed;
            ExpandedAvatarFallback.Visibility = fallbackVisibility;
            SettingsAvatarFallback.Visibility = fallbackVisibility;
        }

        private void ApplyTheme()
        {
            var isLight = string.Equals(
                _selectedTheme,
                "Light",
                StringComparison.OrdinalIgnoreCase);
            if (string.Equals(
                _selectedTheme,
                "System",
                StringComparison.OrdinalIgnoreCase))
            {
                isLight = IsSystemLightTheme();
            }

            var resources = Application.Current.Resources;
            if (isLight)
            {
                resources["PanelBrush"] = MakeBrush(248, 250, 252, _backgroundOpacity);
                resources["CardBrush"] = MakeBrush(235, 239, 244, 0.97);
                resources["CardHoverBrush"] = MakeBrush(224, 230, 237, 1.0);
                resources["TextBrush"] = MakeBrush(27, 33, 42, 1.0);
                resources["MutedTextBrush"] = MakeBrush(86, 96, 109, 1.0);
                resources["SubtleTextBrush"] = MakeBrush(116, 125, 137, 1.0);
                resources["AccentBrush"] = MakeBrush(18, 151, 103, 1.0);
                resources["WarningBrush"] = MakeBrush(190, 113, 15, 1.0);
                resources["DividerBrush"] = MakeBrush(205, 212, 221, 0.95);
            }
            else
            {
                resources["PanelBrush"] = MakeBrush(26, 28, 34, _backgroundOpacity);
                resources["CardBrush"] = MakeBrush(36, 39, 47, 0.98);
                resources["CardHoverBrush"] = MakeBrush(43, 46, 56, 1.0);
                resources["TextBrush"] = MakeBrush(245, 246, 248, 1.0);
                resources["MutedTextBrush"] = MakeBrush(158, 164, 177, 1.0);
                resources["SubtleTextBrush"] = MakeBrush(115, 121, 134, 1.0);
                resources["AccentBrush"] = MakeBrush(112, 225, 178, 1.0);
                resources["WarningBrush"] = MakeBrush(255, 198, 109, 1.0);
                resources["DividerBrush"] = MakeBrush(48, 51, 61, 0.98);
            }

            UpdateSelectionVisuals();
        }

        private static bool IsSystemLightTheme()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(
                    "Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize"))
                {
                    var value = key == null ? null : key.GetValue("AppsUseLightTheme");
                    return value == null ||
                           Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private void UpdateSelectionVisuals()
        {
            if (ThemeToggleThumb == null)
            {
                return;
            }

            var isDark = string.Equals(
                _selectedTheme,
                WidgetThemes.Dark,
                StringComparison.OrdinalIgnoreCase);
            var toggleAnimation = new DoubleAnimation(
                ThemeToggleTranslate.X,
                isDark ? 48.0 : 0.0,
                TimeSpan.FromMilliseconds(180.0))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            ThemeToggleTranslate.BeginAnimation(
                TranslateTransform.XProperty,
                toggleAnimation,
                HandoffBehavior.SnapshotAndReplace);
            ThemeToggleTranslate.X = isDark ? 48.0 : 0.0;
            ThemeToggleThumb.Background = isDark
                ? MakeBrush(29, 32, 39, 1.0)
                : MakeBrush(255, 255, 255, 1.0);
            LightThemeLabel.Foreground = isDark
                ? MakeBrush(132, 138, 149, 1.0)
                : MakeBrush(32, 36, 43, 1.0);
            DarkThemeLabel.Foreground = isDark
                ? MakeBrush(247, 248, 250, 1.0)
                : MakeBrush(91, 100, 112, 1.0);

            foreach (var child in FindVisualChildren<Button>(SettingsPanel))
            {
                int value;
                var tag = child.Tag == null
                    ? string.Empty
                    : Convert.ToString(child.Tag, CultureInfo.InvariantCulture);
                if (int.TryParse(tag, out value))
                {
                    UpdateButtonSelection(child, value == _archiveSeconds);
                }
            }
        }

        private static void UpdateButtonSelection(Button button, bool selected)
        {
            if (selected)
            {
                var accent = Application.Current.Resources["AccentBrush"] as SolidColorBrush;
                button.BorderBrush = accent;
                if (accent != null)
                {
                    var color = accent.Color;
                    button.Background =
                        new SolidColorBrush(Color.FromArgb(44, color.R, color.G, color.B));
                }
            }
            else
            {
                button.ClearValue(Control.BackgroundProperty);
                button.SetResourceReference(
                    Control.BorderBrushProperty,
                    "DividerBrush");
            }
        }

        private static System.Collections.Generic.IEnumerable<T> FindVisualChildren<T>(
            DependencyObject root)
            where T : DependencyObject
        {
            if (root == null)
            {
                yield break;
            }

            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var index = 0; index < count; index++)
            {
                var child = VisualTreeHelper.GetChild(root, index);
                var typed = child as T;
                if (typed != null)
                {
                    yield return typed;
                }

                foreach (var descendant in FindVisualChildren<T>(child))
                {
                    yield return descendant;
                }
            }
        }

        private void LoadSettings()
        {
            BackgroundOpacitySlider.Value = _backgroundOpacity * 100.0;
            OpacityValueText.Text =
                Math.Round(_backgroundOpacity * 100.0).ToString("0") + "%";
        }

        private void ScheduleSaveSettings()
        {
            if (!_settingsLoaded)
            {
                return;
            }

            _settingsSaveTimer.Stop();
            _settingsSaveTimer.Start();
        }

        private void OnSettingsSaveTimerTick(object sender, EventArgs e)
        {
            _settingsSaveTimer.Stop();
            SaveSettings();
        }

        private void SaveSettings()
        {
            try
            {
                _settings.Theme = _selectedTheme;
                _settings.PanelOpacity = _backgroundOpacity;
                _settings.ArchiveDelay = TimeSpan.FromSeconds(_archiveSeconds);
                var currentWidth = _isArchived
                    ? _expandedBounds.Width
                    : (ActualWidth > 0.0 ? ActualWidth : Width);
                var currentHeight = _isArchived
                    ? _expandedBounds.Height
                    : (ActualHeight > 0.0 ? ActualHeight : Height);
                _settings.WindowWidth = Clamp(
                    currentWidth > 0.0 ? currentWidth : _settings.WindowWidth,
                    ExpandedMinWidth,
                    ExpandedMaxWidth);
                _settings.WindowHeight = Clamp(
                    currentHeight > 0.0 ? currentHeight : _settings.WindowHeight,
                    ExpandedMinHeight,
                    ExpandedMaxHeight);
                _settings.WindowOffsetX = _expandedOffsetX;
                _settings.WindowOffsetY = _expandedOffsetY;
                _settings.BubbleY = double.IsNaN(_savedBubbleTop)
                    ? 0.0
                    : Math.Max(0.0, _savedBubbleTop);
                _settings.AvatarPath = _avatarPath ?? string.Empty;
                _settingsStore.Save(_settings);
            }
            catch
            {
            }
        }

        private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var viewer = GetActiveCreditViewer();
            if (viewer == null || viewer.ScrollableHeight <= 0.0)
            {
                return;
            }

            viewer.ScrollToVerticalOffset(viewer.VerticalOffset - (e.Delta / 3.0));
            e.Handled = true;
            MarkInteraction();
        }

        private ScrollViewer GetActiveCreditViewer()
        {
            if (_layoutMode == LayoutMode.Vertical)
            {
                return CreditScrollViewer;
            }

            if (_layoutMode == LayoutMode.Horizontal)
            {
                return HorizontalCreditScrollViewer;
            }

            return null;
        }

        private void OnCreditScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            UpdateQuickSliderPosition();
        }

        private void UpdateQuickSliderPosition()
        {
            if (QuickSliderLane == null || QuickSlider == null)
            {
                return;
            }

            var viewer = GetActiveCreditViewer();
            var travel = Math.Max(0.0, QuickSliderLane.ActualHeight - QuickSlider.ActualHeight);
            var ratio = viewer == null || viewer.ScrollableHeight <= 0.0
                ? 0.0
                : Clamp(viewer.VerticalOffset / viewer.ScrollableHeight, 0.0, 1.0);
            Canvas.SetTop(QuickSlider, travel * ratio);
            Canvas.SetLeft(QuickSlider, Math.Max(0.0, QuickSliderLane.ActualWidth - QuickSlider.Width));
        }

        private void OnQuickSliderMouseEnter(object sender, MouseEventArgs e)
        {
            SetQuickSliderVisual(7.0, 0.58);
            MarkInteraction();
        }

        private void OnQuickSliderMouseLeave(object sender, MouseEventArgs e)
        {
            if (!_quickSliderCaptured)
            {
                SetQuickSliderVisual(3.0, 0.20);
            }
        }

        private void OnQuickSliderMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left)
            {
                return;
            }

            _quickSliderCaptured = true;
            QuickSlider.CaptureMouse();
            _quickLongPressTimer.Stop();
            _quickLongPressTimer.Start();
            MoveQuickSliderToPointer(e.GetPosition(QuickSliderLane).Y);
            MarkInteraction();
            e.Handled = true;
        }

        private void OnQuickSliderMouseMove(object sender, MouseEventArgs e)
        {
            if (!_quickSliderCaptured || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            MoveQuickSliderToPointer(e.GetPosition(QuickSliderLane).Y);
            e.Handled = true;
        }

        private void OnQuickSliderMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_quickSliderCaptured || e.ChangedButton != MouseButton.Left)
            {
                return;
            }

            _quickSliderCaptured = false;
            _quickLongPressTimer.Stop();
            QuickSlider.ReleaseMouseCapture();
            SetQuickSliderVisual(QuickSlider.IsMouseOver ? 7.0 : 3.0,
                QuickSlider.IsMouseOver ? 0.58 : 0.20);
            e.Handled = true;
        }

        private void OnQuickLongPressTimerTick(object sender, EventArgs e)
        {
            _quickLongPressTimer.Stop();
            if (_quickSliderCaptured)
            {
                SetQuickSliderVisual(9.0, 0.80);
            }
        }

        private void SetQuickSliderVisual(double width, double opacity)
        {
            var widthAnimation = new DoubleAnimation(
                QuickSlider.Width,
                width,
                TimeSpan.FromMilliseconds(120.0));
            var opacityAnimation = new DoubleAnimation(
                QuickSlider.Opacity,
                opacity,
                TimeSpan.FromMilliseconds(120.0));
            QuickSlider.BeginAnimation(WidthProperty, widthAnimation);
            QuickSlider.BeginAnimation(OpacityProperty, opacityAnimation);
            QuickSlider.Width = width;
            QuickSlider.Opacity = opacity;
            UpdateQuickSliderPosition();
        }

        private void MoveQuickSliderToPointer(double pointerY)
        {
            var viewer = GetActiveCreditViewer();
            if (viewer == null || viewer.ScrollableHeight <= 0.0)
            {
                return;
            }

            var travel = Math.Max(1.0, QuickSliderLane.ActualHeight - QuickSlider.ActualHeight);
            var ratio = Clamp((pointerY - QuickSlider.ActualHeight / 2.0) / travel, 0.0, 1.0);
            viewer.ScrollToVerticalOffset(viewer.ScrollableHeight * ratio);
            UpdateQuickSliderPosition();
        }

        private static SolidColorBrush MakeBrush(byte r, byte g, byte b, double opacity)
        {
            var alpha = (byte)Math.Round(Clamp(opacity, 0.0, 1.0) * 255.0);
            var brush = new SolidColorBrush(Color.FromArgb(alpha, r, g, b));
            brush.Freeze();
            return brush;
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index)
        {
            return IntPtr.Size == 8
                ? GetWindowLongPtr64(hwnd, index)
                : new IntPtr(GetWindowLong32(hwnd, index));
        }

        private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value)
        {
            return IntPtr.Size == 8
                ? SetWindowLongPtr64(hwnd, index, value)
                : new IntPtr(SetWindowLong32(hwnd, index, value.ToInt32()));
        }

        private enum LayoutMode
        {
            Vertical,
            Horizontal,
            Minimal
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public NativePoint(int x, int y)
            {
                X = x;
                Y = y;
            }

            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRectangle
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInformation
        {
            public int Size;
            public NativeRectangle Monitor;
            public NativeRectangle Work;
            public int Flags;
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong32(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(
            NativePoint point,
            uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(
            IntPtr monitor,
            ref MonitorInformation information);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);
    }
}

namespace CodexBalanceWidget.App.Controls
{
    public sealed class AdaptiveQuotaRing : FrameworkElement
    {
        public static readonly DependencyProperty RemainingPercentProperty =
            DependencyProperty.Register(
                "RemainingPercent",
                typeof(double),
                typeof(AdaptiveQuotaRing),
                new FrameworkPropertyMetadata(
                    double.NaN,
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty HeadingProperty =
            DependencyProperty.Register(
                "Heading",
                typeof(string),
                typeof(AdaptiveQuotaRing),
                new FrameworkPropertyMetadata(
                    string.Empty,
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty DetailProperty =
            DependencyProperty.Register(
                "Detail",
                typeof(string),
                typeof(AdaptiveQuotaRing),
                new FrameworkPropertyMetadata(
                    string.Empty,
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty AccentProperty =
            DependencyProperty.Register(
                "Accent",
                typeof(Brush),
                typeof(AdaptiveQuotaRing),
                new FrameworkPropertyMetadata(
                    Brushes.MediumAquamarine,
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty TrackProperty =
            DependencyProperty.Register(
                "Track",
                typeof(Brush),
                typeof(AdaptiveQuotaRing),
                new FrameworkPropertyMetadata(
                    Brushes.DimGray,
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty TextBrushProperty =
            DependencyProperty.Register(
                "TextBrush",
                typeof(Brush),
                typeof(AdaptiveQuotaRing),
                new FrameworkPropertyMetadata(
                    Brushes.White,
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty MutedBrushProperty =
            DependencyProperty.Register(
                "MutedBrush",
                typeof(Brush),
                typeof(AdaptiveQuotaRing),
                new FrameworkPropertyMetadata(
                    Brushes.Gray,
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

        public Brush Track
        {
            get { return (Brush)GetValue(TrackProperty); }
            set { SetValue(TrackProperty, value); }
        }

        public Brush TextBrush
        {
            get { return (Brush)GetValue(TextBrushProperty); }
            set { SetValue(TextBrushProperty, value); }
        }

        public Brush MutedBrush
        {
            get { return (Brush)GetValue(MutedBrushProperty); }
            set { SetValue(MutedBrushProperty, value); }
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            var width = Math.Max(1.0, ActualWidth);
            var height = Math.Max(1.0, ActualHeight);
            var showDetail = height >= 84.0 && !string.IsNullOrWhiteSpace(Detail);
            var labelHeight = showDetail ? 29.0 : 18.0;
            var diameter = Math.Max(
                28.0,
                Math.Min(width - 8.0, height - labelHeight - 4.0));
            var stroke = Clamp(diameter * 0.085, 4.0, 7.0);
            var radius = Math.Max(8.0, diameter / 2.0 - stroke / 2.0);
            var center = new Point(width / 2.0, 2.0 + diameter / 2.0);
            var available = !double.IsNaN(RemainingPercent) &&
                            !double.IsInfinity(RemainingPercent);
            var value = available
                ? Clamp(RemainingPercent, 0.0, 100.0)
                : 0.0;

            var trackPen = new Pen(Track, stroke);
            drawingContext.DrawEllipse(null, trackPen, center, radius, radius);
            DrawProgressArc(
                drawingContext,
                center,
                radius,
                stroke,
                value,
                available,
                Accent);

            DrawCenteredText(
                drawingContext,
                available ? value.ToString("0", CultureInfo.InvariantCulture) + "%" : "—",
                Clamp(diameter * 0.25, 11.0, 20.0),
                FontWeights.SemiBold,
                available ? TextBrush : MutedBrush,
                center.Y - Clamp(diameter * 0.15, 7.0, 13.0),
                width);
            DrawCenteredText(
                drawingContext,
                Heading ?? string.Empty,
                Clamp(diameter * 0.13, 8.0, 11.0),
                FontWeights.SemiBold,
                TextBrush,
                diameter + 2.0,
                width);
            if (showDetail)
            {
                DrawCenteredText(
                    drawingContext,
                    Detail ?? string.Empty,
                    7.5,
                    FontWeights.Normal,
                    MutedBrush,
                    diameter + 15.0,
                    width);
            }
        }

        internal static void DrawProgressArc(
            DrawingContext drawingContext,
            Point center,
            double radius,
            double stroke,
            double percent,
            bool available,
            Brush accent)
        {
            if (!available || percent <= 0.0)
            {
                return;
            }

            var progressPen = new Pen(accent, stroke);
            progressPen.StartLineCap = PenLineCap.Round;
            progressPen.EndLineCap = PenLineCap.Round;
            if (percent >= 99.95)
            {
                drawingContext.DrawEllipse(null, progressPen, center, radius, radius);
                return;
            }

            var sweep = percent * 3.6;
            var start = new Point(center.X, center.Y - radius);
            var radians = (sweep - 90.0) * Math.PI / 180.0;
            var end = new Point(
                center.X + radius * Math.Cos(radians),
                center.Y + radius * Math.Sin(radians));
            var figure = new PathFigure();
            figure.StartPoint = start;
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
            drawingContext.DrawGeometry(null, progressPen, geometry);
        }

        internal static void DrawCenteredText(
            DrawingContext drawingContext,
            string value,
            double size,
            FontWeight weight,
            Brush brush,
            double top,
            double width)
        {
            var formatted = new FormattedText(
                value,
                CultureInfo.GetCultureInfo("zh-CN"),
                FlowDirection.LeftToRight,
                new Typeface(
                    new FontFamily("Segoe UI, Microsoft YaHei UI"),
                    FontStyles.Normal,
                    weight,
                    FontStretches.Normal),
                size,
                brush,
                1.0);
            formatted.MaxTextWidth = Math.Max(1.0, width - 3.0);
            formatted.Trimming = TextTrimming.CharacterEllipsis;
            drawingContext.DrawText(
                formatted,
                new Point(Math.Max(0.0, (width - formatted.Width) / 2.0), top));
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }

    public sealed class AvatarQuotaBubble : FrameworkElement
    {
        public static readonly DependencyProperty RemainingPercentProperty =
            DependencyProperty.Register(
                "RemainingPercent",
                typeof(double),
                typeof(AvatarQuotaBubble),
                new FrameworkPropertyMetadata(
                    double.NaN,
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty AvatarSourceProperty =
            DependencyProperty.Register(
                "AvatarSource",
                typeof(ImageSource),
                typeof(AvatarQuotaBubble),
                new FrameworkPropertyMetadata(
                    null,
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty AccentProperty =
            DependencyProperty.Register(
                "Accent",
                typeof(Brush),
                typeof(AvatarQuotaBubble),
                new FrameworkPropertyMetadata(
                    Brushes.MediumAquamarine,
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty TrackProperty =
            DependencyProperty.Register(
                "Track",
                typeof(Brush),
                typeof(AvatarQuotaBubble),
                new FrameworkPropertyMetadata(
                    Brushes.DimGray,
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty BubbleBackgroundProperty =
            DependencyProperty.Register(
                "BubbleBackground",
                typeof(Brush),
                typeof(AvatarQuotaBubble),
                new FrameworkPropertyMetadata(
                    Brushes.Black,
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty TextBrushProperty =
            DependencyProperty.Register(
                "TextBrush",
                typeof(Brush),
                typeof(AvatarQuotaBubble),
                new FrameworkPropertyMetadata(
                    Brushes.White,
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public double RemainingPercent
        {
            get { return (double)GetValue(RemainingPercentProperty); }
            set { SetValue(RemainingPercentProperty, value); }
        }

        public ImageSource AvatarSource
        {
            get { return (ImageSource)GetValue(AvatarSourceProperty); }
            set { SetValue(AvatarSourceProperty, value); }
        }

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

        public Brush BubbleBackground
        {
            get { return (Brush)GetValue(BubbleBackgroundProperty); }
            set { SetValue(BubbleBackgroundProperty, value); }
        }

        public Brush TextBrush
        {
            get { return (Brush)GetValue(TextBrushProperty); }
            set { SetValue(TextBrushProperty, value); }
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            var size = Math.Max(1.0, Math.Min(ActualWidth, ActualHeight));
            var center = new Point(ActualWidth / 2.0, ActualHeight / 2.0);
            var ringRadius = size / 2.0 - 4.0;
            var avatarRadius = Math.Max(1.0, ringRadius - 6.0);
            var available = !double.IsNaN(RemainingPercent) &&
                            !double.IsInfinity(RemainingPercent);
            var value = available
                ? Math.Max(0.0, Math.Min(100.0, RemainingPercent))
                : 0.0;

            drawingContext.DrawEllipse(
                BubbleBackground,
                null,
                center,
                avatarRadius,
                avatarRadius);
            if (AvatarSource != null)
            {
                var imageBrush = new ImageBrush(AvatarSource);
                imageBrush.Stretch = Stretch.UniformToFill;
                drawingContext.DrawEllipse(
                    imageBrush,
                    null,
                    center,
                    avatarRadius,
                    avatarRadius);
            }
            else
            {
                drawingContext.DrawEllipse(
                    TextBrush,
                    null,
                    new Point(center.X, center.Y - 5.0),
                    3.5,
                    3.5);
                var shoulders = new StreamGeometry();
                using (var context = shoulders.Open())
                {
                    context.BeginFigure(
                        new Point(center.X - 8.0, center.Y + 8.0),
                        false,
                        false);
                    context.BezierTo(
                        new Point(center.X - 6.5, center.Y + 1.0),
                        new Point(center.X + 6.5, center.Y + 1.0),
                        new Point(center.X + 8.0, center.Y + 8.0),
                        true,
                        false);
                }
                shoulders.Freeze();
                drawingContext.DrawGeometry(
                    null,
                    new Pen(TextBrush, 2.0),
                    shoulders);
            }

            var trackPen = new Pen(Track, 5.0);
            drawingContext.DrawEllipse(
                null,
                trackPen,
                center,
                ringRadius,
                ringRadius);
            AdaptiveQuotaRing.DrawProgressArc(
                drawingContext,
                center,
                ringRadius,
                5.0,
                value,
                available,
                Accent);
        }
    }
}
