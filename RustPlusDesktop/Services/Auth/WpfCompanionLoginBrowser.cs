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
        var browser = new WebView2();
        var window = new Window
        {
            Title = "BetterRustPlus — Sign in to Steam",
            Width = 900, Height = 720, MinWidth = 560, MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = browser, Owner = Application.Current.MainWindow
        };
        using var cancel = cancellationToken.Register(() => dispatcher.BeginInvoke(new Action(() => window.Close())));
        try
        {
            var profilePath = Path.Combine(AppDataPaths.LocalDirectory, "CompanionLoginWebView2");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profilePath);
            cancellationToken.ThrowIfCancellationRequested();
            await browser.EnsureCoreWebView2Async(environment);
            cancellationToken.ThrowIfCancellationRequested();
            browser.CoreWebView2.NavigationStarting += (_, e) =>
            {
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || !MatchesCallback(uri, callbackUri)) return;
                e.Cancel = true;
                result = uri;
                window.Close();
            };
            browser.CoreWebView2.Navigate(requestUri.AbsoluteUri);
            window.ShowDialog();
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally { browser.Dispose(); }
    }

    internal static bool MatchesCallback(Uri actual, Uri expected) =>
        actual.IsAbsoluteUri && actual.Scheme == expected.Scheme &&
        actual.Host == expected.Host && actual.Port == expected.Port &&
        actual.AbsolutePath == expected.AbsolutePath && string.IsNullOrEmpty(actual.UserInfo);
}
