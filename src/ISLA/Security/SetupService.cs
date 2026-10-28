using ISLA.Services;
using ISLA.Storage;

namespace ISLA.Security;

public sealed class SetupService(Installation installation, Accounts accounts, IHouseholdPersistence household, StorageConfiguration? storage = null, SqliteStorage? sqlite = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private int failures;
    private DateTimeOffset blockedUntil;
    public bool HasToken => installation.HasToken;
    public string Provider => storage?.Provider ?? "Json";
    public bool StorageControlled => storage?.ExternallyControlled ?? false;
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
    public async Task ValidateStorageAsync(string token, string? provider = null)
    {
        if (!await VerifyAsync(token)) throw new ArgumentException("Setup token was rejected or setup has already completed.");
        await gate.WaitAsync();
        try
        {
            if (await accounts.IsConfiguredAsync()) throw new ArgumentException("Setup has already completed.");
            if (provider is not null && storage is not null) await storage.SelectForSetupAsync(provider);
            // Check the mounted directory before creating a database.
            var probe = Path.Combine(installation.DirectoryPath, ".setup-check-" + Guid.NewGuid().ToString("N"));
            await using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, options: FileOptions.Asynchronous | FileOptions.DeleteOnClose))
            {
                await stream.WriteAsync(new byte[] { 0 });
                await stream.FlushAsync();
            }
            if (Provider == "Sqlite") await sqlite!.InitializeAsync();
            await household.ReadAsync();
        }
        finally { gate.Release(); }
    }

    public async Task CompleteAsync(string token, string username, string password, string? expectedProvider = null)
    {
        if (!await VerifyAsync(token)) throw new ArgumentException("Setup token was rejected or setup has already completed.");
        await gate.WaitAsync();
        try
        {
            if (await accounts.IsConfiguredAsync()) throw new ArgumentException("Setup has already completed.");
            if (expectedProvider is not null && expectedProvider != Provider) throw new ArgumentException("Storage selection changed. Reload setup and review the active provider.");
            Accounts.Validate(username, password);
            await household.ReadAsync(); // Fail closed on damaged existing data.
            if (Provider == "Sqlite")
            {
                await sqlite!.ValidateAsync();
                await accounts.CreateAsync(username, password, true, initial: true);
                return;
            }
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
