using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RustPlusDesk.Core.Accounts;
using RustPlusDesk.Infrastructure.Accounts;
using RustPlusDesk.Services.Auth;

namespace RustPlusDesk.Services;

internal static class PublicCompanion
{
    internal static readonly string ConfigPath = Path.Combine(AppDataPaths.RoamingDirectory, "rustplusjs-config.json");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    internal static Func<ICompanionAccountService> CreateService = () => new CompanionAccountService(Http, new WpfCompanionLoginBrowser());
    internal static Action<string> SetSteamId = value => TrackingService.SteamId64 = value;
    internal static readonly SemaphoreSlim Gate = new(1, 1);

    internal static async Task<T> Locked<T>(Func<ICompanionAccountService, Task<T>> action, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await action(CreateService()).ConfigureAwait(false); }
        finally { Gate.Release(); }
    }

    internal static string SafeMessage(AccountFailure failure) => failure switch
    {
        AccountFailure.Rejected => "Steam or Rust+ rejected the sign-in. Please try pairing again.",
        AccountFailure.InvalidConfiguration => "The local pairing configuration is invalid; your data was not replaced.",
        AccountFailure.ChangedConfiguration => "The pairing configuration changed during the operation; please try again.",
        AccountFailure.PushRegistration => "Push notification registration failed; the previous configuration was preserved.",
        AccountFailure.MissingDeviceRegistration => "This configuration has no device registration to revoke.",
        _ => "The Rust+ service is unavailable. Please try again later."
    };
}

public static class RustCompanionAuthService
{
    public static async Task<RustPlusTokenCheckResult> CheckTokenStatusAsync(string? token = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // The public service validates the credential in the current configuration. Never silently
        // validate a different token than the one a caller requested.
        if (token is not null && !string.Equals(token, ReadCurrentToken(), StringComparison.Ordinal))
            return new(RustPlusTokenStatus.Missing, "The supplied token does not match this device.", 0, 0, null, null);
        var account = await PublicCompanion.Locked(s => s.CheckAsync(PublicCompanion.ConfigPath, ct), ct).ConfigureAwait(false);
        var status = account.State switch
        {
            AccountState.Active => RustPlusTokenStatus.Valid,
            AccountState.Expired => RustPlusTokenStatus.Expired,
            AccountState.Rejected => RustPlusTokenStatus.LoggedOutOrInvalid,
            AccountState.Invalid => RustPlusTokenStatus.Corrupt,
            AccountState.Offline or AccountState.Unavailable => RustPlusTokenStatus.NetworkError,
            _ => RustPlusTokenStatus.Missing
        };
        ulong.TryParse(account.SteamId, out ulong steamId);
        return new(status, account.State.ToString(), steamId, 0, null, account.ExpiresAt?.UtcDateTime);
    }

    public static string? ReadCurrentToken()
    {
        try
        {
            if (!File.Exists(PublicCompanion.ConfigPath)) return null;
            using var stream = File.OpenRead(PublicCompanion.ConfigPath);
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.TryGetProperty("rustplus_auth_token", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return null; }
    }

    public static Task<(bool Success, string Message)> LogoutCurrentDeviceAsync(string? deviceId = null, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(deviceId)) throw new NotSupportedException("Selecting another device is not supported.");
        return SignOutAsync(CompanionSignOutScope.CurrentDevice, ct);
    }

    public static Task<(bool Success, string Message)> LogoutAllDevicesAsync(CancellationToken ct = default) =>
        SignOutAsync(CompanionSignOutScope.AllDevices, ct);

    private static async Task<(bool Success, string Message)> SignOutAsync(CompanionSignOutScope scope, CancellationToken ct)
    {
        var result = await PublicCompanion.Locked(s => s.SignOutAsync(PublicCompanion.ConfigPath, scope, ct), ct).ConfigureAwait(false);
        // The legacy caller deletes the entire config after success. Only permit this when remote
        // revocation and local cleanup both succeeded; leave partial outcomes for the user to resolve.
        if (result != CompanionSignOutResult.Completed)
            throw new InvalidOperationException(result == CompanionSignOutResult.RemoteCompletedLocalCleanupFailed
                ? "Remote logout succeeded, but local cleanup failed. The local configuration was not deleted."
                : "Remote logout was not confirmed. Local credential fields may have been cleared; the configuration file was retained.");
        return (true, "Rust+ logout completed.");
    }
}

public static class NativeFcmRegistrationService
{
    internal static (DateTime? IssuedAt, DateTime? ExpiresAt) ReadRegistrationMetadata(string configPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            var root = document.RootElement;
            static DateTime? Read(JsonElement root, string name) => root.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), out var parsed)
                ? parsed.LocalDateTime : null;
            return (Read(root, "issue_date"), Read(root, "expiry_date"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return (null, null); }
    }

    public static async Task<bool> TryRegisterAsync(string configPath, Action<string> log, string? browserPath = null,
        string? browserName = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // Browser selection is not supported by the embedded, isolated WebView2 login yet.
        if (!string.IsNullOrEmpty(browserPath)) log("[pairing] Using the built-in secure sign-in window.");
        try
        {
            var account = await PublicCompanion.Locked(async s =>
            {
                var registered = await s.SignInAsync(configPath, ct).ConfigureAwait(false);
                if (registered.State == AccountState.Active)
                    PublicCompanion.SetSteamId(ulong.TryParse(registered.SteamId, out var id) && id != 0 ? registered.SteamId : string.Empty);
                return registered;
            }, ct).ConfigureAwait(false);
            if (account.State != AccountState.Active) { log("[pairing] Sign-in was not confirmed."); return false; }
            if (!ulong.TryParse(account.SteamId, out var steamId) || steamId == 0)
            {
                // Registration was committed; do not claim the config was left untouched.
                log("[pairing] Registration was saved, but Steam identity was missing or invalid. The previous identity was cleared; sign in again before connecting.");
                return false;
            }
            log("[pairing] Rust+ pairing registered. Starting the notification listener.");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { log("[pairing] Sign-in was cancelled."); return false; }
        catch (CompanionAccountException ex) { log("[pairing] " + PublicCompanion.SafeMessage(ex.Failure)); return false; }
        catch (Exception) { log("[pairing] Could not open the sign-in window or complete registration."); return false; }
    }

    public static void StampConfigMetadata(string configPath, DateTime issuedAt, DateTime expiresAt, string? steamId, Action<string> log)
    {
        // SignInAsync writes validated identity and metadata atomically. The legacy listener passes
        // a potentially stale Steam ID here, so it must not overwrite the new registration.
    }
}

public static class FcmRepairService
{
    public enum RepairOutcome { Repaired, NeedsFullRePair, Unavailable }
    public sealed record RepairResult(RepairOutcome Outcome, string Detail);
    public static async Task<RepairResult> TryRepairAsync(Action<string> log, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var account = await PublicCompanion.Locked(s => s.RenewPushAsync(PublicCompanion.ConfigPath, ct), ct).ConfigureAwait(false);
            return account.State == AccountState.Active
                ? new(RepairOutcome.Repaired, "Push registration renewed.")
                : new(RepairOutcome.Unavailable, "Push renewal was not confirmed.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (CompanionAccountException ex)
        {
            string message = PublicCompanion.SafeMessage(ex.Failure);
            log("[fcm-repair] " + message);
            return new(ex.Failure == AccountFailure.Rejected ? RepairOutcome.NeedsFullRePair : RepairOutcome.Unavailable, message);
        }
        catch (Exception) { log("[fcm-repair] Push renewal is unavailable."); return new(RepairOutcome.Unavailable, "Push renewal is unavailable."); }
    }
}
