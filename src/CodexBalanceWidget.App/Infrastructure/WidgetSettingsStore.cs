using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using CodexBalanceWidget.Core;

namespace CodexBalanceWidget.App.Infrastructure
{
    public sealed class WidgetStatePaths
    {
        public WidgetStatePaths(string stateDirectory)
        {
            if (string.IsNullOrWhiteSpace(stateDirectory))
            {
                throw new ArgumentException(
                    "A local state directory is required.",
                    "stateDirectory");
            }

            var fullPath = Path.GetFullPath(stateDirectory).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root) ||
                string.Equals(
                    fullPath,
                    root.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith(
                    "\\\\",
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The state directory must be a non-root local path.",
                    "stateDirectory");
            }

            StateDirectory = fullPath;
            SettingsPath = Path.Combine(fullPath, "settings.json");
            SettingsBackupPath = Path.Combine(fullPath, "settings.backup.json");
            AvatarDirectory = Path.Combine(fullPath, "avatars");
            AvatarPath = Path.Combine(AvatarDirectory, "avatar.png");
            AvatarBackupPath = Path.Combine(
                AvatarDirectory,
                "avatar.backup.png");
        }

        public string StateDirectory { get; private set; }
        public string SettingsPath { get; private set; }
        public string SettingsBackupPath { get; private set; }
        public string AvatarDirectory { get; private set; }
        public string AvatarPath { get; private set; }
        public string AvatarBackupPath { get; private set; }

        public bool IsInsideStateDirectory(string path)
        {
            return IsInside(StateDirectory, path);
        }

        public bool IsInsideAvatarDirectory(string path)
        {
            return IsInside(AvatarDirectory, path);
        }

        private static bool IsInside(string parent, string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return false;
            }

            try
            {
                var canonicalParent = Path.GetFullPath(parent).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                var canonicalCandidate = Path.GetFullPath(candidate);
                var prefix = canonicalParent + Path.DirectorySeparatorChar;
                return canonicalCandidate.StartsWith(
                    prefix,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Persists user-visible widget preferences without using the registry.
    /// Writes are staged beside the target and atomically replaced.
    /// </summary>
    public sealed class WidgetSettingsStore
    {
        private const int CurrentSchemaVersion = 2;
        private readonly object _gate = new object();
        private readonly WidgetStatePaths _paths;
        private readonly IWidgetLogger _logger;

        public WidgetSettingsStore(
            string stateDirectory,
            IWidgetLogger logger)
        {
            _paths = new WidgetStatePaths(stateDirectory);
            _logger = logger;
        }

        public string SettingsPath
        {
            get { return _paths.SettingsPath; }
        }

        public WidgetSettings Load()
        {
            lock (_gate)
            {
                WidgetSettings settings;
                if (TryLoad(_paths.SettingsPath, out settings))
                {
                    return settings;
                }

                if (File.Exists(_paths.SettingsPath))
                {
                    LogWarning(
                        "The widget settings file is damaged; trying its backup.");
                }

                if (TryLoad(_paths.SettingsBackupPath, out settings))
                {
                    return settings;
                }

                return Sanitize(new WidgetSettings());
            }
        }

        public void Save(WidgetSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }

            lock (_gate)
            {
                var safeSettings = Sanitize(settings);
                var document = ToDocument(safeSettings);
                var serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = 1024 * 1024;
                var json = serializer.Serialize(document);

                Directory.CreateDirectory(_paths.StateDirectory);
                var stagingPath = Path.Combine(
                    _paths.StateDirectory,
                    "settings." + Guid.NewGuid().ToString("N") + ".staging");
                try
                {
                    using (var stream = new FileStream(
                        stagingPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        4096,
                        FileOptions.WriteThrough))
                    using (var writer = new StreamWriter(
                        stream,
                        new UTF8Encoding(false)))
                    {
                        writer.Write(json);
                        writer.Flush();
                        stream.Flush(true);
                    }

                    if (File.Exists(_paths.SettingsPath))
                    {
                        if (File.Exists(_paths.SettingsBackupPath))
                        {
                            File.Delete(_paths.SettingsBackupPath);
                        }

                        File.Replace(
                            stagingPath,
                            _paths.SettingsPath,
                            _paths.SettingsBackupPath,
                            true);
                    }
                    else
                    {
                        File.Move(stagingPath, _paths.SettingsPath);
                    }

                    stagingPath = null;
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

        public WidgetSettings Sanitize(WidgetSettings settings)
        {
            if (settings == null)
            {
                settings = new WidgetSettings();
            }

            var defaults = new WidgetSettings();
            var result = settings.Clone();
            result.Theme = WidgetThemes.IsSupported(result.Theme)
                ? result.Theme
                : defaults.Theme;
            result.PanelOpacity = ClampOrDefault(
                result.PanelOpacity,
                0.70,
                1.0,
                defaults.PanelOpacity);
            result.ArchiveDelay = ClampArchiveDelay(
                result.ArchiveDelay,
                defaults.ArchiveDelay);
            result.WindowWidth = ClampOrDefault(
                result.WindowWidth,
                240.0,
                300.0,
                defaults.WindowWidth);
            result.WindowHeight = ClampOrDefault(
                result.WindowHeight,
                148.0,
                320.0,
                defaults.WindowHeight);
            result.WindowOffsetX = ClampOrDefault(
                result.WindowOffsetX,
                -96.0,
                12.0,
                defaults.WindowOffsetX);
            result.WindowOffsetY = ClampOrDefault(
                result.WindowOffsetY,
                -96.0,
                16.0,
                defaults.WindowOffsetY);
            result.BubbleY = ClampOrDefault(
                result.BubbleY,
                0.0,
                100000.0,
                defaults.BubbleY);
            result.AvatarPath = NormalizeAvatarPath(result.AvatarPath);
            return result;
        }

        private bool TryLoad(string path, out WidgetSettings settings)
        {
            settings = null;
            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                var info = new FileInfo(path);
                if (info.Length <= 0 || info.Length > 1024 * 1024)
                {
                    return false;
                }

                string json;
                using (var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read))
                using (var reader = new StreamReader(
                    stream,
                    Encoding.UTF8,
                    true,
                    4096))
                {
                    json = reader.ReadToEnd();
                }

                var serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = 1024 * 1024;
                var document = serializer.Deserialize<SettingsDocument>(json);
                if (document == null ||
                    document.SchemaVersion <= 0 ||
                    document.SchemaVersion > CurrentSchemaVersion)
                {
                    return false;
                }

                TimeSpan archiveDelay;
                if (!TimeSpan.TryParseExact(
                    document.ArchiveDelay,
                    "c",
                    CultureInfo.InvariantCulture,
                    out archiveDelay))
                {
                    archiveDelay = new WidgetSettings().ArchiveDelay;
                }

                settings = Sanitize(
                    new WidgetSettings
                    {
                        Theme = document.Theme,
                        PanelOpacity = document.PanelOpacity,
                        ArchiveDelay = archiveDelay,
                        WindowWidth = document.WindowWidth,
                        WindowHeight = document.WindowHeight,
                        WindowOffsetX = document.WindowOffsetX,
                        WindowOffsetY = document.WindowOffsetY,
                        BubbleY = document.BubbleY,
                        AvatarPath = ResolveStoredAvatarPath(document.AvatarPath)
                    });
                return true;
            }
            catch (Exception exception)
            {
                LogWarning(
                    "Widget settings could not be read (" +
                    exception.GetType().Name +
                    ").");
                settings = null;
                return false;
            }
        }

        private SettingsDocument ToDocument(WidgetSettings settings)
        {
            return new SettingsDocument
            {
                SchemaVersion = CurrentSchemaVersion,
                Theme = settings.Theme,
                PanelOpacity = settings.PanelOpacity,
                ArchiveDelay = settings.ArchiveDelay.ToString(
                    "c",
                    CultureInfo.InvariantCulture),
                WindowWidth = settings.WindowWidth,
                WindowHeight = settings.WindowHeight,
                WindowOffsetX = settings.WindowOffsetX,
                WindowOffsetY = settings.WindowOffsetY,
                BubbleY = settings.BubbleY,
                AvatarPath = GetStoredAvatarPath(settings.AvatarPath)
            };
        }

        private string NormalizeAvatarPath(string avatarPath)
        {
            if (string.IsNullOrWhiteSpace(avatarPath))
            {
                return string.Empty;
            }

            try
            {
                var canonical = Path.GetFullPath(avatarPath);
                return _paths.IsInsideAvatarDirectory(canonical) &&
                    string.Equals(
                        Path.GetExtension(canonical),
                        ".png",
                        StringComparison.OrdinalIgnoreCase)
                    ? canonical
                    : string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private string ResolveStoredAvatarPath(string storedPath)
        {
            if (string.IsNullOrWhiteSpace(storedPath) ||
                Path.IsPathRooted(storedPath))
            {
                return string.Empty;
            }

            try
            {
                return NormalizeAvatarPath(
                    Path.Combine(_paths.StateDirectory, storedPath));
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private string GetStoredAvatarPath(string avatarPath)
        {
            var canonical = NormalizeAvatarPath(avatarPath);
            if (string.IsNullOrWhiteSpace(canonical))
            {
                return string.Empty;
            }

            return canonical.Substring(_paths.StateDirectory.Length)
                .TrimStart(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
        }

        private static double ClampOrDefault(
            double value,
            double minimum,
            double maximum,
            double defaultValue)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return defaultValue;
            }

            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private static TimeSpan ClampArchiveDelay(
            TimeSpan value,
            TimeSpan defaultValue)
        {
            var supportedValues = new[]
            {
                TimeSpan.FromSeconds(10.0),
                TimeSpan.FromSeconds(15.0),
                TimeSpan.FromSeconds(30.0),
                TimeSpan.FromMinutes(1.0)
            };
            foreach (var supportedValue in supportedValues)
            {
                if (value == supportedValue)
                {
                    return value;
                }
            }

            return defaultValue;
        }

        private void LogWarning(string message)
        {
            if (_logger != null)
            {
                _logger.Warn(message);
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

        public sealed class SettingsDocument
        {
            public int SchemaVersion { get; set; }
            public string Theme { get; set; }
            public double PanelOpacity { get; set; }
            public string ArchiveDelay { get; set; }
            public double WindowWidth { get; set; }
            public double WindowHeight { get; set; }
            public double WindowOffsetX { get; set; }
            public double WindowOffsetY { get; set; }
            public double BubbleY { get; set; }
            public string AvatarPath { get; set; }
        }
    }
}
