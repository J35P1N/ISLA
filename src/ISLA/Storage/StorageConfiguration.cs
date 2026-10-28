using System.Text.Json;
using ISLA.Security;
using ISLA.Services;

namespace ISLA.Storage;

// Only the active process's choice is mutable during first-run setup. Settings writes
// a pending choice to disk; services keep using the current provider until restart.
public sealed class StorageConfiguration
{
    private readonly Installation installation;
    public string Provider { get; private set; } = "Json";
    public bool ExternallyControlled { get; }
    public string? ConfigurationError { get; }
    public string? PendingProvider { get; private set; }
    public string DatabasePath => Path.Combine(installation.DirectoryPath, "isla.db");
    private string SettingsPath => Path.Combine(installation.DirectoryPath, "storage.json");
    public StorageConfiguration(Installation installation, IConfiguration configuration)
    {
        this.installation = installation;
        ExternallyControlled = configuration["Storage:Provider"] is not null;
        try
        {
            var value = configuration["Storage:Provider"];
            if (value is null && File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<StorageSettings>(File.ReadAllText(SettingsPath)) ?? throw new InvalidDataException("Invalid storage settings.");
                if (settings.SchemaVersion != 1) throw new InvalidDataException("Unsupported storage settings version.");
                value = settings.Provider;
            }
            if (value is null && File.Exists(Path.Combine(installation.DirectoryPath, "sqlite-installation.json")))
                throw new InvalidDataException("SQLite provider selection is missing. Restore storage.json or set Storage:Provider explicitly.");
            Provider = Canonical(value ?? "Json");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException or UnauthorizedAccessException)
        { ConfigurationError = "Storage configuration cannot be read or is unsupported. Check storage.json and Storage:Provider."; }
    }
    public static string Canonical(string provider) => provider?.ToUpperInvariant() switch
    {
        "JSON" => "Json", "SQLITE" => "Sqlite", _ => throw new ArgumentException("Choose JSON or SQLite storage.")
    };
    public async Task SelectForSetupAsync(string provider)
    {
        provider = Canonical(provider);
        if (provider == Provider) return;
        if (ExternallyControlled) throw new ArgumentException("Storage is controlled by deployment configuration.");
        RequireEmptyDestination(provider);
        // An existing legacy household must complete JSON setup, then be exported.
        if (Provider == "Json" && (File.Exists(Path.Combine(installation.DirectoryPath, "recipes.json")) || File.Exists(Path.Combine(installation.DirectoryPath, "identity.json"))))
            throw new ArgumentException("Complete JSON setup and export the existing household before changing storage in Settings.");
        await PrivateFiles.WriteAsync(SettingsPath, new StorageSettings { Provider = provider });
        Provider = provider;
    }
    public async Task ScheduleAsync(string provider)
    {
        if (ExternallyControlled) throw new ArgumentException("Storage is controlled by deployment configuration.");
        provider = Canonical(provider);
        if (provider != Provider) RequireEmptyDestination(provider);
        await PrivateFiles.WriteAsync(SettingsPath, new StorageSettings { Provider = provider });
        PendingProvider = provider == Provider ? null : provider;
    }
    private void RequireEmptyDestination(string provider)
    {
        var files = provider == "Sqlite" ? new[] { "isla.db", "isla.db-wal", "isla.db-shm", "isla.db-journal", "sqlite-installation.json" } : new[] { "recipes.json", "identity.json", "installation.json" };
        if (files.Any(file => File.Exists(Path.Combine(installation.DirectoryPath, file))))
            throw new ArgumentException("The destination already contains storage files. Use a new, empty DataDirectory and configure the provider there; existing stores are never overwritten.");
    }
    private sealed class StorageSettings
    {
        [System.Text.Json.Serialization.JsonRequired]
        public int SchemaVersion { get; set; } = 1;
        [System.Text.Json.Serialization.JsonRequired]
        public string Provider { get; set; } = "Json";
    }
}
