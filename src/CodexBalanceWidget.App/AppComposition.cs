using System;
using System.IO;
using System.Windows.Threading;
using CodexBalanceWidget.App.Infrastructure;
using CodexBalanceWidget.App.ViewModels;
using CodexBalanceWidget.Core;

namespace CodexBalanceWidget.App
{
    // This is the only file that knows which Core runtime/source implementations
    // are used. Protocol or constructor changes should be adapted here.
    public sealed class AppComposition : IDisposable
    {
        private readonly WidgetWindow _window;
        private readonly WidgetSupervisor _supervisor;
        private bool _disposed;

        public AppComposition(Dispatcher dispatcher)
        {
            var localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            var stateDirectory = Path.Combine(localAppData, "CodexBalanceWidget");
            var logDirectory = Path.Combine(stateDirectory, "logs");
            var runtimeDirectory = Path.Combine(stateDirectory, "runtime");

            Logger = new RollingFileLogger(logDirectory);
            var viewModel = new WidgetViewModel();
            var windowMonitor = new CodexProcessWindowMonitor();
            var settingsStore = new WidgetSettingsStore(stateDirectory, Logger);
            var avatarImageService = new AvatarImageService(stateDirectory, Logger);
            var settings = settingsStore.Load();
            _window = new WidgetWindow(
                viewModel,
                windowMonitor,
                settingsStore,
                avatarImageService,
                settings);

            var synchronizer = new CodexRuntimeSynchronizer(runtimeDirectory, Logger);
            _supervisor = new WidgetSupervisor(
                dispatcher,
                _window,
                viewModel,
                synchronizer,
                CreateRateLimitSource,
                Logger);
        }

        public RollingFileLogger Logger { get; private set; }

        public void Start()
        {
            _supervisor.Start();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _supervisor.Dispose();
            _window.StopTimers();
            _window.Close();
            Logger.Dispose();
        }

        private IRateLimitSource CreateRateLimitSource(string executablePath)
        {
            return new CodexAppServerRateLimitSource(executablePath, Logger);
        }
    }
}
