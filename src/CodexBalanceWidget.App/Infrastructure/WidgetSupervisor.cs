using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CodexBalanceWidget.App.ViewModels;
using CodexBalanceWidget.Core;

namespace CodexBalanceWidget.App.Infrastructure
{
    public sealed class WidgetSupervisor : IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private readonly WidgetWindow _window;
        private readonly WidgetViewModel _viewModel;
        private readonly CodexRuntimeSynchronizer _runtimeSynchronizer;
        private readonly Func<string, IRateLimitSource> _rateLimitSourceFactory;
        private readonly IWidgetLogger _logger;
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private readonly object _sourceGate = new object();
        private Task _runTask;
        private IRateLimitSource _activeSource;
        private bool _disposed;

        public WidgetSupervisor(
            Dispatcher dispatcher,
            WidgetWindow window,
            WidgetViewModel viewModel,
            CodexRuntimeSynchronizer runtimeSynchronizer,
            Func<string, IRateLimitSource> rateLimitSourceFactory,
            IWidgetLogger logger)
        {
            _dispatcher = dispatcher;
            _window = window;
            _viewModel = viewModel;
            _runtimeSynchronizer = runtimeSynchronizer;
            _rateLimitSourceFactory = rateLimitSourceFactory;
            _logger = logger;
        }

        public void Start()
        {
            if (_runTask != null)
            {
                return;
            }

            _logger.Info("Widget supervisor started.");
            _runTask = Task.Run(
                delegate { return RunAsync(_shutdown.Token); },
                _shutdown.Token);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _shutdown.Cancel();
            var runTask = _runTask;
            if (runTask != null)
            {
                try
                {
                    runTask.Wait(TimeSpan.FromSeconds(3.0));
                }
                catch (AggregateException exception)
                {
                    var flattened = exception.Flatten();
                    foreach (var item in flattened.InnerExceptions)
                    {
                        if (!(item is OperationCanceledException))
                        {
                            _logger.Error("Supervisor shutdown failed.", item);
                        }
                    }
                }
            }

            StopSourceSynchronously();
            _shutdown.Dispose();
            _logger.Info("Widget supervisor stopped.");
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            var retryDelaySeconds = 1;
            var nextStartAttempt = DateTimeOffset.MinValue;
            var wasCodexRunning = false;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    IList<string> packageExecutablePaths;
                    try
                    {
                        packageExecutablePaths =
                            CodexRuntimeLocator.FindRunningPackageExecutablePaths();
                    }
                    catch (Exception exception)
                    {
                        _logger.Error("Could not inspect running Codex processes.", exception);
                        packageExecutablePaths = new List<string>();
                    }

                    var codexRunning = packageExecutablePaths != null &&
                                       packageExecutablePaths.Count > 0;
                    if (codexRunning != wasCodexRunning)
                    {
                        wasCodexRunning = codexRunning;
                        Dispatch(
                            delegate
                            {
                                _window.SetCodexRunning(codexRunning);
                                if (!codexRunning)
                                {
                                    _viewModel.SetWaitingForCodex();
                                }
                            });
                    }

                    if (!codexRunning)
                    {
                        if (GetActiveSource() != null)
                        {
                            await StopSourceAsync().ConfigureAwait(false);
                        }

                        retryDelaySeconds = 1;
                        nextStartAttempt = DateTimeOffset.MinValue;
                        await Task.Delay(700, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (GetActiveSource() == null &&
                        DateTimeOffset.UtcNow >= nextStartAttempt)
                    {
                        Dispatch(
                            delegate
                            {
                                _window.SetCodexRunning(true);
                                _viewModel.SetConnecting();
                            });

                        try
                        {
                            var stagedExecutablePath = SynchronizeRuntime(
                                packageExecutablePaths);
                            if (string.IsNullOrEmpty(stagedExecutablePath) ||
                                !File.Exists(stagedExecutablePath))
                            {
                                throw new FileNotFoundException(
                                    "No private Codex app-server runtime is available.");
                            }

                            var source = _rateLimitSourceFactory(stagedExecutablePath);
                            source.SnapshotUpdated += OnSnapshotUpdated;
                            SetActiveSource(source);

                            await source.StartAsync(cancellationToken).ConfigureAwait(false);

                            retryDelaySeconds = 1;
                            nextStartAttempt = DateTimeOffset.MinValue;
                            _logger.Info("Codex app-server source connected.");
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            _logger.Error("Could not start the Codex app-server source.", exception);
                            StopSourceSynchronously();
                            Dispatch(
                                delegate
                                {
                                    _viewModel.SetRuntimeError(
                                        "同步暂时中断，正在重试");
                                });
                            nextStartAttempt =
                                DateTimeOffset.UtcNow.AddSeconds(retryDelaySeconds);
                            retryDelaySeconds = Math.Min(30, retryDelaySeconds * 2);
                        }
                    }

                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                StopSourceSynchronously();
                Dispatch(delegate { _window.SetCodexRunning(false); });
            }
        }

        private string SynchronizeRuntime(IList<string> packageExecutablePaths)
        {
            string stagedExecutablePath;
            foreach (var packagePath in packageExecutablePaths)
            {
                if (_runtimeSynchronizer.TrySynchronize(
                    packagePath,
                    out stagedExecutablePath))
                {
                    return stagedExecutablePath;
                }
            }

            if (_runtimeSynchronizer.TrySynchronizeFromRunningCodex(
                out stagedExecutablePath))
            {
                return stagedExecutablePath;
            }

            return _runtimeSynchronizer.LastKnownGoodExecutablePath;
        }

        private void OnSnapshotUpdated(
            object sender,
            RateLimitSnapshotEventArgs eventArgs)
        {
            if (eventArgs == null || eventArgs.Snapshot == null)
            {
                return;
            }

            PublishSnapshot(eventArgs.Snapshot);
        }

        private void PublishSnapshot(RateLimitSnapshot snapshot)
        {
            Dispatch(delegate { _viewModel.ApplySnapshot(snapshot); });
        }

        private void Dispatch(Action action)
        {
            if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
            {
                return;
            }

            if (_dispatcher.CheckAccess())
            {
                action();
                return;
            }

            try
            {
                _dispatcher.BeginInvoke(DispatcherPriority.Background, action);
            }
            catch (TaskCanceledException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        private IRateLimitSource GetActiveSource()
        {
            lock (_sourceGate)
            {
                return _activeSource;
            }
        }

        private void SetActiveSource(IRateLimitSource source)
        {
            lock (_sourceGate)
            {
                _activeSource = source;
            }
        }

        private IRateLimitSource TakeActiveSource()
        {
            lock (_sourceGate)
            {
                var result = _activeSource;
                _activeSource = null;
                return result;
            }
        }

        private async Task StopSourceAsync()
        {
            var source = TakeActiveSource();
            if (source == null)
            {
                return;
            }

            source.SnapshotUpdated -= OnSnapshotUpdated;
            try
            {
                await source.StopAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.Error("Could not stop the Codex app-server source cleanly.", exception);
            }
            finally
            {
                source.Dispose();
            }
        }

        private void StopSourceSynchronously()
        {
            var source = TakeActiveSource();
            if (source == null)
            {
                return;
            }

            source.SnapshotUpdated -= OnSnapshotUpdated;
            try
            {
                var stopTask = source.StopAsync();
                if (stopTask != null)
                {
                    stopTask.Wait(TimeSpan.FromSeconds(2.0));
                }
            }
            catch (Exception exception)
            {
                _logger.Error("Final app-server cleanup failed.", exception);
            }
            finally
            {
                source.Dispose();
            }
        }
    }
}
