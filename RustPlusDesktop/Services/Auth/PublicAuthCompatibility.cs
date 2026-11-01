using System;
using System.Threading;
using System.Threading.Tasks;

namespace RustPlusDesk.Services;

/// <summary>
/// Public-build adapter for call sites that otherwise depend on RustPlus.Auth.
/// This does not implement companion authentication. In particular, an absent
/// provider never becomes a successful registration, token check or logout.
/// The project excludes this file when the private provider project is present.
/// </summary>
public static class RustCompanionAuthService
{
    public static Task<RustPlusTokenCheckResult> CheckTokenStatusAsync(
        string? token = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return CompanionAuthManager.Provider.CheckTokenStatusAsync(token, ct);
    }

    /// <summary>
    /// The public provider contract has no token-export API. Do not guess a
    /// credential field or send an unrelated FCM token to the companion relay.
    /// Existing direct server connections remain handled by their normal code.
    /// </summary>
    public static string? ReadCurrentToken() => null;

    public static async Task<(bool Success, string Message)> LogoutCurrentDeviceAsync(
        string? deviceId = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var result = await RequireAvailableProvider().LogoutCurrentDeviceAsync(deviceId, ct).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException(result.Message);
        return result;
    }

    public static async Task<(bool Success, string Message)> LogoutAllDevicesAsync(
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var result = await RequireAvailableProvider().LogoutAllDevicesAsync(ct).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException(result.Message);
        return result;
    }

    private static IRustCompanionAuthProvider RequireAvailableProvider()
    {
        var provider = CompanionAuthManager.Provider;
        if (!provider.IsAvailable)
        {
            // Some legacy callers clear local credentials after any returned
            // result. Throw before returning so unavailable logout cannot be
            // mistaken for remote revocation or trigger that cleanup path.
            throw new NotSupportedException(
                "Companion logout is unavailable in this public build. " +
                "No remote session was revoked and no credentials were changed.");
        }
        return provider;
    }
}

/// <summary>Explicitly unavailable public-build registration adapter.</summary>
public static class NativeFcmRegistrationService
{
    public static Task<bool> TryRegisterAsync(
        string configPath, Action<string> log, string? browserPath = null,
        string? browserName = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // The existing provider contract does not accept a destination path or
        // browser configuration. Calling it here could falsely register against
        // a different file. Leave all existing credentials untouched.
        log("[pairing] New companion registration is unavailable in this public build. " +
            "Existing imported pairing configuration is preserved; an independent " +
            "registration provider still needs to be implemented.");
        return Task.FromResult(false);
    }

    public static void StampConfigMetadata(
        string configPath, DateTime issuedAt, DateTime expiresAt,
        string? steamId, Action<string> log)
    {
        // Registration above never succeeds. Do not stamp an invented lifetime
        // on credentials that this build did not issue or validate.
        throw new NotSupportedException(
            "Registration metadata cannot be written without a supported registration provider.");
    }
}

/// <summary>Preserves the repair call contract without pretending to renew FCM credentials.</summary>
public static class FcmRepairService
{
    public enum RepairOutcome
    {
        Repaired,
        NeedsFullRePair,
        Unavailable
    }

    public sealed record RepairResult(RepairOutcome Outcome, string Detail);

    public static Task<RepairResult> TryRepairAsync(
        Action<string> log, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        const string detail = "Automatic companion registration repair is unavailable in this " +
            "public build. Existing credentials have not been changed.";
        log("[fcm-repair] " + detail);
        return Task.FromResult(new RepairResult(RepairOutcome.Unavailable, detail));
    }
}
