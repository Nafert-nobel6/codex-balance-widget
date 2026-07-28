using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexBalanceWidget.App;
using CodexBalanceWidget.App.Infrastructure;
using CodexBalanceWidget.App.ViewModels;
using CodexBalanceWidget.App.Windows;

namespace CodexBalanceWidget.Infrastructure.Tests
{
    internal static class Program
    {
        private static int _failed;
        private static string _testRoot;

        [STAThread]
        private static int Main()
        {
            var application = new Application();
            application.Resources["UiFont"] =
                new FontFamily("Segoe UI, Microsoft YaHei UI");
            _testRoot = Path.Combine(
                Environment.CurrentDirectory,
                "infrastructure-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testRoot);
            try
            {
                Run("settings round trip and bounds", TestSettingsRoundTrip);
                Run("corrupt settings backup fallback", TestBackupFallback);
                Run("unsafe persisted avatar path", TestUnsafeAvatarPath);
                Run("avatar crop and circular alpha", TestAvatarCrop);
                Run("avatar crop window initialization", TestAvatarCropWindowInitialization);
                Run("same-path avatar refresh", TestSamePathAvatarRefresh);
                Run("JPEG EXIF orientation", TestExifOrientation);
                Run("avatar dimension limit", TestDimensionLimit);
                Run("avatar file-size limit", TestFileSizeLimit);
                Run("invalid image rejection", TestInvalidImage);
            }
            finally
            {
                if (Directory.Exists(_testRoot))
                {
                    Directory.Delete(_testRoot, true);
                }
            }

            if (_failed == 0)
            {
                Console.WriteLine("All infrastructure tests passed.");
                return 0;
            }

            Console.Error.WriteLine(
                _failed + " infrastructure test(s) failed.");
            return 1;
        }

        private static void TestSettingsRoundTrip()
        {
            var directory = NewCaseDirectory("settings-roundtrip");
            var store = new WidgetSettingsStore(directory, null);
            store.Save(
                new WidgetSettings
                {
                    Theme = WidgetThemes.Dark,
                    PanelOpacity = 0.81,
                    ArchiveDelay = TimeSpan.FromSeconds(15.0),
                    WindowWidth = 270.0,
                    WindowHeight = 300.0,
                    WindowOffsetX = -36.0,
                    WindowOffsetY = -28.0,
                    BubbleY = 125.5,
                    AvatarPath = Path.Combine(
                        _testRoot,
                        "outside-avatar.png")
                });

            var loaded = store.Load();
            AssertEqual(WidgetThemes.Dark, loaded.Theme, "theme");
            AssertNear(0.81, loaded.PanelOpacity, "opacity");
            AssertEqual(TimeSpan.FromSeconds(15.0), loaded.ArchiveDelay,
                "archive delay");
            AssertNear(270.0, loaded.WindowWidth, "window width");
            AssertNear(300.0, loaded.WindowHeight, "window height");
            AssertNear(-36.0, loaded.WindowOffsetX, "window X offset");
            AssertNear(-28.0, loaded.WindowOffsetY, "window Y offset");
            AssertNear(125.5, loaded.BubbleY, "bubble Y");
            AssertEqual(string.Empty, loaded.AvatarPath,
                "external avatar path is not persisted");

            loaded.Theme = "Unknown";
            loaded.PanelOpacity = 20.0;
            loaded.WindowWidth = double.NaN;
            loaded.WindowOffsetX = -1000.0;
            loaded.WindowOffsetY = 1000.0;
            loaded.ArchiveDelay = TimeSpan.FromMinutes(2.0);
            var sanitized = store.Sanitize(loaded);
            AssertEqual(WidgetThemes.Light, sanitized.Theme,
                "invalid theme fallback");
            AssertNear(1.0, sanitized.PanelOpacity, "opacity clamp");
            AssertNear(300.0, sanitized.WindowWidth, "NaN width fallback");
            AssertNear(-96.0, sanitized.WindowOffsetX, "X offset clamp");
            AssertNear(16.0, sanitized.WindowOffsetY, "Y offset clamp");
            AssertEqual(TimeSpan.FromSeconds(30.0), sanitized.ArchiveDelay,
                "invalid archive delay fallback");
        }

        private static void TestBackupFallback()
        {
            var directory = NewCaseDirectory("settings-backup");
            var store = new WidgetSettingsStore(directory, null);

            var first = new WidgetSettings();
            first.Theme = WidgetThemes.Dark;
            store.Save(first);

            var second = new WidgetSettings();
            second.Theme = WidgetThemes.Light;
            store.Save(second);

            File.WriteAllText(
                store.SettingsPath,
                "{damaged",
                new UTF8Encoding(false));
            var recovered = store.Load();
            AssertEqual(WidgetThemes.Dark, recovered.Theme,
                "previous valid generation used");
        }

        private static void TestUnsafeAvatarPath()
        {
            var directory = NewCaseDirectory("settings-path");
            var store = new WidgetSettingsStore(directory, null);
            var json =
                "{\"SchemaVersion\":1,\"Theme\":\"Light\"," +
                "\"PanelOpacity\":0.8,\"ArchiveDelay\":\"1.00:00:00\"," +
                "\"WindowWidth\":330,\"WindowHeight\":440,\"BubbleY\":1," +
                "\"AvatarPath\":\"..\\\\outside.png\"}";
            File.WriteAllText(
                store.SettingsPath,
                json,
                new UTF8Encoding(false));

            var loaded = store.Load();
            AssertEqual(string.Empty, loaded.AvatarPath,
                "path traversal is discarded");
        }

        private static void TestAvatarCrop()
        {
            var directory = NewCaseDirectory("avatar-crop");
            var source = Path.Combine(directory, "source.png");
            WriteSolidPng(source, 400, 200, 40, 120, 220, 255);

            var service = new AvatarImageService(directory, null);
            var info = service.Inspect(source);
            AssertEqual(400, info.OrientedWidth, "PNG width");
            AssertEqual(200, info.OrientedHeight, "PNG height");

            var output = service.ImportAndCrop(
                source,
                AvatarCropParameters.Centered);
            AssertTrue(File.Exists(output), "avatar output exists");

            using (var stream = File.OpenRead(output))
            {
                var decoder = new PngBitmapDecoder(
                    stream,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                AssertEqual(256, frame.PixelWidth, "output width");
                AssertEqual(256, frame.PixelHeight, "output height");

                var converted = new FormatConvertedBitmap(
                    frame,
                    PixelFormats.Bgra32,
                    null,
                    0.0);
                var pixels = new byte[256 * 256 * 4];
                converted.CopyPixels(pixels, 256 * 4, 0);
                AssertEqual((byte)0, pixels[3], "transparent corner");
                var centerAlpha =
                    pixels[((128 * 256) + 128) * 4 + 3];
                AssertEqual((byte)255, centerAlpha, "opaque center");
            }
        }

        private static void TestAvatarCropWindowInitialization()
        {
            var directory = NewCaseDirectory("avatar-crop-window");
            var source = Path.Combine(directory, "source.png");
            WriteSolidPng(source, 640, 480, 70, 130, 220, 255);
            var window = new AvatarCropWindow(
                new AvatarImageService(directory, null),
                source);
            window.Close();
        }

        private static void TestSamePathAvatarRefresh()
        {
            var directory = NewCaseDirectory("avatar-refresh");
            var redSource = Path.Combine(directory, "red.png");
            var blueSource = Path.Combine(directory, "blue.png");
            WriteSolidPng(redSource, 160, 160, 220, 30, 40, 255);
            WriteSolidPng(blueSource, 160, 160, 25, 70, 225, 255);

            var service = new AvatarImageService(directory, null);
            var avatarPath = service.ImportAndCrop(
                redSource,
                AvatarCropParameters.Centered);
            var store = new WidgetSettingsStore(directory, null);
            var window = new WidgetWindow(
                new WidgetViewModel(),
                new CodexProcessWindowMonitor(),
                store,
                service,
                new WidgetSettings { AvatarPath = avatarPath });
            window.StopTimers();
            try
            {
                var image = window.FindName("ExpandedAvatarImage") as Image;
                AssertTrue(image != null, "avatar image control exists");
                var before = ReadCenterPixel(image.Source as BitmapSource);

                service.ImportAndCrop(
                    blueSource,
                    AvatarCropParameters.Centered);
                var applyAvatar = typeof(WidgetWindow).GetMethod(
                    "ApplyAvatar",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                AssertTrue(applyAvatar != null, "avatar refresh entry point exists");
                applyAvatar.Invoke(window, null);

                var after = ReadCenterPixel(image.Source as BitmapSource);
                AssertTrue(before[2] > before[0], "initial avatar is red");
                AssertTrue(after[0] > after[2], "refreshed avatar is blue");
            }
            finally
            {
                window.Close();
            }
        }

        private static byte[] ReadCenterPixel(BitmapSource source)
        {
            AssertTrue(source != null, "avatar bitmap source exists");
            var converted = new FormatConvertedBitmap(
                source,
                PixelFormats.Bgra32,
                null,
                0.0);
            var pixel = new byte[4];
            converted.CopyPixels(
                new Int32Rect(
                    converted.PixelWidth / 2,
                    converted.PixelHeight / 2,
                    1,
                    1),
                pixel,
                4,
                0);
            return pixel;
        }

        private static void TestExifOrientation()
        {
            var directory = NewCaseDirectory("avatar-exif");
            var path = Path.Combine(directory, "oriented.jpg");
            WriteOrientedJpeg(path, 120, 80, 6);

            var service = new AvatarImageService(directory, null);
            var info = service.Inspect(path);
            AssertEqual(6, info.ExifOrientation, "EXIF orientation");
            AssertEqual(80, info.OrientedWidth, "oriented width");
            AssertEqual(120, info.OrientedHeight, "oriented height");
            var output = service.ImportAndCrop(
                path,
                AvatarCropParameters.Centered);
            AssertTrue(File.Exists(output), "oriented avatar output");
        }

        private static void TestDimensionLimit()
        {
            var directory = NewCaseDirectory("avatar-dimension");
            var path = Path.Combine(directory, "oversize.png");
            WriteSolidPng(path, 8193, 1, 1, 2, 3, 255);
            AssertAvatarFailure(
                delegate
                {
                    new AvatarImageService(directory, null).Inspect(path);
                },
                "oversize image rejected");
        }

        private static void TestFileSizeLimit()
        {
            var directory = NewCaseDirectory("avatar-size");
            var path = Path.Combine(directory, "large.png");
            using (var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                stream.SetLength(AvatarImageService.MaximumInputBytes + 1);
            }

            AssertAvatarFailure(
                delegate
                {
                    new AvatarImageService(directory, null).Inspect(path);
                },
                "large file rejected before decode");
        }

        private static void TestInvalidImage()
        {
            var directory = NewCaseDirectory("avatar-invalid");
            var path = Path.Combine(directory, "invalid.png");
            File.WriteAllText(path, "not an image");
            AssertAvatarFailure(
                delegate
                {
                    new AvatarImageService(directory, null).Inspect(path);
                },
                "invalid image rejected");
        }

        private static void WriteSolidPng(
            string path,
            int width,
            int height,
            byte red,
            byte green,
            byte blue,
            byte alpha)
        {
            var stride = width * 4;
            var pixels = new byte[stride * height];
            for (var offset = 0; offset < pixels.Length; offset += 4)
            {
                pixels[offset] = blue;
                pixels[offset + 1] = green;
                pixels[offset + 2] = red;
                pixels[offset + 3] = alpha;
            }

            var bitmap = BitmapSource.Create(
                width,
                height,
                96.0,
                96.0,
                PixelFormats.Bgra32,
                null,
                pixels,
                stride);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path))
            {
                encoder.Save(stream);
            }
        }

        private static void WriteOrientedJpeg(
            string path,
            int width,
            int height,
            ushort orientation)
        {
            var stride = width * 3;
            var pixels = new byte[stride * height];
            for (var offset = 0; offset < pixels.Length; offset += 3)
            {
                pixels[offset] = 80;
                pixels[offset + 1] = 140;
                pixels[offset + 2] = 220;
            }

            var bitmap = BitmapSource.Create(
                width,
                height,
                96.0,
                96.0,
                PixelFormats.Bgr24,
                null,
                pixels,
                stride);
            var metadata = new BitmapMetadata("jpg");
            metadata.SetQuery(
                "/app1/ifd/{ushort=274}",
                orientation);
            var frame = BitmapFrame.Create(
                bitmap,
                null,
                metadata,
                null);
            var encoder = new JpegBitmapEncoder();
            encoder.Frames.Add(frame);
            using (var stream = File.Create(path))
            {
                encoder.Save(stream);
            }
        }

        private static string NewCaseDirectory(string name)
        {
            var path = Path.Combine(_testRoot, name);
            Directory.CreateDirectory(path);
            return path;
        }

        private static void AssertAvatarFailure(Action action, string message)
        {
            var threw = false;
            try
            {
                action();
            }
            catch (AvatarImageException)
            {
                threw = true;
            }

            AssertTrue(threw, message);
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                Console.WriteLine("PASS " + name);
            }
            catch (Exception exception)
            {
                _failed++;
                Console.Error.WriteLine(
                    "FAIL " + name + ": " + exception.Message);
            }
        }

        private static void AssertTrue(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static void AssertEqual<T>(
            T expected,
            T actual,
            string message)
        {
            if (!object.Equals(expected, actual))
            {
                throw new InvalidOperationException(
                    message +
                    " (expected " +
                    expected +
                    ", actual " +
                    actual +
                    ")");
            }
        }

        private static void AssertNear(
            double expected,
            double actual,
            string message)
        {
            if (Math.Abs(expected - actual) > 0.0001)
            {
                throw new InvalidOperationException(
                    message +
                    " (expected " +
                    expected +
                    ", actual " +
                    actual +
                    ")");
            }
        }
    }
}
