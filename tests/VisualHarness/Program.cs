using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexBalanceWidget.App;
using CodexBalanceWidget.App.Infrastructure;
using CodexBalanceWidget.App.ViewModels;
using CodexBalanceWidget.App.Windows;
using CodexBalanceWidget.Core;

namespace CodexBalanceWidget.VisualHarness
{
    internal static class Program
    {
        private const int GwlExStyle = -20;
        private const long WsExToolWindow = 0x00000080L;
        private const long WsExNoActivate = 0x08000000L;

        [STAThread]
        private static int Main(string[] arguments)
        {
            try
            {
                return Run(arguments);
            }
            catch (Exception exception)
            {
                File.WriteAllText(
                    Path.Combine(
                        Path.GetTempPath(),
                        "CodexBalanceWidget-visual-harness-error.txt"),
                    exception.ToString());
                return 1;
            }
        }

        private static int Run(string[] arguments)
        {
            var application = new Application();
            application.ShutdownMode = ShutdownMode.OnMainWindowClose;
            AddResources(application.Resources);

            var stateDirectory = Path.Combine(
                Path.GetTempPath(),
                "CodexBalanceWidget-visual-acceptance");
            Directory.CreateDirectory(stateDirectory);
            var settingsStore = new WidgetSettingsStore(stateDirectory, null);
            var avatarImageService = new AvatarImageService(stateDirectory, null);
            if (HasArgument(arguments, "--crop"))
            {
                var cropWindow = new AvatarCropWindow(
                    avatarImageService,
                    CreateSampleImage(stateDirectory));
                cropWindow.ShowInTaskbar = true;
                cropWindow.ShowActivated = true;
                application.MainWindow = cropWindow;
                return application.Run(cropWindow);
            }

            var settings = new WidgetSettings
            {
                Theme = HasArgument(arguments, "--dark")
                    ? WidgetThemes.Dark
                    : WidgetThemes.Light,
                PanelOpacity = 0.94,
                ArchiveDelay = TimeSpan.FromSeconds(30.0),
                WindowWidth = 240.0,
                WindowHeight = 320.0
            };
            var viewModel = CreateViewModel();
            var window = new WidgetWindow(
                viewModel,
                new CodexProcessWindowMonitor(),
                settingsStore,
                avatarImageService,
                settings);
            window.ShowInTaskbar = true;
            window.ShowActivated = true;
            window.Focusable = true;
            window.SourceInitialized += delegate
            {
                var handle = new WindowInteropHelper(window).Handle;
                var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
                style &= ~WsExToolWindow;
                style &= ~WsExNoActivate;
                SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style));
            };
            window.StopTimers();
            var qaRaiseTimer = new DispatcherTimer();
            qaRaiseTimer.Interval = TimeSpan.FromMilliseconds(650.0);
            qaRaiseTimer.Tick += delegate
            {
                qaRaiseTimer.Stop();
                window.Topmost = false;
                window.Topmost = true;
            };
            window.SizeChanged += delegate
            {
                qaRaiseTimer.Stop();
                qaRaiseTimer.Start();
            };
            window.PreviewMouseRightButtonUp += delegate(
                object sender,
                MouseButtonEventArgs eventArgs)
            {
                var method = typeof(WidgetWindow).GetMethod(
                    "BeginArchive",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (method == null)
                {
                    throw new InvalidOperationException(
                        "Archive transition entry point was not found.");
                }

                method.Invoke(window, null);
                eventArgs.Handled = true;
            };
            window.Loaded += delegate
            {
                window.Topmost = true;
                qaRaiseTimer.Start();
            };
            if (HasArgument(arguments, "--bubble"))
            {
                window.Loaded += delegate
                {
                    var timer = new DispatcherTimer();
                    timer.Interval = TimeSpan.FromMilliseconds(700.0);
                    timer.Tick += delegate
                    {
                        timer.Stop();
                        var method = typeof(WidgetWindow).GetMethod(
                            "BeginArchive",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                        if (method == null)
                        {
                            throw new InvalidOperationException(
                                "Archive transition entry point was not found.");
                        }
                        method.Invoke(window, null);
                    };
                    timer.Start();
                };
            }
            application.MainWindow = window;
            return application.Run(window);
        }

        private static bool HasArgument(string[] arguments, string expected)
        {
            foreach (var argument in arguments)
            {
                if (string.Equals(
                    argument,
                    expected,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static string CreateSampleImage(string directory)
        {
            const int width = 640;
            const int height = 480;
            var pixels = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var index = (y * width + x) * 4;
                    pixels[index] = (byte)(80 + (x * 120 / width));
                    pixels[index + 1] = (byte)(90 + (y * 140 / height));
                    pixels[index + 2] = (byte)(210 - (x * 80 / width));
                    pixels[index + 3] = 255;
                }
            }

            var bitmap = BitmapSource.Create(
                width,
                height,
                96.0,
                96.0,
                PixelFormats.Bgra32,
                null,
                pixels,
                width * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.Combine(directory, "visual-avatar-source.png");
            using (var stream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None))
            {
                encoder.Save(stream);
            }
            return path;
        }

        private static WidgetViewModel CreateViewModel()
        {
            var now = DateTimeOffset.Now;
            var credits = new List<ResetCredit>
            {
                new ResetCredit(
                    "a",
                    "legacy label",
                    "优先使用 · 即将到期",
                    "available",
                    now.AddDays(-4.0),
                    now.AddHours(8.0).AddMinutes(26.0)),
                new ResetCredit(
                    "b",
                    "legacy label",
                    "项目补充额度",
                    "available",
                    now.AddDays(-2.0),
                    now.AddDays(2.0).AddHours(3.0)),
                new ResetCredit(
                    "c",
                    "legacy label",
                    "季度活动额度",
                    "available",
                    now.AddDays(-1.0),
                    now.AddDays(4.0)),
                new ResetCredit(
                    "d",
                    "legacy label",
                    "备用额度",
                    "available",
                    now.AddDays(-1.0),
                    now.AddDays(7.0))
            };
            var viewModel = new WidgetViewModel();
            viewModel.ApplySnapshot(
                new RateLimitSnapshot(
                    new List<QuotaWindow>
                    {
                        new QuotaWindow(35.0, 10080, now.AddDays(3.0)),
                        new QuotaWindow(58.0, 300, now.AddHours(2.0))
                    },
                    credits.Count,
                    true,
                    credits,
                    now,
                    "额度已同步",
                    false));
            return viewModel;
        }

        private static void AddResources(ResourceDictionary resources)
        {
            resources["UiFont"] = new FontFamily("Segoe UI, Microsoft YaHei UI");
            resources["PanelBrush"] = Brush(26, 28, 34);
            resources["CardBrush"] = Brush(36, 39, 47);
            resources["CardHoverBrush"] = Brush(43, 46, 56);
            resources["TextBrush"] = Brush(245, 246, 248);
            resources["MutedTextBrush"] = Brush(158, 164, 177);
            resources["SubtleTextBrush"] = Brush(115, 121, 134);
            resources["AccentBrush"] = Brush(112, 225, 178);
            resources["WarningBrush"] = Brush(255, 198, 109);
            resources["DividerBrush"] = Brush(48, 51, 61);
        }

        private static SolidColorBrush Brush(byte red, byte green, byte blue)
        {
            var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
            brush.Freeze();
            return brush;
        }

        private static IntPtr GetWindowLongPtr(IntPtr handle, int index)
        {
            return IntPtr.Size == 8
                ? GetWindowLongPtr64(handle, index)
                : new IntPtr(GetWindowLong32(handle, index));
        }

        private static IntPtr SetWindowLongPtr(
            IntPtr handle,
            int index,
            IntPtr value)
        {
            return IntPtr.Size == 8
                ? SetWindowLongPtr64(handle, index, value)
                : new IntPtr(SetWindowLong32(handle, index, value.ToInt32()));
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong32(IntPtr handle, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr handle, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLong32(
            IntPtr handle,
            int index,
            int value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr64(
            IntPtr handle,
            int index,
            IntPtr value);
    }
}
