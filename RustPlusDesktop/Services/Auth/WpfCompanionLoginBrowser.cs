using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RustPlusDesk.Core.Accounts;

namespace RustPlusDesk.Services.Auth;

/// <summary>Intercepts the exact loopback callback inside a temporary WebView2 dialog.</summary>
internal sealed class WpfCompanionLoginBrowser : ICompanionLoginBrowser
{
    public async Task<Uri?> AuthenticateAsync(Uri requestUri, Uri callbackUri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dispatcher = Application.Current?.Dispatcher ?? throw new InvalidOperationException("A desktop UI is required to sign in.");
        if (!dispatcher.CheckAccess())
            return await (await dispatcher.InvokeAsync(() => AuthenticateAsync(requestUri, callbackUri, cancellationToken))).ConfigureAwait(false);

        Uri? result = null;
        bool closed = false;
        Exception? initializationError = null;
        Task initialization = Task.CompletedTask;
        var browser = new WebView2();
        var window = new Window
        {
            Title = "BetterRustPlus — Sign in to Steam",
            Width = 900, Height = 720, MinWidth = 560, MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = browser, Owner = Application.Current.MainWindow
        };
        window.Closed += (_, _) => closed = true;
        async Task InitializeAsync()
        {
            try
            {
                var profilePath = Path.Combine(AppDataPaths.LocalDirectory, "CompanionLoginWebView2");
                var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profilePath);
                if (closed || cancellationToken.IsCancellationRequested) return;
                await browser.EnsureCoreWebView2Async(environment);
                if (closed || cancellationToken.IsCancellationRequested) return;
                browser.CoreWebView2.NavigationStarting += (_, e) =>
                {
                    if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || !MatchesCallback(uri, callbackUri)) return;
                    e.Cancel = true;
                    result = uri;
                    window.Close();
                };
                browser.CoreWebView2.Navigate(requestUri.AbsoluteUri);
            }
            catch (Exception ex)
            {
                initializationError = ex;
                if (!closed) window.Close();
            }
        }
        // Start initialization only once the modal window exists. Closing an unshown WPF
        // window makes a later ShowDialog fail, and WebView2 needs a live visual host.
        window.Loaded += (_, _) => initialization = InitializeAsync();
        using var cancel = cancellationToken.Register(() =>
        {
            if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
                dispatcher.BeginInvoke(new Action(() => { if (!closed) window.Close(); }));
        });
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            window.ShowDialog();
            await initialization;
            cancellationToken.ThrowIfCancellationRequested();
            if (initializationError is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(initializationError).Throw();
            return result;
        }
        finally { if (!closed) window.Close(); browser.Dispose(); }
    }

    internal static bool MatchesCallback(Uri actual, Uri expected) =>
        actual.IsAbsoluteUri && expected.IsAbsoluteUri && actual.Scheme == expected.Scheme &&
        actual.Host == expected.Host && actual.Port == expected.Port &&
        actual.AbsolutePath == expected.AbsolutePath && string.IsNullOrEmpty(actual.UserInfo);
}
