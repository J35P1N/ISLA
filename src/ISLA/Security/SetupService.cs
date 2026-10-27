using ISLA.Services;

namespace ISLA.Security;

public sealed class SetupService(Installation installation, Accounts accounts, IHouseholdPersistence household)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private int failures;
    private DateTimeOffset blockedUntil;
    public bool HasToken => installation.HasToken;
    public async Task<bool> VerifyAsync(string token)
    {
        await gate.WaitAsync();
        try
        {
            if (await accounts.IsConfiguredAsync() || DateTimeOffset.UtcNow < blockedUntil) return false;
            if (installation.TokenMatches(token)) { failures = 0; return true; }
            if (++failures >= 10) { blockedUntil = DateTimeOffset.UtcNow.AddMinutes(5); failures = 0; }
            return false;
        }
        finally { gate.Release(); }
    }
    public async Task ValidateStorageAsync(string token)
    {
        if (!await VerifyAsync(token)) throw new ArgumentException("Setup token was rejected or setup has already completed.");
        await household.ReadAsync();
        // Confirm the configured mount is writable before collecting administrator credentials.
        var probe = Path.Combine(installation.DirectoryPath, ".setup-check-" + Guid.NewGuid().ToString("N"));
        await using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 4096, options: FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        await stream.WriteAsync(new byte[] { 0 });
        await stream.FlushAsync();
    }
    public async Task CompleteAsync(string token, string username, string password)
    {
        if (!await VerifyAsync(token)) throw new ArgumentException("Setup token was rejected or setup has already completed.");
        await gate.WaitAsync();
        try
        {
            if (await accounts.IsConfiguredAsync()) throw new ArgumentException("Setup has already completed.");
            Accounts.Validate(username, password);
            await household.ReadAsync(); // Fail closed on damaged existing data.
            var legacy = Path.Combine(installation.DirectoryPath, "recipes.json");
            if (File.Exists(legacy))
            {
                var backup = legacy + ".pre-setup.bak";
                if (!File.Exists(backup))
                {
                    File.Copy(legacy, backup);
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(backup, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
            if (!File.Exists(legacy)) await household.UpdateAsync(_ => new());
            // This atomic identity write is the installation commit. No household conversion is needed.
            await accounts.CreateAsync(username, password, true, initial: true);
            await PrivateFiles.WriteAsync(Path.Combine(installation.DirectoryPath, "installation.json"), new { SchemaVersion = 1, Provider = "Json" });
        }
        finally { gate.Release(); }
    }
}
