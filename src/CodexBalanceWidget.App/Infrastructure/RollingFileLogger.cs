using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CodexBalanceWidget.Core;

namespace CodexBalanceWidget.App.Infrastructure
{
    public sealed class RollingFileLogger : IWidgetLogger, IDisposable
    {
        private const long MaximumLogBytes = 512 * 1024;
        private const int RetainedLogCount = 3;
        private static readonly Regex JsonSecretPattern = new Regex(
            "(?i)(\"[^\"]*(?:token|authorization|cookie|secret|credential)[^\"]*\"\\s*:\\s*\")[^\"]*(\")",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex PlainSecretPattern = new Regex(
            "(?i)(\\b[^\\s=:,;]*(?:token|authorization|cookie|secret|credential)[^\\s=:,;]*\\s*[=:]\\s*)[^\\s,;]+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly object _gate = new object();
        private readonly string _logDirectory;
        private readonly string _activeLogPath;
        private bool _disposed;

        public RollingFileLogger(string logDirectory)
        {
            if (string.IsNullOrWhiteSpace(logDirectory))
            {
                throw new ArgumentException("A log directory is required.", "logDirectory");
            }

            _logDirectory = logDirectory;
            _activeLogPath = Path.Combine(logDirectory, "widget.log");
        }

        public void Info(string message)
        {
            Write("INFO", message, null);
        }

        public void Warn(string message)
        {
            Write("WARN", message, null);
        }

        public void Error(string message, Exception exception)
        {
            Write("ERROR", message, exception);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
            }
        }

        private void Write(string level, string message, Exception exception)
        {
            var text = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz") +
                       " [" + level + "] " +
                       (message ?? string.Empty);
            if (exception != null)
            {
                text += Environment.NewLine + exception;
            }

            text = Redact(text) + Environment.NewLine;
            var bytesNeeded = Encoding.UTF8.GetByteCount(text);

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                try
                {
                    Directory.CreateDirectory(_logDirectory);
                    RotateIfNeeded(bytesNeeded);
                    File.AppendAllText(_activeLogPath, text, new UTF8Encoding(false));
                }
                catch
                {
                    // Diagnostics must never bring down the widget.
                }
            }
        }

        private static string Redact(string value)
        {
            var redacted = JsonSecretPattern.Replace(value, "$1[REDACTED]$2");
            return PlainSecretPattern.Replace(redacted, "$1[REDACTED]");
        }

        private void RotateIfNeeded(int bytesNeeded)
        {
            if (!File.Exists(_activeLogPath))
            {
                return;
            }

            var length = new FileInfo(_activeLogPath).Length;
            if (length + bytesNeeded <= MaximumLogBytes)
            {
                return;
            }

            var oldest = _activeLogPath + "." + RetainedLogCount;
            if (File.Exists(oldest))
            {
                File.Delete(oldest);
            }

            for (var index = RetainedLogCount - 1; index >= 1; index--)
            {
                var source = _activeLogPath + "." + index;
                var destination = _activeLogPath + "." + (index + 1);
                if (File.Exists(source))
                {
                    File.Move(source, destination);
                }
            }

            File.Move(_activeLogPath, _activeLogPath + ".1");
        }
    }
}
