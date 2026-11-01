namespace RustPlusDesk.Core.Servers;

/// <summary>A stale edit must be refreshed rather than overwriting newer data.</summary>
public sealed class ProfileChangedException() : IOException("The profiles file changed. Refresh before editing again.");
