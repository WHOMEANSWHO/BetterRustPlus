using System;
using System.IO;

namespace RustPlusDesk.Services;

/// <summary>
/// Keeps the fork's settings, credentials and caches separate from upstream installations.
/// Set BETTERRUSTPLUS_DATA_ROOT before starting the process to use an isolated test profile.
/// Resolving paths never creates directories or imports an existing profile.
/// </summary>
public static class AppDataPaths
{
    public const string ApplicationName = "BetterRustPlus";
    public const string DataRootEnvironmentVariable = "BETTERRUSTPLUS_DATA_ROOT";

    private static readonly string? OverrideRoot = Environment.GetEnvironmentVariable(DataRootEnvironmentVariable);
    private static readonly AppDataDirectories Directories = ResolvePaths(
        OverrideRoot,
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Path.GetTempPath());

    public static string RoamingDirectory => Directories.Roaming;
    public static string LocalDirectory => Directories.Local;
    public static string TempDirectory => Directories.Temp;
    public static bool UsesCustomDataRoot => !string.IsNullOrWhiteSpace(OverrideRoot);

    internal static AppDataDirectories ResolvePaths(string? overrideRoot, string roamingBase,
        string localBase, string temporaryBase)
    {
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            if (!Path.IsPathFullyQualified(overrideRoot))
                throw new ArgumentException("BETTERRUSTPLUS_DATA_ROOT must be an absolute path.", nameof(overrideRoot));

            var root = Path.GetFullPath(overrideRoot);
            return new(Path.Combine(root, "roaming"), Path.Combine(root, "local"), Path.Combine(root, "temp"));
        }

        return new(Path.Combine(roamingBase, ApplicationName), Path.Combine(localBase, ApplicationName),
            Path.Combine(temporaryBase, ApplicationName));
    }

    internal sealed record AppDataDirectories(string Roaming, string Local, string Temp);
}
