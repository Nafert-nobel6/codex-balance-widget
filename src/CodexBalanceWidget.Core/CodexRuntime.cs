using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace CodexBalanceWidget.Core
{
    public static class CodexRuntimeLocator
    {
        private const uint ProcessQueryLimitedInformation = 0x1000;

        public static IList<string> FindRunningPackageExecutablePaths()
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName("ChatGPT");
            }
            catch (Exception)
            {
                return result;
            }

            foreach (var process in processes)
            {
                using (process)
                {
                    var path = TryGetProcessImagePath(process.Id);
                    if (string.IsNullOrWhiteSpace(path) ||
                        !string.Equals(
                            Path.GetFileName(path),
                            "ChatGPT.exe",
                            StringComparison.OrdinalIgnoreCase) ||
                        !IsCodexPackagePath(path) ||
                        !seen.Add(path))
                    {
                        continue;
                    }

                    result.Add(path);
                }
            }

            return result;
        }

        public static bool IsCodexPackagePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                var canonicalPath = Path.GetFullPath(path);
                var windowsAppsRoot = GetWindowsAppsRoot();
                var rootWithSeparator =
                    windowsAppsRoot.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar) +
                    Path.DirectorySeparatorChar;
                if (!canonicalPath.StartsWith(
                    rootWithSeparator,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var relative = canonicalPath.Substring(rootWithSeparator.Length);
                var separator = relative.IndexOfAny(
                    new[]
                    {
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar
                    });
                if (separator <= 0)
                {
                    return false;
                }

                var packageDirectory = relative.Substring(0, separator);
                return packageDirectory.StartsWith(
                    "OpenAI.Codex_",
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static string GetBundledCodexPath(string chatGptExecutablePath)
        {
            if (string.IsNullOrWhiteSpace(chatGptExecutablePath))
            {
                return null;
            }

            string canonicalChatGptPath;
            try
            {
                canonicalChatGptPath = Path.GetFullPath(chatGptExecutablePath);
            }
            catch (Exception)
            {
                return null;
            }

            if (!IsCodexPackagePath(canonicalChatGptPath) ||
                !string.Equals(
                    Path.GetFileName(canonicalChatGptPath),
                    "ChatGPT.exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var executableDirectory = Path.GetDirectoryName(canonicalChatGptPath);
            if (string.IsNullOrWhiteSpace(executableDirectory))
            {
                return null;
            }

            var besideApplication = Path.GetFullPath(
                Path.Combine(executableDirectory, "resources", "codex.exe"));
            if (IsCodexPackagePath(besideApplication) &&
                File.Exists(besideApplication))
            {
                return besideApplication;
            }

            var packageRoot = GetPackageRoot(canonicalChatGptPath);
            if (packageRoot == null)
            {
                return null;
            }

            var packageResource = Path.GetFullPath(
                Path.Combine(packageRoot, "resources", "codex.exe"));
            return IsCodexPackagePath(packageResource) &&
                File.Exists(packageResource)
                ? packageResource
                : null;
        }

        internal static string GetPackageRoot(string packagePath)
        {
            if (!IsCodexPackagePath(packagePath))
            {
                return null;
            }

            var canonicalPath = Path.GetFullPath(packagePath);
            var root = GetWindowsAppsRoot().TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            var relative = canonicalPath.Substring(root.Length + 1);
            var separator = relative.IndexOfAny(
                new[]
                {
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar
                });
            if (separator <= 0)
            {
                return null;
            }

            return Path.Combine(root, relative.Substring(0, separator));
        }

        private static string GetWindowsAppsRoot()
        {
            var programFiles = Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFiles);
            return Path.GetFullPath(Path.Combine(programFiles, "WindowsApps"));
        }

        private static string TryGetProcessImagePath(int processId)
        {
            var handle = OpenProcess(
                ProcessQueryLimitedInformation,
                false,
                processId);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var capacity = 32768;
                var builder = new StringBuilder(capacity);
                if (!QueryFullProcessImageName(
                    handle,
                    0,
                    builder,
                    ref capacity))
                {
                    return null;
                }

                return builder.ToString();
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(
            uint desiredAccess,
            bool inheritHandle,
            int processId);

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(
            IntPtr process,
            uint flags,
            StringBuilder executableName,
            ref int size);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }

    /// <summary>
    /// Copies the signed Codex runtime from the live MSIX package into a
    /// user-writable private directory. Failed updates never replace the last
    /// verified runtime.
    /// </summary>
    public sealed class CodexRuntimeSynchronizer
    {
        private readonly string _runtimeDirectory;
        private readonly string _currentExecutablePath;
        private readonly string _backupExecutablePath;
        private readonly IWidgetLogger _logger;
        private readonly object _synchronizationLock = new object();

        public CodexRuntimeSynchronizer(
            string runtimeDirectory,
            IWidgetLogger logger)
        {
            if (string.IsNullOrWhiteSpace(runtimeDirectory))
            {
                throw new ArgumentException(
                    "A private runtime directory is required.",
                    "runtimeDirectory");
            }

            _runtimeDirectory = Path.GetFullPath(runtimeDirectory);
            _currentExecutablePath =
                Path.Combine(_runtimeDirectory, "codex.exe");
            _backupExecutablePath =
                Path.Combine(_runtimeDirectory, "codex.last-good.exe");
            _logger = logger ?? NullWidgetLogger.Instance;
        }

        public string LastKnownGoodExecutablePath
        {
            get
            {
                lock (_synchronizationLock)
                {
                    if (IsTrustedRuntime(_currentExecutablePath))
                    {
                        return _currentExecutablePath;
                    }

                    if (IsTrustedRuntime(_backupExecutablePath))
                    {
                        return _backupExecutablePath;
                    }

                    return null;
                }
            }
        }

        public bool TrySynchronizeFromRunningCodex(
            out string stagedExecutablePath)
        {
            var paths = CodexRuntimeLocator.FindRunningPackageExecutablePaths();
            foreach (var path in paths)
            {
                if (TrySynchronize(path, out stagedExecutablePath))
                {
                    return true;
                }
            }

            return TryUseLastKnownGood(out stagedExecutablePath);
        }

        public bool TrySynchronize(
            string chatGptExecutablePath,
            out string stagedExecutablePath)
        {
            lock (_synchronizationLock)
            {
                stagedExecutablePath = null;
                string temporaryPath = null;
                string previousRuntimeStagingPath = null;
                try
                {
                    var sourcePath = CodexRuntimeLocator.GetBundledCodexPath(
                        chatGptExecutablePath);
                    if (sourcePath == null ||
                        !HaveSamePackageRoot(
                            chatGptExecutablePath,
                            sourcePath) ||
                        !AuthenticodeVerifier.IsTrustedOpenAiExecutable(sourcePath))
                    {
                        _logger.Warn(
                            "The bundled Codex runtime failed package or signature validation.");
                        return TryUseLastKnownGoodUnsafe(
                            out stagedExecutablePath);
                    }

                    Directory.CreateDirectory(_runtimeDirectory);
                    var sourceHash = ComputeSha256(sourcePath);

                    if (File.Exists(_currentExecutablePath) &&
                        IsTrustedRuntime(_currentExecutablePath) &&
                        HashesEqual(
                            sourceHash,
                            ComputeSha256(_currentExecutablePath)))
                    {
                        stagedExecutablePath = _currentExecutablePath;
                        return true;
                    }

                    temporaryPath = Path.Combine(
                        _runtimeDirectory,
                        "codex." + Guid.NewGuid().ToString("N") + ".staging");
                    File.Copy(sourcePath, temporaryPath, false);

                    if (!HashesEqual(sourceHash, ComputeSha256(temporaryPath)) ||
                        !AuthenticodeVerifier.IsTrustedOpenAiExecutable(
                            temporaryPath))
                    {
                        throw new InvalidDataException(
                            "The staged runtime did not pass integrity validation.");
                    }

                    var currentIsTrusted =
                        IsTrustedRuntime(_currentExecutablePath);
                    if (File.Exists(_currentExecutablePath))
                    {
                        if (currentIsTrusted)
                        {
                            previousRuntimeStagingPath = Path.Combine(
                                _runtimeDirectory,
                                "codex." +
                                Guid.NewGuid().ToString("N") +
                                ".previous");
                            File.Copy(
                                _currentExecutablePath,
                                previousRuntimeStagingPath,
                                false);
                            if (!IsTrustedRuntime(previousRuntimeStagingPath))
                            {
                                throw new InvalidDataException(
                                    "The previous runtime could not be preserved safely.");
                            }
                            File.Replace(
                                temporaryPath,
                                _currentExecutablePath,
                                null,
                                true);
                        }
                        else
                        {
                            // Preserve any separately verified backup when the
                            // current file is corrupt or has been tampered with.
                            File.Replace(
                                temporaryPath,
                                _currentExecutablePath,
                                null,
                                true);
                        }
                    }
                    else
                    {
                        File.Move(temporaryPath, _currentExecutablePath);
                    }

                    temporaryPath = null;
                    if (!IsTrustedRuntime(_currentExecutablePath) ||
                        !HashesEqual(
                            sourceHash,
                            ComputeSha256(_currentExecutablePath)))
                    {
                        throw new InvalidDataException(
                            "The installed runtime failed final validation.");
                    }

                    if (previousRuntimeStagingPath != null)
                    {
                        if (File.Exists(_backupExecutablePath))
                        {
                            File.Replace(
                                previousRuntimeStagingPath,
                                _backupExecutablePath,
                                null,
                                true);
                        }
                        else
                        {
                            File.Move(
                                previousRuntimeStagingPath,
                                _backupExecutablePath);
                        }

                        previousRuntimeStagingPath = null;
                    }

                    stagedExecutablePath = _currentExecutablePath;
                    _logger.Info("The private Codex runtime is synchronized.");
                    return true;
                }
                catch (Exception exception)
                {
                    _logger.Warn(
                        "Codex runtime synchronization failed (" +
                        exception.GetType().Name +
                        "); retaining the last verified runtime.");
                    return TryUseLastKnownGoodUnsafe(
                        out stagedExecutablePath);
                }
                finally
                {
                    if (temporaryPath != null)
                    {
                        try
                        {
                            if (File.Exists(temporaryPath))
                            {
                                File.Delete(temporaryPath);
                            }
                        }
                        catch (Exception)
                        {
                        }
                    }

                    if (previousRuntimeStagingPath != null)
                    {
                        try
                        {
                            if (File.Exists(previousRuntimeStagingPath))
                            {
                                File.Delete(previousRuntimeStagingPath);
                            }
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
            }
        }

        private bool TryUseLastKnownGood(out string stagedExecutablePath)
        {
            lock (_synchronizationLock)
            {
                return TryUseLastKnownGoodUnsafe(out stagedExecutablePath);
            }
        }

        private bool TryUseLastKnownGoodUnsafe(
            out string stagedExecutablePath)
        {
            if (IsTrustedRuntime(_currentExecutablePath))
            {
                stagedExecutablePath = _currentExecutablePath;
                return true;
            }

            if (IsTrustedRuntime(_backupExecutablePath))
            {
                stagedExecutablePath = _backupExecutablePath;
                return true;
            }

            stagedExecutablePath = null;
            return false;
        }

        private static bool HaveSamePackageRoot(
            string left,
            string right)
        {
            var leftRoot = CodexRuntimeLocator.GetPackageRoot(left);
            var rightRoot = CodexRuntimeLocator.GetPackageRoot(right);
            return leftRoot != null &&
                rightRoot != null &&
                string.Equals(
                    leftRoot,
                    rightRoot,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTrustedRuntime(string path)
        {
            return File.Exists(path) &&
                AuthenticodeVerifier.IsTrustedOpenAiExecutable(path);
        }

        private static byte[] ComputeSha256(string path)
        {
            using (var algorithm = SHA256.Create())
            using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            {
                return algorithm.ComputeHash(stream);
            }
        }

        private static bool HashesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }

            var difference = 0;
            for (var index = 0; index < left.Length; index++)
            {
                difference |= left[index] ^ right[index];
            }

            return difference == 0;
        }
    }

    internal static class AuthenticodeVerifier
    {
        private static readonly Guid GenericVerifyV2 =
            new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        private const uint UiNone = 2;
        private const uint RevokeNone = 0;
        private const uint ChoiceFile = 1;
        private const uint StateActionIgnore = 0;
        private const uint RevocationCheckNone = 0x00000010;
        private const uint CacheOnlyUrlRetrieval = 0x00001000;

        public static bool IsTrustedOpenAiExecutable(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return false;
            }

            var fileInfo = new WinTrustFileInfo(path);
            var fileInfoPointer = IntPtr.Zero;
            try
            {
                fileInfoPointer = Marshal.AllocCoTaskMem(
                    Marshal.SizeOf(typeof(WinTrustFileInfo)));
                Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);

                var trustData = new WinTrustData();
                trustData.StructSize =
                    (uint)Marshal.SizeOf(typeof(WinTrustData));
                trustData.UiChoice = UiNone;
                trustData.RevocationChecks = RevokeNone;
                trustData.UnionChoice = ChoiceFile;
                trustData.File = fileInfoPointer;
                trustData.StateAction = StateActionIgnore;
                trustData.ProviderFlags =
                    RevocationCheckNone | CacheOnlyUrlRetrieval;

                var action = GenericVerifyV2;
                var status = WinVerifyTrust(
                    new IntPtr(-1),
                    ref action,
                    ref trustData);
                if (status != 0)
                {
                    return false;
                }

                using (var certificate = new X509Certificate2(
                    X509Certificate.CreateFromSignedFile(path)))
                {
                    var publisher = certificate.GetNameInfo(
                        X509NameType.SimpleName,
                        false);
                    return string.Equals(
                        publisher,
                        "OpenAI OpCo, LLC",
                        StringComparison.Ordinal);
                }
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (fileInfoPointer != IntPtr.Zero)
                {
                    Marshal.DestroyStructure(
                        fileInfoPointer,
                        typeof(WinTrustFileInfo));
                    Marshal.FreeCoTaskMem(fileInfoPointer);
                }
            }
        }

        [DllImport(
            "wintrust.dll",
            ExactSpelling = true,
            PreserveSig = true,
            SetLastError = false)]
        private static extern int WinVerifyTrust(
            IntPtr windowHandle,
            [In] ref Guid actionId,
            [In] ref WinTrustData trustData);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private sealed class WinTrustFileInfo
        {
            public WinTrustFileInfo(string path)
            {
                StructSize =
                    (uint)Marshal.SizeOf(typeof(WinTrustFileInfo));
                FilePath = path;
                FileHandle = IntPtr.Zero;
                KnownSubject = IntPtr.Zero;
            }

            public uint StructSize;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string FilePath;
            public IntPtr FileHandle;
            public IntPtr KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustData
        {
            public uint StructSize;
            public IntPtr PolicyCallbackData;
            public IntPtr SipClientData;
            public uint UiChoice;
            public uint RevocationChecks;
            public uint UnionChoice;
            public IntPtr File;
            public uint StateAction;
            public IntPtr StateData;
            public IntPtr UrlReference;
            public uint ProviderFlags;
            public uint UiContext;
        }
    }
}
