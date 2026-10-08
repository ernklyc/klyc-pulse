namespace Pulse.Core.Apps;

public sealed record InstalledApp(
    string Name,
    string Publisher,
    string Version,
    DateTime? InstallDate,
    long SizeBytes,
    string? InstallLocation,
    string? UninstallString,
    string? QuietUninstallString,
    string RegistryKey)
{
    public bool CanUninstall => !string.IsNullOrWhiteSpace(UninstallString) || !string.IsNullOrWhiteSpace(QuietUninstallString);
}