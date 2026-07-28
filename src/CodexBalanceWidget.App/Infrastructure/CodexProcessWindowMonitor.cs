using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CodexBalanceWidget.Core;

namespace CodexBalanceWidget.App.Infrastructure
{
    public sealed class CodexProcessWindowMonitor
    {
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const int GwlExStyle = -20;
        private const long WsExToolWindow = 0x00000080L;
        private const uint GwOwner = 4;
        private readonly object _cacheGate = new object();
        private readonly Dictionary<int, ProcessPathCacheEntry> _processPathCache =
            new Dictionary<int, ProcessPathCacheEntry>();

        public bool IsAnyPrimaryCodexWindowMaximized()
        {
            var found = false;
            EnumWindows(
                delegate(IntPtr window, IntPtr parameter)
                {
                    if (!IsWindowVisible(window) || IsIconic(window))
                    {
                        return true;
                    }

                    if (GetWindow(window, GwOwner) != IntPtr.Zero)
                    {
                        return true;
                    }

                    var extendedStyle = GetWindowLongPtr(window, GwlExStyle).ToInt64();
                    if ((extendedStyle & WsExToolWindow) != 0)
                    {
                        return true;
                    }

                    int processId;
                    GetWindowThreadProcessId(window, out processId);
                    if (processId <= 0 || !IsCodexPackageProcess(processId))
                    {
                        return true;
                    }

                    if (IsZoomed(window))
                    {
                        found = true;
                        return false;
                    }

                    return true;
                },
                IntPtr.Zero);
            return found;
        }

        private bool IsCodexPackageProcess(int processId)
        {
            ProcessPathCacheEntry entry;
            lock (_cacheGate)
            {
                if (_processPathCache.TryGetValue(processId, out entry) &&
                    DateTime.UtcNow - entry.CheckedAtUtc < TimeSpan.FromSeconds(1.0))
                {
                    return entry.IsCodex;
                }
            }

            var executablePath = TryGetProcessImagePath(processId);
            var isCodex = false;
            if (!string.IsNullOrEmpty(executablePath))
            {
                try
                {
                    isCodex = CodexRuntimeLocator.IsCodexPackagePath(executablePath);
                }
                catch
                {
                    isCodex = false;
                }
            }

            lock (_cacheGate)
            {
                _processPathCache[processId] =
                    new ProcessPathCacheEntry(DateTime.UtcNow, isCodex);
                RemoveExpiredCacheEntries();
            }

            return isCodex;
        }

        private static string TryGetProcessImagePath(int processId)
        {
            var processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (processHandle != IntPtr.Zero)
            {
                try
                {
                    var capacity = 32768;
                    var buffer = new StringBuilder(capacity);
                    if (QueryFullProcessImageName(processHandle, 0, buffer, ref capacity))
                    {
                        return buffer.ToString();
                    }
                }
                finally
                {
                    CloseHandle(processHandle);
                }
            }

            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    return process.MainModule == null
                        ? null
                        : process.MainModule.FileName;
                }
            }
            catch
            {
                return null;
            }
        }

        private void RemoveExpiredCacheEntries()
        {
            if (_processPathCache.Count < 64)
            {
                return;
            }

            var cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(10.0);
            var expired = new List<int>();
            foreach (var item in _processPathCache)
            {
                if (item.Value.CheckedAtUtc < cutoff)
                {
                    expired.Add(item.Key);
                }
            }

            foreach (var processId in expired)
            {
                _processPathCache.Remove(processId);
            }
        }

        private sealed class ProcessPathCacheEntry
        {
            public ProcessPathCacheEntry(DateTime checkedAtUtc, bool isCodex)
            {
                CheckedAtUtc = checkedAtUtc;
                IsCodex = isCodex;
            }

            public DateTime CheckedAtUtc { get; private set; }
            public bool IsCodex { get; private set; }
        }

        private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

        private static IntPtr GetWindowLongPtr(IntPtr window, int index)
        {
            return IntPtr.Size == 8
                ? GetWindowLongPtr64(window, index)
                : new IntPtr(GetWindowLong32(window, index));
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(
            EnumWindowsCallback callback,
            IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsZoomed(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr window, uint command);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(
            IntPtr window,
            out int processId);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong32(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            int processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(
            IntPtr process,
            int flags,
            StringBuilder executableName,
            ref int size);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
