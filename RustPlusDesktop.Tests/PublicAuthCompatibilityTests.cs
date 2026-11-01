using Microsoft.VisualStudio.TestTools.UnitTesting;
using RustPlusDesk.Services;

namespace RustPlusDesktop.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PublicAuthCompatibilityTests
{
    private IRustCompanionAuthProvider? _originalProvider;

    [TestInitialize]
    public void UseUnavailableProvider()
    {
        if (typeof(NativeFcmRegistrationService).Assembly != typeof(CompanionAuthManager).Assembly)
            Assert.Inconclusive("These tests cover the public compatibility build only.");

        _originalProvider = CompanionAuthManager.Provider;
        CompanionAuthManager.Provider = new UnavailableProvider();
    }

    [TestCleanup]
    public void RestoreProvider()
    {
        if (_originalProvider is not null)
            CompanionAuthManager.Provider = _originalProvider;
    }

    [TestMethod]
    public async Task UnsupportedRegistrationPreservesSuppliedConfiguration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "BetterRustPlusTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "fixture.json");
        const string fixture = "{\"test_fixture\":true}";
        await File.WriteAllTextAsync(path, fixture);
        var messages = new List<string>();

        try
        {
            var registered = await NativeFcmRegistrationService.TryRegisterAsync(path, messages.Add);

            Assert.IsFalse(registered);
            Assert.AreEqual(fixture, await File.ReadAllTextAsync(path));
            StringAssert.Contains(string.Join(" ", messages), "unavailable");
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }

    [TestMethod]
    public async Task UnsupportedRepairDoesNotReportRenewedCredentials()
    {
        var messages = new List<string>();
        var repair = await FcmRepairService.TryRepairAsync(messages.Add);

        Assert.AreEqual(FcmRepairService.RepairOutcome.Unavailable, repair.Outcome);
        StringAssert.Contains(repair.Detail, "have not been changed");
    }

    [TestMethod]
    public async Task UnsupportedLogoutThrowsBeforeLegacyCallerCanClearConfiguration()
    {
        var reachedCleanup = false;

        await Assert.ThrowsExceptionAsync<NotSupportedException>(async () =>
        {
            await RustCompanionAuthService.LogoutCurrentDeviceAsync();
            reachedCleanup = true;
        });

        Assert.IsFalse(reachedCleanup);
        await Assert.ThrowsExceptionAsync<NotSupportedException>(async () =>
            await RustCompanionAuthService.LogoutAllDevicesAsync());
    }

    [TestMethod]
    public async Task FailedLogoutFromAvailableProviderAlsoStopsLegacyCleanup()
    {
        CompanionAuthManager.Provider = new FailedLogoutProvider();
        var reachedCleanup = false;

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
        {
            await RustCompanionAuthService.LogoutCurrentDeviceAsync();
            reachedCleanup = true;
        });

        Assert.IsFalse(reachedCleanup);
    }

    [TestMethod]
    public async Task UnavailableProviderCannotProduceValidTokenOrRelayCredential()
    {
        var result = await RustCompanionAuthService.CheckTokenStatusAsync();

        Assert.AreEqual(RustPlusTokenStatus.Missing, result.Status);
        Assert.IsNull(RustCompanionAuthService.ReadCurrentToken());
    }

    [TestMethod]
    public async Task CancellationStopsRegistrationRepairAndLogoutBeforeAnyWork()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var messages = new List<string>();

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () =>
            await NativeFcmRegistrationService.TryRegisterAsync("unused-fixture.json", messages.Add, ct: cancellation.Token));
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () =>
            await FcmRepairService.TryRepairAsync(messages.Add, cancellation.Token));
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () =>
            await RustCompanionAuthService.LogoutCurrentDeviceAsync(ct: cancellation.Token));
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () =>
            await RustCompanionAuthService.CheckTokenStatusAsync(ct: cancellation.Token));
        Assert.AreEqual(0, messages.Count);
    }

    private class UnavailableProvider : IRustCompanionAuthProvider
    {
        public virtual bool IsAvailable => false;

        public Task<bool> RunRegistrationFlowAsync(Action<string> log, CancellationToken ct = default)
            => throw new AssertFailedException("The unavailable provider must not register.");

        public Task<RustPlusTokenCheckResult> CheckTokenStatusAsync(string? token = null, CancellationToken ct = default)
            => Task.FromResult(new RustPlusTokenCheckResult(RustPlusTokenStatus.Missing, "Unavailable test provider", 0, 0, null, null));

        public virtual Task<(bool Success, string Message)> LogoutCurrentDeviceAsync(string? deviceId = null, CancellationToken ct = default)
            => throw new AssertFailedException("The unavailable provider must not revoke sessions.");

        public virtual Task<(bool Success, string Message)> LogoutAllDevicesAsync(CancellationToken ct = default)
            => throw new AssertFailedException("The unavailable provider must not revoke sessions.");
    }

    private sealed class FailedLogoutProvider : UnavailableProvider
    {
        public override bool IsAvailable => true;

        public override Task<(bool Success, string Message)> LogoutCurrentDeviceAsync(string? deviceId = null, CancellationToken ct = default)
            => Task.FromResult((false, "Remote logout failed in test fixture."));
    }
}
