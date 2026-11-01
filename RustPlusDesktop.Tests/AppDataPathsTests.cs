using Microsoft.VisualStudio.TestTools.UnitTesting;
using RustPlusDesk.Services;
using RustPlusDesk.Services.Deaths;

namespace RustPlusDesktop.Tests;

[TestClass]
public sealed class AppDataPathsTests
{
    [TestMethod]
    public void DefaultProfileUsesForkDirectoriesWithoutCreatingOrImportingData()
    {
        var root = Path.Combine(Path.GetTempPath(), "BetterRustPlus-path-tests", Guid.NewGuid().ToString("N"));
        var roaming = Path.Combine(root, "Roaming");
        var local = Path.Combine(root, "Local");
        var temp = Path.Combine(root, "Temp");
        var paths = AppDataPaths.ResolvePaths(null, roaming, local, temp);

        Assert.AreEqual(Path.Combine(roaming, "BetterRustPlus"), paths.Roaming);
        Assert.AreEqual(Path.Combine(local, "BetterRustPlus"), paths.Local);
        Assert.AreEqual(Path.Combine(temp, "BetterRustPlus"), paths.Temp);
        Assert.IsFalse(Directory.Exists(root), "Path resolution must not create or migrate any profile data.");
    }

    [TestMethod]
    public void ExplicitTestProfileRedirectsEveryWritableAreaUnderTheSameRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "BetterRustPlus-path-tests", Guid.NewGuid().ToString("N"));
        var paths = AppDataPaths.ResolvePaths(root, @"C:\original-roaming", @"C:\original-local", @"C:\original-temp");

        Assert.AreEqual(Path.Combine(root, "roaming"), paths.Roaming);
        Assert.AreEqual(Path.Combine(root, "local"), paths.Local);
        Assert.AreEqual(Path.Combine(root, "temp"), paths.Temp);
        Assert.IsFalse(Directory.Exists(root));
    }

    [TestMethod]
    public void RelativeOverrideIsRejectedRatherThanWritingBesideTheExecutable()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            AppDataPaths.ResolvePaths("relative-profile", @"C:\roaming", @"C:\local", @"C:\temp"));
    }

    [TestMethod]
    public void DeathLogsResolveDirectlyToTheForkWithoutLegacyMigration()
    {
        Assert.AreEqual(Path.Combine(AppDataPaths.RoamingDirectory, "deaths"), DeathReporter.LogDirectory);
    }
}
