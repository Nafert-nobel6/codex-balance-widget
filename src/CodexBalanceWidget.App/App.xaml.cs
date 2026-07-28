using System;
using System.Threading;
using System.Windows;

namespace CodexBalanceWidget.App
{
    public partial class App : Application
    {
        private const string MutexName = "Local\\CodexBalanceWidget.SingleInstance.v1";
        private Mutex _singleInstanceMutex;
        private AppComposition _composition;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            bool ownsMutex;
            _singleInstanceMutex = new Mutex(true, MutexName, out ownsMutex);
            if (!ownsMutex)
            {
                Shutdown(0);
                return;
            }

            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            SessionEnding += OnSessionEnding;

            _composition = new AppComposition(Dispatcher);
            _composition.Start();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_composition != null)
            {
                _composition.Dispose();
                _composition = null;
            }

            if (_singleInstanceMutex != null)
            {
                try
                {
                    _singleInstanceMutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                }

                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
            }

            base.OnExit(e);
        }

        private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
        {
            Shutdown(0);
        }

        private void OnDispatcherUnhandledException(
            object sender,
            System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            if (_composition != null)
            {
                _composition.Logger.Error("Unhandled UI exception.", e.Exception);
            }

            e.Handled = true;
            Shutdown(1);
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (_composition != null)
            {
                _composition.Logger.Error(
                    "Unhandled background exception.",
                    e.ExceptionObject as Exception);
            }
        }
    }
}
