using ISLA.Models;
using ISLA.Security;
using ISLA.Services;

namespace ISLA.Storage;

public sealed class ConfiguredHouseholdPersistence(StorageConfiguration storage, JsonHouseholdPersistence json, SqliteHouseholdPersistence sqlite) : IHouseholdPersistence
{
    private IHouseholdPersistence Active => storage.ConfigurationError is not null
        ? throw new InvalidDataException(storage.ConfigurationError)
        : storage.Provider == "Sqlite" ? sqlite : json;
    public Task<RecipeData> ReadAsync() => Active.ReadAsync();
    public Task UpdateAsync(Func<RecipeData, RecipeData> update) => Active.UpdateAsync(update);
}
public sealed class ConfiguredIdentityRepository(StorageConfiguration storage, JsonIdentityRepository json, SqliteIdentityRepository sqlite) : IIdentityRepository
{
    public Task<T> ExecuteTransactionAsync<T>(Func<IdentityData, Task<T>> operation) => storage.ConfigurationError is not null
        ? throw new InvalidDataException(storage.ConfigurationError)
        : storage.Provider == "Sqlite" ? sqlite.ExecuteTransactionAsync(operation) : json.ExecuteTransactionAsync(operation);
}
