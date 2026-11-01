using Microsoft.VisualStudio.TestTools.UnitTesting;
using RustPlusDesk.Core.Accounts;
using RustPlusDesk.Services;

namespace RustPlusDesktop.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PublicAuthCompatibilityTests
{
    private Func<ICompanionAccountService>? previous;
    private Action<string>? previousSetSteamId;
    private string savedSteamId = "old-account";
    private readonly FakeCompanion fake = new();

    [TestInitialize] public void Setup() { previous = PublicCompanion.CreateService; previousSetSteamId = PublicCompanion.SetSteamId; PublicCompanion.CreateService = () => fake; PublicCompanion.SetSteamId = value => savedSteamId = value; }
    [TestCleanup] public void Cleanup() { if (previous is not null) PublicCompanion.CreateService = previous; if (previousSetSteamId is not null) PublicCompanion.SetSteamId = previousSetSteamId; }

    [TestMethod]
    public async Task RegistrationUsesRequestedConfigAndDoesNotOverwriteMetadata()
    {
        var messages = new List<string>();
        bool ok = await NativeFcmRegistrationService.TryRegisterAsync("fixture-config.json", messages.Add);
        Assert.IsTrue(ok);
        Assert.AreEqual("76561198000000000", savedSteamId);
        Assert.AreEqual("fixture-config.json", fake.Path);
        NativeFcmRegistrationService.StampConfigMetadata("missing-fixture.json", DateTime.Now, DateTime.Now, "stale-id", messages.Add);
        Assert.IsFalse(File.Exists("missing-fixture.json"));
    }

    [TestMethod]
    public async Task MissingSteamIdentityClearsStaleIdentityAndFailsClosed()
    {
        fake.SteamId = "";
        var messages = new List<string>();
        Assert.IsFalse(await NativeFcmRegistrationService.TryRegisterAsync("fixture.json", messages.Add));
        Assert.AreEqual("", savedSteamId);
        StringAssert.Contains(string.Join(" ", messages), "Registration was saved");
    }

    [TestMethod]
    public async Task RejectedAndCancelledRegistrationsDoNotReportSuccess()
    {
        fake.Failure = new CompanionAccountException(AccountFailure.Rejected);
        Assert.IsFalse(await NativeFcmRegistrationService.TryRegisterAsync("fixture.json", _ => { }));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () => await NativeFcmRegistrationService.TryRegisterAsync("fixture.json", _ => { }, ct: cancel.Token));
    }

    [TestMethod]
    public async Task MetadataIsReadFromCommittedConfigWithoutChangingIdentity()
    {
        var directory = Path.Combine(Path.GetTempPath(), "BetterRustPlusTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "fixture.json");
        const string content = "{\"steam_id\":\"123\",\"issue_date\":\"2026-01-02T03:04:05Z\",\"expiry_date\":\"2026-01-17T03:04:05Z\"}";
        try
        {
            await File.WriteAllTextAsync(path, content);
            var dates = NativeFcmRegistrationService.ReadRegistrationMetadata(path);
            Assert.IsNotNull(dates.IssuedAt);
            Assert.IsNotNull(dates.ExpiresAt);
            NativeFcmRegistrationService.StampConfigMetadata(path, DateTime.Now, DateTime.Now, "old-id", _ => { });
            Assert.AreEqual(content, await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }

    [TestMethod]
    public async Task UnconfirmedLogoutThrowsBeforeLegacyCallerCanDeleteConfig()
    {
        fake.SignOutResult = CompanionSignOutResult.LocalOnly;
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () => await RustCompanionAuthService.LogoutCurrentDeviceAsync());
        fake.SignOutResult = CompanionSignOutResult.RemoteCompletedLocalCleanupFailed;
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () => await RustCompanionAuthService.LogoutAllDevicesAsync());
        fake.SignOutResult = CompanionSignOutResult.Completed;
        Assert.IsTrue((await RustCompanionAuthService.LogoutAllDevicesAsync()).Success);
    }

    [TestMethod]
    public async Task RepairAndStatusRespectActualProviderResults()
    {
        fake.State = AccountState.Expired;
        Assert.AreEqual(RustPlusTokenStatus.Expired, (await RustCompanionAuthService.CheckTokenStatusAsync()).Status);
        Assert.AreEqual(FcmRepairService.RepairOutcome.Unavailable, (await FcmRepairService.TryRepairAsync(_ => { })).Outcome);
        fake.Failure = new CompanionAccountException(AccountFailure.Rejected);
        Assert.AreEqual(FcmRepairService.RepairOutcome.NeedsFullRePair, (await FcmRepairService.TryRepairAsync(_ => { })).Outcome);
    }

    private sealed class FakeCompanion : ICompanionAccountService
    {
        public string? Path;
        public AccountState State = AccountState.Active;
        public string SteamId = "76561198000000000";
        public CompanionSignOutResult SignOutResult = CompanionSignOutResult.Completed;
        public Exception? Failure;
        public Task<CompanionAccount> CheckAsync(string path, CancellationToken ct) => Task.FromResult(new CompanionAccount(State));
        public Task<CompanionAccount> SignInAsync(string path, CancellationToken ct) { Path = path; if (Failure is not null) throw Failure; return Task.FromResult(new CompanionAccount(State, SteamId)); }
        public Task<CompanionAccount> RenewPushAsync(string path, CancellationToken ct) { if (Failure is not null) throw Failure; return Task.FromResult(new CompanionAccount(State)); }
        public Task<CompanionSignOutResult> SignOutAsync(string path, CompanionSignOutScope scope, CancellationToken ct) => Task.FromResult(SignOutResult);
    }
}
