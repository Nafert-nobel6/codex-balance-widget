using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexBalanceWidget.Core;

namespace CodexBalanceWidget.App.Infrastructure
{
    public sealed class AvatarCropParameters
    {
        public AvatarCropParameters(
            double centerX,
            double centerY,
            double zoom)
        {
            if (!IsFinite(centerX) ||
                !IsFinite(centerY) ||
                !IsFinite(zoom) ||
                centerX < 0.0 ||
                centerX > 1.0 ||
                centerY < 0.0 ||
                centerY > 1.0 ||
                zoom < 1.0 ||
                zoom > 8.0)
            {
                throw new ArgumentOutOfRangeException(
                    "Crop values must use normalized centers and 1x-8x zoom.");
            }

            CenterX = centerX;
            CenterY = centerY;
            Zoom = zoom;
        }

        public double CenterX { get; private set; }
        public double CenterY { get; private set; }
        public double Zoom { get; private set; }

        public static AvatarCropParameters Centered
        {
            get { return new AvatarCropParameters(0.5, 0.5, 1.0); }
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }

    public sealed class AvatarImageInfo
    {
        public AvatarImageInfo(
            int sourceWidth,
            int sourceHeight,
            int orientedWidth,
            int orientedHeight,
            int exifOrientation,
            long fileBytes)
        {
            SourceWidth = sourceWidth;
            SourceHeight = sourceHeight;
            OrientedWidth = orientedWidth;
            OrientedHeight = orientedHeight;
            ExifOrientation = exifOrientation;
            FileBytes = fileBytes;
        }

        public int SourceWidth { get; private set; }
        public int SourceHeight { get; private set; }
        public int OrientedWidth { get; private set; }
        public int OrientedHeight { get; private set; }
        public int ExifOrientation { get; private set; }
        public long FileBytes { get; private set; }
    }

    public sealed class AvatarImageException : Exception
    {
        public AvatarImageException(string message)
            : base(message)
        {
        }

        public AvatarImageException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// Validates and imports local PNG/JPEG avatars. Output is always a
    /// 256-by-256 PNG with transparent pixels outside the circular crop.
    /// </summary>
    public sealed class AvatarImageService
    {
        public const long MaximumInputBytes = 20L * 1024L * 1024L;
        public const int MaximumInputDimension = 8192;
        public const int OutputDimension = 256;

        private readonly WidgetStatePaths _paths;
        private readonly IWidgetLogger _logger;
        private readonly object _gate = new object();

        public AvatarImageService(
            string stateDirectory,
            IWidgetLogger logger)
        {
            _paths = new WidgetStatePaths(stateDirectory);
            _logger = logger;
        }

        public string OutputPath
        {
            get { return _paths.AvatarPath; }
        }

        public AvatarImageInfo Inspect(string sourcePath)
        {
            var canonicalPath = ValidateSourcePath(sourcePath);
            try
            {
                using (var stream = OpenSource(canonicalPath))
                {
                    ValidateOpenedLength(stream);
                    var decoder = CreateSupportedDecoder(stream);
                    var frame = GetValidatedFrame(decoder);
                    var orientation = ReadExifOrientation(frame);
                    return CreateImageInfo(
                        frame,
                        orientation,
                        stream.Length);
                }
            }
            catch (AvatarImageException)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogFailure(exception);
                throw new AvatarImageException(
                    "The selected avatar could not be decoded safely.",
                    exception);
            }
        }

        public string ImportAndCrop(
            string sourcePath,
            AvatarCropParameters crop)
        {
            if (crop == null)
            {
                throw new ArgumentNullException("crop");
            }

            var canonicalPath = ValidateSourcePath(sourcePath);
            lock (_gate)
            {
                string stagingPath = null;
                try
                {
                    Directory.CreateDirectory(_paths.AvatarDirectory);
                    stagingPath = Path.Combine(
                        _paths.AvatarDirectory,
                        "avatar." + Guid.NewGuid().ToString("N") + ".staging");

                    using (var input = OpenSource(canonicalPath))
                    {
                        ValidateOpenedLength(input);
                        var decoder = CreateSupportedDecoder(input);
                        var frame = GetValidatedFrame(decoder);
                        var orientation = ReadExifOrientation(frame);
                        var oriented = ApplyExifOrientation(frame, orientation);
                        var output = RenderCircularCrop(oriented, crop);
                        WritePng(output, stagingPath);
                    }

                    ValidateOutput(stagingPath);
                    ReplaceAvatarAtomically(stagingPath);
                    stagingPath = null;
                    ValidateOutput(_paths.AvatarPath);
                    return _paths.AvatarPath;
                }
                catch (AvatarImageException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    LogFailure(exception);
                    throw new AvatarImageException(
                        "The selected avatar could not be imported safely.",
                        exception);
                }
                finally
                {
                    if (stagingPath != null)
                    {
                        TryDelete(stagingPath);
                    }
                }
            }
        }

        private string ValidateSourcePath(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                throw new AvatarImageException(
                    "Select a local PNG or JPEG image.");
            }

            string canonicalPath;
            try
            {
                canonicalPath = Path.GetFullPath(sourcePath);
            }
            catch (Exception exception)
            {
                throw new AvatarImageException(
                    "The selected avatar path is invalid.",
                    exception);
            }

            if (canonicalPath.StartsWith("\\\\", StringComparison.Ordinal))
            {
                throw new AvatarImageException(
                    "Network avatar paths are not supported.");
            }

            var extension = Path.GetExtension(canonicalPath);
            if (!string.Equals(
                    extension,
                    ".png",
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(
                    extension,
                    ".jpg",
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(
                    extension,
                    ".jpeg",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new AvatarImageException(
                    "Only PNG and JPEG avatars are supported.");
            }

            FileInfo information;
            try
            {
                information = new FileInfo(canonicalPath);
                if (!information.Exists)
                {
                    throw new AvatarImageException(
                        "The selected avatar does not exist.");
                }
            }
            catch (AvatarImageException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new AvatarImageException(
                    "The selected avatar could not be inspected.",
                    exception);
            }

            if (information.Length <= 0 ||
                information.Length > MaximumInputBytes)
            {
                throw new AvatarImageException(
                    "Avatar files must be between 1 byte and 20 MB.");
            }

            return canonicalPath;
        }

        private static FileStream OpenSource(string path)
        {
            return new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
        }

        private static void ValidateOpenedLength(FileStream stream)
        {
            if (stream.Length <= 0 || stream.Length > MaximumInputBytes)
            {
                throw new AvatarImageException(
                    "The selected avatar exceeds the supported file-size limit.");
            }
        }

        private static BitmapDecoder CreateSupportedDecoder(Stream stream)
        {
            BitmapDecoder decoder;
            try
            {
                decoder = BitmapDecoder.Create(
                    stream,
                    BitmapCreateOptions.PreservePixelFormat |
                    BitmapCreateOptions.IgnoreColorProfile |
                    BitmapCreateOptions.DelayCreation,
                    BitmapCacheOption.None);
            }
            catch (Exception exception)
            {
                throw new AvatarImageException(
                    "The selected file is not a valid image.",
                    exception);
            }

            if (!(decoder is PngBitmapDecoder) &&
                !(decoder is JpegBitmapDecoder))
            {
                throw new AvatarImageException(
                    "The file contents are not PNG or JPEG.");
            }

            return decoder;
        }

        private static BitmapFrame GetValidatedFrame(BitmapDecoder decoder)
        {
            BitmapFrame frame;
            try
            {
                if (decoder.Frames == null || decoder.Frames.Count == 0)
                {
                    throw new AvatarImageException(
                        "The image does not contain a decodable frame.");
                }

                frame = decoder.Frames[0];
            }
            catch (AvatarImageException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new AvatarImageException(
                    "The image frame is damaged.",
                    exception);
            }

            if (frame.PixelWidth <= 0 ||
                frame.PixelHeight <= 0 ||
                frame.PixelWidth > MaximumInputDimension ||
                frame.PixelHeight > MaximumInputDimension)
            {
                throw new AvatarImageException(
                    "Avatar dimensions must not exceed 8192 pixels.");
            }

            return frame;
        }

        private static AvatarImageInfo CreateImageInfo(
            BitmapFrame frame,
            int orientation,
            long fileBytes)
        {
            var swapsDimensions =
                orientation == 5 ||
                orientation == 6 ||
                orientation == 7 ||
                orientation == 8;
            return new AvatarImageInfo(
                frame.PixelWidth,
                frame.PixelHeight,
                swapsDimensions ? frame.PixelHeight : frame.PixelWidth,
                swapsDimensions ? frame.PixelWidth : frame.PixelHeight,
                orientation,
                fileBytes);
        }

        private static int ReadExifOrientation(BitmapFrame frame)
        {
            var metadata = frame.Metadata as BitmapMetadata;
            if (metadata == null)
            {
                return 1;
            }

            var queries = new[]
            {
                "/app1/ifd/{ushort=274}",
                "/ifd/{ushort=274}"
            };
            foreach (var query in queries)
            {
                try
                {
                    var value = metadata.GetQuery(query);
                    if (value == null)
                    {
                        continue;
                    }

                    var orientation = Convert.ToInt32(value);
                    if (orientation >= 1 && orientation <= 8)
                    {
                        return orientation;
                    }
                }
                catch (Exception)
                {
                }
            }

            return 1;
        }

        private static BitmapSource ApplyExifOrientation(
            BitmapSource source,
            int orientation)
        {
            BitmapSource result = source;
            switch (orientation)
            {
                case 2:
                    result = Transform(result, new ScaleTransform(-1.0, 1.0));
                    break;
                case 3:
                    result = Transform(result, new RotateTransform(180.0));
                    break;
                case 4:
                    result = Transform(result, new ScaleTransform(1.0, -1.0));
                    break;
                case 5:
                    result = Transform(result, new ScaleTransform(-1.0, 1.0));
                    result = Transform(result, new RotateTransform(270.0));
                    break;
                case 6:
                    result = Transform(result, new RotateTransform(90.0));
                    break;
                case 7:
                    result = Transform(result, new ScaleTransform(-1.0, 1.0));
                    result = Transform(result, new RotateTransform(90.0));
                    break;
                case 8:
                    result = Transform(result, new RotateTransform(270.0));
                    break;
            }

            if (result.CanFreeze)
            {
                result.Freeze();
            }

            return result;
        }

        private static BitmapSource Transform(
            BitmapSource source,
            Transform transform)
        {
            var transformed = new TransformedBitmap(source, transform);
            if (transformed.CanFreeze)
            {
                transformed.Freeze();
            }

            return transformed;
        }

        private static BitmapSource RenderCircularCrop(
            BitmapSource source,
            AvatarCropParameters crop)
        {
            var width = (double)source.PixelWidth;
            var height = (double)source.PixelHeight;
            var side = Math.Min(width, height) / crop.Zoom;
            var halfSide = side / 2.0;
            var centerX = Clamp(
                crop.CenterX * width,
                halfSide,
                width - halfSide);
            var centerY = Clamp(
                crop.CenterY * height,
                halfSide,
                height - halfSide);
            var cropX = centerX - halfSide;
            var cropY = centerY - halfSide;
            var scale = OutputDimension / side;

            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(
                visual,
                BitmapScalingMode.HighQuality);
            using (var context = visual.RenderOpen())
            {
                context.DrawRectangle(
                    Brushes.Transparent,
                    null,
                    new Rect(
                        0.0,
                        0.0,
                        OutputDimension,
                        OutputDimension));
                context.PushClip(
                    new EllipseGeometry(
                        new Point(
                            OutputDimension / 2.0,
                            OutputDimension / 2.0),
                        OutputDimension / 2.0,
                        OutputDimension / 2.0));
                context.DrawImage(
                    source,
                    new Rect(
                        -cropX * scale,
                        -cropY * scale,
                        width * scale,
                        height * scale));
                context.Pop();
            }

            var output = new RenderTargetBitmap(
                OutputDimension,
                OutputDimension,
                96.0,
                96.0,
                PixelFormats.Pbgra32);
            output.Render(visual);
            output.Freeze();
            return output;
        }

        private static void WritePng(BitmapSource source, string path)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using (var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.WriteThrough))
            {
                encoder.Save(stream);
                stream.Flush(true);
            }
        }

        private static void ValidateOutput(string path)
        {
            using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            {
                var decoder = new PngBitmapDecoder(
                    stream,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.None);
                if (decoder.Frames.Count != 1 ||
                    decoder.Frames[0].PixelWidth != OutputDimension ||
                    decoder.Frames[0].PixelHeight != OutputDimension)
                {
                    throw new AvatarImageException(
                        "The generated avatar failed validation.");
                }
            }
        }

        private void ReplaceAvatarAtomically(string stagingPath)
        {
            if (!File.Exists(_paths.AvatarPath))
            {
                File.Move(stagingPath, _paths.AvatarPath);
                ValidateOutput(_paths.AvatarPath);
                return;
            }

            var currentIsValid = IsValidOutput(_paths.AvatarPath);
            if (!currentIsValid)
            {
                File.Replace(
                    stagingPath,
                    _paths.AvatarPath,
                    null,
                    true);
                ValidateOutput(_paths.AvatarPath);
                return;
            }

            var previousPath = Path.Combine(
                _paths.AvatarDirectory,
                "avatar." + Guid.NewGuid().ToString("N") + ".previous");
            var preservePrevious = false;
            try
            {
                File.Copy(_paths.AvatarPath, previousPath, false);
                ValidateOutput(previousPath);

                try
                {
                    File.Replace(
                        stagingPath,
                        _paths.AvatarPath,
                        null,
                        true);
                    ValidateOutput(_paths.AvatarPath);
                }
                catch (Exception replaceException)
                {
                    try
                    {
                        if (File.Exists(_paths.AvatarPath))
                        {
                            File.Replace(
                                previousPath,
                                _paths.AvatarPath,
                                null,
                                true);
                        }
                        else
                        {
                            File.Move(previousPath, _paths.AvatarPath);
                        }

                        previousPath = null;
                        ValidateOutput(_paths.AvatarPath);
                    }
                    catch (Exception recoveryException)
                    {
                        preservePrevious = true;
                        throw new AvatarImageException(
                            "Avatar replacement and recovery both failed.",
                            new AggregateException(
                                replaceException,
                                recoveryException));
                    }

                    throw;
                }

                try
                {
                    if (File.Exists(_paths.AvatarBackupPath))
                    {
                        File.Replace(
                            previousPath,
                            _paths.AvatarBackupPath,
                            null,
                            true);
                    }
                    else
                    {
                        File.Move(
                            previousPath,
                            _paths.AvatarBackupPath);
                    }

                    previousPath = null;
                }
                catch (Exception exception)
                {
                    LogFailure(exception);
                }
            }
            finally
            {
                if (previousPath != null && !preservePrevious)
                {
                    TryDelete(previousPath);
                }
            }
        }

        private static bool IsValidOutput(string path)
        {
            try
            {
                ValidateOutput(path);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static double Clamp(
            double value,
            double minimum,
            double maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private void LogFailure(Exception exception)
        {
            if (_logger != null)
            {
                _logger.Warn(
                    "Avatar processing failed (" +
                    exception.GetType().Name +
                    ").");
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
