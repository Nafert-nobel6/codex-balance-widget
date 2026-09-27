using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexBalanceWidget.Core
{
    /// <summary>
    /// A read-only JSONL client for the stable Codex app-server rate-limit API.
    /// This type never reads credential files and exposes no reset-consumption API.
    /// </summary>
    public sealed class CodexAppServerRateLimitSource : IRateLimitSource
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
        private const int MaxJsonMessageCharacters = 1024 * 1024;
        private const int MaxDiagnosticLineCharacters = 64 * 1024;
        private const int MaxJsonRecursionDepth = 64;

        private readonly string _executablePath;
        private readonly IWidgetLogger _logger;
        private readonly object _lifecycleLock = new object();
        private readonly SemaphoreSlim _refreshLock = new SemaphoreSlim(1, 1);

        private CancellationTokenSource _lifetimeCancellation;
        private Task _supervisorTask;
        private AppServerConnection _connection;
        private RateLimitSnapshot _lastSnapshot;
        private bool _disposed;

        public CodexAppServerRateLimitSource(
            string executablePath,
            IWidgetLogger logger)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                throw new ArgumentException(
                    "An app-server executable path is required.",
                    "executablePath");
            }

            _executablePath = Path.GetFullPath(executablePath);
            _logger = logger ?? NullWidgetLogger.Instance;
        }

        public event EventHandler<RateLimitSnapshotEventArgs> SnapshotUpdated;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            lock (_lifecycleLock)
            {
                ThrowIfDisposed();
                if (_lifetimeCancellation != null)
                {
                    return CompletedTask();
                }

                cancellationToken.ThrowIfCancellationRequested();
                _lifetimeCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var lifetimeToken = _lifetimeCancellation.Token;
                _supervisorTask = Task.Run(
                    delegate
                    {
                        return RunSupervisorAsync(lifetimeToken);
                    });
                return CompletedTask();
            }
        }

        public async Task<RateLimitSnapshot> RefreshAsync(
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var connection = GetActiveConnection();
                EnsureCurrentAccount(connection);
                var accountResult = await connection.RequestAsync(
                    "account/read",
                    new Dictionary<string, object> { { "refreshToken", false } },
                    cancellationToken).ConfigureAwait(false) as IDictionary<string, object>;
                EnsureCurrentAccount(connection);
                object account;
                if (accountResult == null || !accountResult.TryGetValue("account", out account))
                {
                    throw new InvalidDataException("The account response is invalid.");
                }
                var accountObject = account as IDictionary<string, object>;
                object accountType;
                if (accountObject == null ||
                    !accountObject.TryGetValue("type", out accountType) ||
                    !string.Equals(accountType as string, "chatgpt", StringComparison.Ordinal))
                {
                    return ClearAccountSnapshot(accountObject == null
                        ? "请在 Codex 中登录账号"
                        : "当前登录方式不提供 ChatGPT 额度");
                }
                var result = await connection.RequestAsync(
                    "account/rateLimits/read",
                    null,
                    cancellationToken).ConfigureAwait(false);
                var resultObject = result as IDictionary<string, object>;
                if (resultObject == null)
                {
                    throw new InvalidDataException(
                        "The rate-limit response did not contain an object.");
                }

                var snapshot = RateLimitSnapshotParser.ParseResponseObject(
                    resultObject,
                    DateTimeOffset.UtcNow);
                lock (_lifecycleLock)
                {
                    EnsureCurrentAccount(connection);
                    _lastSnapshot = snapshot;
                    Publish(snapshot);
                }

                return snapshot;
            }
            catch
            {
                PublishStaleSnapshot();
                throw;
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        public async Task StopAsync()
        {
            CancellationTokenSource cancellation;
            Task supervisor;
            AppServerConnection connection;
            lock (_lifecycleLock)
            {
                cancellation = _lifetimeCancellation;
                supervisor = _supervisorTask;
                connection = _connection;
            }

            if (cancellation == null)
            {
                return;
            }

            cancellation.Cancel();
            if (connection != null)
            {
                connection.Dispose();
            }

            if (supervisor != null)
            {
                try
                {
                    await supervisor.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            lock (_lifecycleLock)
            {
                if (ReferenceEquals(_lifetimeCancellation, cancellation))
                {
                    _lifetimeCancellation = null;
                    _supervisorTask = null;
                    _connection = null;
                }
            }

            cancellation.Dispose();
        }

        public void Dispose()
        {
            lock (_lifecycleLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            try
            {
                StopAsync().GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // Dispose is best effort. StopAsync already terminates the child.
            }

            _refreshLock.Dispose();
        }

        private async Task RunSupervisorAsync(CancellationToken cancellationToken)
        {
            var backoffSeconds = 1;
            while (!cancellationToken.IsCancellationRequested)
            {
                AppServerConnection connection = null;
                Task accountWatch = null;
                try
                {
                    connection = new AppServerConnection(
                        _executablePath,
                        _logger,
                        RequestTimeout);
                    connection.Start(cancellationToken);
                    accountWatch = WatchAccountAsync(connection, cancellationToken);
                    lock (_lifecycleLock)
                    {
                        _connection = connection;
                    }

                    await connection.InitializeAsync(cancellationToken)
                        .ConfigureAwait(false);
                    _logger.Info("Codex app-server connection initialized.");
                    backoffSeconds = 1;

                    try
                    {
                        await RefreshAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        _logger.Warn(
                            "Initial rate-limit refresh failed (" +
                            exception.GetType().Name +
                            ").");
                    }

                    await MonitorConnectionAsync(connection, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Dispose the connection and join its watcher below.
                }
                catch (Exception exception)
                {
                    PublishStaleSnapshot();
                    _logger.Warn(
                        "Codex app-server stopped unexpectedly (" +
                        exception.GetType().Name +
                        "); retrying.");
                }
                finally
                {
                    lock (_lifecycleLock)
                    {
                        if (ReferenceEquals(_connection, connection))
                        {
                            _connection = null;
                        }
                    }

                    if (connection != null)
                    {
                        connection.Dispose();
                    }
                }

                if (accountWatch != null)
                {
                    try { await accountWatch.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(backoffSeconds),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                backoffSeconds = Math.Min(30, backoffSeconds * 2);
            }
        }

        private async Task MonitorConnectionAsync(
            AppServerConnection connection,
            CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using (var waitCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken))
                {
                    var notificationOrPollTask =
                        connection.RateLimitChanged.WaitAsync(
                            PollInterval,
                            waitCancellation.Token);
                    var completed = await Task.WhenAny(
                        connection.ReaderTask,
                        notificationOrPollTask).ConfigureAwait(false);

                    if (ReferenceEquals(completed, connection.ReaderTask))
                    {
                        waitCancellation.Cancel();
                        try
                        {
                            await notificationOrPollTask.ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                        }

                        await connection.ReaderTask.ConfigureAwait(false);
                        throw new EndOfStreamException(
                            "The Codex app-server output stream closed.");
                    }

                    await notificationOrPollTask.ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();

                while (connection.RateLimitChanged.Wait(0))
                {
                    // Coalesce a burst of sparse notifications into one full read.
                }

                try
                {
                    await RefreshAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (TimeoutException)
                {
                    throw;
                }
                catch (IOException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.Warn(
                        "Rate-limit refresh failed (" +
                        exception.GetType().Name +
                        "); reconnecting before retry.");
                    throw;
                }
            }
        }

        private async Task WatchAccountAsync(
            AppServerConnection connection, CancellationToken cancellationToken)
        {
            while (!connection.IsDisposed)
            {
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                if (connection.IsDisposed) { return; }
                if (connection.AuthenticationStateChanged)
                {
                    lock (_lifecycleLock)
                    {
                        ClearAccountSnapshot("账号状态已变化，正在重新同步");
                        connection.Dispose();
                    }
                    return;
                }
            }
        }

        private void EnsureCurrentAccount(AppServerConnection connection)
        {
            if (connection.AuthenticationStateChanged)
            {
                ClearAccountSnapshot("账号状态已变化，正在重新同步");
                connection.Dispose();
                throw new IOException("Account state changed; reconnecting.");
            }
        }

        private RateLimitSnapshot ClearAccountSnapshot(string message)
        {
            lock (_lifecycleLock)
            {
                var snapshot = new RateLimitSnapshot(
                    new List<QuotaWindow>(), 0, false, new List<ResetCredit>(),
                    DateTimeOffset.UtcNow, message, true);
                _lastSnapshot = snapshot;
                Publish(snapshot);
                return snapshot;
            }
        }

        // Only file metadata is inspected. Credentials are never opened or parsed.
        private static string ReadAuthenticationStamp()
        {
            try
            {
                var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
                if (string.IsNullOrWhiteSpace(codexHome))
                {
                    codexHome = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
                }
                var file = new FileInfo(Path.Combine(codexHome, "auth.json"));
                file.Refresh();
                return file.Exists
                    ? file.CreationTimeUtc.Ticks + ":" + file.LastWriteTimeUtc.Ticks + ":" + file.Length
                    : "missing";
            }
            catch (IOException) { return "unavailable"; }
            catch (UnauthorizedAccessException) { return "unavailable"; }
        }

        private AppServerConnection GetActiveConnection()
        {
            lock (_lifecycleLock)
            {
                ThrowIfDisposed();
                if (_connection == null || !_connection.IsInitialized)
                {
                    throw new InvalidOperationException(
                        "The Codex app-server is not initialized.");
                }

                return _connection;
            }
        }

        private void PublishStaleSnapshot()
        {
            RateLimitSnapshot previous;
            lock (_lifecycleLock)
            {
                previous = _lastSnapshot;
                if (previous == null || previous.IsStale)
                {
                    return;
                }

                previous = new RateLimitSnapshot(
                    new List<QuotaWindow>(previous.Windows),
                    previous.AvailableCreditCount,
                    previous.CreditDetailsAvailable,
                    new List<ResetCredit>(previous.Credits),
                    previous.ObservedAt,
                    "Temporarily unavailable; showing the last successful update.",
                    true);
                _lastSnapshot = previous;
                Publish(previous);
            }
        }

        private void Publish(RateLimitSnapshot snapshot)
        {
            var handler = SnapshotUpdated;
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(this, new RateLimitSnapshotEventArgs(snapshot));
            }
            catch (Exception exception)
            {
                _logger.Warn(
                    "A snapshot subscriber failed (" +
                    exception.GetType().Name +
                    ").");
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    "CodexAppServerRateLimitSource");
            }
        }

        private static Task CompletedTask()
        {
            return Task.FromResult(true);
        }

        private sealed class AppServerConnection : IDisposable
        {
            private readonly string _executablePath;
            private readonly IWidgetLogger _logger;
            private readonly TimeSpan _requestTimeout;
            private readonly object _pendingLock = new object();
            private readonly Dictionary<string, TaskCompletionSource<Response>>
                _pending =
                    new Dictionary<string, TaskCompletionSource<Response>>(
                        StringComparer.Ordinal);
            private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

            private Process _process;
            private StreamWriter _input;
            private long _nextRequestId;
            private bool _disposed;
            private readonly string _authenticationStamp;
            private int _accountChanged;
            private string _accountNotificationStamp;

            public AppServerConnection(
                string executablePath,
                IWidgetLogger logger,
                TimeSpan requestTimeout)
            {
                _executablePath = executablePath;
                _logger = logger;
                _requestTimeout = requestTimeout;
                _authenticationStamp = ReadAuthenticationStamp();
                RateLimitChanged = new SemaphoreSlim(0);
            }

            public SemaphoreSlim RateLimitChanged { get; private set; }
            public Task ReaderTask { get; private set; }
            public bool IsInitialized { get; private set; }
            public bool IsDisposed { get { return _disposed; } }
            public bool AuthenticationStateChanged
            {
                get
                {
                    return Interlocked.CompareExchange(ref _accountChanged, 0, 0) != 0 ||
                        !string.Equals(_authenticationStamp, ReadAuthenticationStamp(), StringComparison.Ordinal);
                }
            }

            public void Start(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var startInfo = new ProcessStartInfo();
                startInfo.FileName = _executablePath;
                startInfo.Arguments = "app-server --listen stdio://";
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                startInfo.RedirectStandardInput = true;
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;
                startInfo.StandardOutputEncoding = new UTF8Encoding(false);
                startInfo.StandardErrorEncoding = new UTF8Encoding(false);

                var process = new Process();
                process.StartInfo = startInfo;
                process.EnableRaisingEvents = true;
                if (!process.Start())
                {
                    process.Dispose();
                    throw new InvalidOperationException(
                        "The Codex app-server process did not start.");
                }

                _process = process;
                _input = new StreamWriter(
                    process.StandardInput.BaseStream,
                    new UTF8Encoding(false),
                    4096);
                _input.AutoFlush = true;
                ReaderTask = ReadLoopAsync(
                    process.StandardOutput,
                    cancellationToken);
                var errorTask = DrainErrorAsync(
                    process.StandardError,
                    cancellationToken);
                ObserveFault(errorTask);
            }

            public async Task InitializeAsync(CancellationToken cancellationToken)
            {
                var clientInfo = new Dictionary<string, object>();
                clientInfo["name"] = "codex_balance_widget";
                clientInfo["title"] = "Codex Balance Widget";
                clientInfo["version"] = "1.1.0";

                var parameters = new Dictionary<string, object>();
                parameters["clientInfo"] = clientInfo;

                await RequestAsync(
                    "initialize",
                    parameters,
                    cancellationToken).ConfigureAwait(false);
                await SendNotificationAsync(
                    "initialized",
                    null,
                    cancellationToken).ConfigureAwait(false);
                IsInitialized = true;
            }

            public async Task<object> RequestAsync(
                string method,
                IDictionary<string, object> parameters,
                CancellationToken cancellationToken)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException("AppServerConnection");
                }

                var id = Interlocked.Increment(ref _nextRequestId);
                var idKey = id.ToString(CultureInfo.InvariantCulture);
                var completion = new TaskCompletionSource<Response>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                lock (_pendingLock)
                {
                    if (_disposed)
                    {
                        throw new ObjectDisposedException("AppServerConnection");
                    }

                    _pending.Add(idKey, completion);
                }

                var request = new Dictionary<string, object>();
                request["method"] = method;
                request["id"] = id;
                if (parameters != null)
                {
                    request["params"] = parameters;
                }

                try
                {
                    await SendAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    lock (_pendingLock)
                    {
                        _pending.Remove(idKey);
                    }

                    throw;
                }

                var timeoutTask = Task.Delay(_requestTimeout, cancellationToken);
                var completed = await Task.WhenAny(
                    completion.Task,
                    timeoutTask).ConfigureAwait(false);
                if (!ReferenceEquals(completed, completion.Task))
                {
                    lock (_pendingLock)
                    {
                        _pending.Remove(idKey);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    throw new TimeoutException(
                        "The Codex app-server request timed out.");
                }

                var response = await completion.Task.ConfigureAwait(false);
                if (response.HasError)
                {
                    throw new InvalidOperationException(
                        "The Codex app-server rejected the request.");
                }

                return response.Result;
            }

            public void Dispose()
            {
                lock (_pendingLock)
                {
                    if (_disposed)
                    {
                        return;
                    }
                    _disposed = true;
                }
                IsInitialized = false;
                FailPending(
                    new IOException("The Codex app-server connection closed."));

                try
                {
                    if (_input != null)
                    {
                        _input.Dispose();
                    }
                }
                catch (Exception)
                {
                }

                try
                {
                    if (_process != null && !_process.HasExited)
                    {
                        _process.Kill();
                        _process.WaitForExit(2000);
                    }
                }
                catch (Exception)
                {
                }

                if (_process != null)
                {
                    _process.Dispose();
                }

                // Waiters/writers may still be unwinding after process termination.
                // These managed semaphores are collected with the connection.
            }

            private async Task ReadLoopAsync(
                StreamReader output,
                CancellationToken cancellationToken)
            {
                var boundedOutput = new BoundedLineReader(
                    output,
                    MaxJsonMessageCharacters);
                try
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        var line = await boundedOutput.ReadLineAsync(
                            cancellationToken).ConfigureAwait(false);
                        if (line == null)
                        {
                            return;
                        }

                        if (boundedOutput.LastLineWasTruncated)
                        {
                            _logger.Warn(
                                "Codex app-server emitted an oversized JSONL message.");
                            continue;
                        }

                        IDictionary<string, object> message;
                        try
                        {
                            var serializer = new JavaScriptSerializer();
                            serializer.MaxJsonLength = MaxJsonMessageCharacters;
                            serializer.RecursionLimit = MaxJsonRecursionDepth;
                            message = serializer.DeserializeObject(line)
                                as IDictionary<string, object>;
                        }
                        catch (Exception)
                        {
                            _logger.Warn(
                                "Codex app-server emitted an invalid JSONL message.");
                            continue;
                        }

                        if (message == null)
                        {
                            continue;
                        }

                        object id;
                        object methodValue;
                        if (message.TryGetValue("method", out methodValue))
                        {
                            var method = methodValue as string;
                            if (string.Equals(
                                method, "account/updated", StringComparison.Ordinal))
                            {
                                object notificationParameters;
                                if (message.TryGetValue("params", out notificationParameters))
                                {
                                    var accountState = notificationParameters as IDictionary<string, object>;
                                    object mode;
                                    object plan;
                                    if (accountState != null && accountState.TryGetValue("authMode", out mode))
                                    {
                                        accountState.TryGetValue("planType", out plan);
                                        var stamp = (mode as string ?? string.Empty) + "\n" +
                                            (plan as string ?? string.Empty);
                                        // account/read can emit an unchanged account/updated notification.
                                        // Establish a baseline and reconnect only for a real state change.
                                        if (_accountNotificationStamp != null &&
                                            !string.Equals(_accountNotificationStamp, stamp, StringComparison.Ordinal))
                                        {
                                            Interlocked.Exchange(ref _accountChanged, 1);
                                        }
                                        _accountNotificationStamp = stamp;
                                    }
                                }
                            }
                            if (string.Equals(
                                method,
                                "account/rateLimits/updated",
                                StringComparison.Ordinal))
                            {
                                if (RateLimitChanged.CurrentCount == 0)
                                {
                                    RateLimitChanged.Release();
                                }
                            }

                            if (message.TryGetValue("id", out id) && id != null)
                            {
                                var responseTask = SendMethodNotFoundAsync(
                                    id,
                                    cancellationToken);
                                ObserveFault(responseTask);
                            }

                            continue;
                        }

                        if (!message.TryGetValue("id", out id) || id == null)
                        {
                            continue;
                        }

                        var idKey = Convert.ToString(
                            id,
                            CultureInfo.InvariantCulture);
                        TaskCompletionSource<Response> completion = null;
                        lock (_pendingLock)
                        {
                            if (_pending.TryGetValue(idKey, out completion))
                            {
                                _pending.Remove(idKey);
                            }
                        }

                        if (completion == null)
                        {
                            continue;
                        }

                        object error;
                        object result;
                        var hasError =
                            message.TryGetValue("error", out error) &&
                            error != null;
                        message.TryGetValue("result", out result);
                        completion.TrySetResult(
                            new Response(result, hasError));
                    }
                }
                finally
                {
                    FailPending(
                        new IOException(
                            "The Codex app-server output stream closed."));
                }
            }

            private async Task DrainErrorAsync(
                StreamReader error,
                CancellationToken cancellationToken)
            {
                var boundedError = new BoundedLineReader(
                    error,
                    MaxDiagnosticLineCharacters);
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await boundedError.ReadLineAsync(
                        cancellationToken).ConfigureAwait(false);
                    if (line == null)
                    {
                        return;
                    }

                    if (boundedError.LastLineWasTruncated)
                    {
                        _logger.Warn(
                            "Codex app-server emitted an oversized diagnostic line.");
                        continue;
                    }

                    var safeLine = SensitiveDataRedactor.RedactDiagnostic(line);
                    if (!string.IsNullOrWhiteSpace(safeLine))
                    {
                        _logger.Warn("Codex app-server: " + safeLine);
                    }
                }
            }

            private async Task SendMethodNotFoundAsync(
                object id,
                CancellationToken cancellationToken)
            {
                var error = new Dictionary<string, object>();
                error["code"] = -32601;
                error["message"] = "Method not supported by this read-only client.";

                var response = new Dictionary<string, object>();
                response["id"] = id;
                response["error"] = error;
                await SendAsync(response, cancellationToken).ConfigureAwait(false);
            }

            private Task SendNotificationAsync(
                string method,
                IDictionary<string, object> parameters,
                CancellationToken cancellationToken)
            {
                var notification = new Dictionary<string, object>();
                notification["method"] = method;
                if (parameters != null)
                {
                    notification["params"] = parameters;
                }

                return SendAsync(notification, cancellationToken);
            }

            private async Task SendAsync(
                IDictionary<string, object> message,
                CancellationToken cancellationToken)
            {
                var serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = MaxJsonMessageCharacters;
                serializer.RecursionLimit = MaxJsonRecursionDepth;
                var json = serializer.Serialize(message);

                await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (_disposed || _input == null)
                    {
                        throw new IOException(
                            "The Codex app-server input stream is closed.");
                    }

                    await _input.WriteLineAsync(json).ConfigureAwait(false);
                    await _input.FlushAsync().ConfigureAwait(false);
                }
                finally
                {
                    _writeLock.Release();
                }
            }

            private void FailPending(Exception exception)
            {
                TaskCompletionSource<Response>[] pending;
                lock (_pendingLock)
                {
                    pending = new TaskCompletionSource<Response>[_pending.Count];
                    _pending.Values.CopyTo(pending, 0);
                    _pending.Clear();
                }

                foreach (var completion in pending)
                {
                    completion.TrySetException(exception);
                }
            }

            private static void ObserveFault(Task task)
            {
                task.ContinueWith(
                    delegate(Task faulted)
                    {
                        var ignored = faulted.Exception;
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
            }

            private sealed class Response
            {
                public Response(object result, bool hasError)
                {
                    Result = result;
                    HasError = hasError;
                }

                public object Result { get; private set; }
                public bool HasError { get; private set; }
            }

            private sealed class BoundedLineReader
            {
                private readonly StreamReader _reader;
                private readonly int _maximumCharacters;
                private readonly char[] _buffer = new char[4096];
                private readonly StringBuilder _pending = new StringBuilder();
                private bool _discardingOversizedLine;
                private bool _endOfStream;

                public BoundedLineReader(
                    StreamReader reader,
                    int maximumCharacters)
                {
                    if (reader == null)
                    {
                        throw new ArgumentNullException("reader");
                    }

                    if (maximumCharacters <= 0)
                    {
                        throw new ArgumentOutOfRangeException(
                            "maximumCharacters");
                    }

                    _reader = reader;
                    _maximumCharacters = maximumCharacters;
                }

                public bool LastLineWasTruncated { get; private set; }

                public async Task<string> ReadLineAsync(
                    CancellationToken cancellationToken)
                {
                    LastLineWasTruncated = false;
                    while (true)
                    {
                        for (var index = 0; index < _pending.Length; index++)
                        {
                            if (_pending[index] != '\n')
                            {
                                continue;
                            }

                            var lineLength = index;
                            if (lineLength > 0 &&
                                _pending[lineLength - 1] == '\r')
                            {
                                lineLength--;
                            }

                            var truncated =
                                _discardingOversizedLine ||
                                lineLength > _maximumCharacters;
                            var line = truncated
                                ? string.Empty
                                : _pending.ToString(0, lineLength);
                            _pending.Remove(0, index + 1);
                            _discardingOversizedLine = false;
                            LastLineWasTruncated = truncated;
                            return line;
                        }

                        if (_endOfStream)
                        {
                            if (_pending.Length == 0 &&
                                !_discardingOversizedLine)
                            {
                                return null;
                            }

                            var truncated =
                                _discardingOversizedLine ||
                                _pending.Length > _maximumCharacters;
                            var line = truncated
                                ? string.Empty
                                : _pending.ToString();
                            _pending.Clear();
                            _discardingOversizedLine = false;
                            LastLineWasTruncated = truncated;
                            return line;
                        }

                        if (_discardingOversizedLine)
                        {
                            _pending.Clear();
                        }
                        else if (_pending.Length > _maximumCharacters)
                        {
                            _pending.Clear();
                            _discardingOversizedLine = true;
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                        var read = await _reader.ReadAsync(
                            _buffer,
                            0,
                            _buffer.Length).ConfigureAwait(false);
                        if (read == 0)
                        {
                            _endOfStream = true;
                        }
                        else
                        {
                            _pending.Append(_buffer, 0, read);
                        }
                    }
                }
            }
        }
    }

    internal static class SensitiveDataRedactor
    {
        private static readonly Regex SensitiveTerm = new Regex(
            "(token|authorization|cookie|secret|credential|bearer|sk-[A-Za-z0-9_-]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static string RedactDiagnostic(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            if (SensitiveTerm.IsMatch(value))
            {
                return "[sensitive diagnostic redacted]";
            }

            const int maximumLength = 1000;
            return value.Length <= maximumLength
                ? value
                : value.Substring(0, maximumLength) + "...";
        }
    }

    internal sealed class NullWidgetLogger : IWidgetLogger
    {
        public static readonly NullWidgetLogger Instance = new NullWidgetLogger();

        private NullWidgetLogger()
        {
        }

        public void Info(string message)
        {
        }

        public void Warn(string message)
        {
        }

        public void Error(string message, Exception exception)
        {
        }
    }
}
