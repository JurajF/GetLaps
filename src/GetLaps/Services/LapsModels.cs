namespace GetLaps.Services;

/// <summary>A device that has a LAPS password backed up to Entra ID.</summary>
public sealed record LapsDevice(
    string Id,
    string DeviceName,
    DateTimeOffset? LastBackupDateTime,
    DateTimeOffset? RefreshDateTime);

/// <summary>One backed-up local administrator credential.</summary>
public sealed record LapsCredential(
    string AccountName,
    string? AccountSid,
    DateTimeOffset? BackupDateTime,
    string Password);

public sealed record LapsDeviceCredentials(
    LapsDevice Device,
    IReadOnlyList<LapsCredential> Credentials)
{
    public LapsCredential? Current => Credentials.Count > 0 ? Credentials[0] : null;
}

public sealed class LapsException(string message) : Exception(message);
