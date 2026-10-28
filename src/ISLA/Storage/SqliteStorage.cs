using ISLA.Models;
using ISLA.Security;
using ISLA.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ISLA.Storage;

public sealed class SqliteStorage(StorageConfiguration storage)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public HouseholdDbContext Open(bool create = false) => OpenDatabase(storage.DatabasePath, create);
    private static HouseholdDbContext OpenDatabase(string path, bool create)
    {
        var connection = new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            ForeignKeys = true, Pooling = false, DefaultTimeout = 30
        };
        return new(new DbContextOptionsBuilder<HouseholdDbContext>().UseSqlite(connection.ToString()).Options);
    }
    public void CheckMissing()
    {
        if (!Exists && File.Exists(Path.Combine(Path.GetDirectoryName(storage.DatabasePath)!, "sqlite-installation.json")))
            throw new InvalidDataException("Configured SQLite database is missing. Restore the complete data directory from backup.");
    }
    public bool Exists => File.Exists(storage.DatabasePath);
    public async Task InitializeAsync()
    {
        await gate.WaitAsync();
        try
        {
            CheckMissing();
            if (Exists)
            {
                await ValidateCoreAsync();
                await WriteInstallationMarkerAsync();
                return;
            }
            // Publish only a complete schema and installation row. A crash before
            // publication leaves a disposable staging database, never active data.
            var staging = storage.DatabasePath + ".initializing";
            foreach (var suffix in new[] { "", "-journal", "-wal", "-shm" })
                File.Delete(staging + suffix);
            await using (var db = OpenDatabase(staging, create: true))
            {
                await db.Database.MigrateAsync();
                db.Add(new DatabaseInstallation());
                await db.SaveChangesAsync();
            }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(staging, storage.DatabasePath);
            await WriteInstallationMarkerAsync();
        }
        catch (Exception ex) when (ex is SqliteException or DbUpdateException)
        { throw new IOException("SQLite storage could not be accessed. Check the data directory, permissions and database integrity.", ex); }
        finally { gate.Release(); }
    }
    private Task WriteInstallationMarkerAsync() => PrivateFiles.WriteAsync(
        Path.Combine(Path.GetDirectoryName(storage.DatabasePath)!, "sqlite-installation.json"), new { SchemaVersion = 1, Provider = "Sqlite" });
    public async Task ValidateAsync()
    {
        await gate.WaitAsync();
        try { await ValidateCoreAsync(); }
        catch (Exception ex) when (ex is SqliteException or DbUpdateException)
        { throw new IOException("SQLite storage could not be accessed. Check the data directory, permissions and database integrity.", ex); }
        finally { gate.Release(); }
    }
    private async Task ValidateCoreAsync()
    {
        await using var db = Open();
        var known = db.Database.GetMigrations().ToArray();
        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        if (!known.SequenceEqual(applied)) throw new InvalidDataException("SQLite schema is incompatible. Back up the data directory and run --upgrade-storage with the server stopped.");
        var state = await db.Set<DatabaseInstallation>().SingleAsync();
        if (state.SchemaVersion != 1) throw new InvalidDataException("Unsupported SQLite installation version.");
        if (state.Initialized && !await db.Set<Account>().AnyAsync(a => a.Administrator && !a.Disabled))
            throw new InvalidDataException("Configured SQLite installation has no active administrator. Restore a backup.");
    }
    public async Task UpgradeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (!Exists) throw new InvalidDataException("No SQLite database exists. Initialise a new installation through setup.");
            await using var db = Open();
            var applied = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
            if (applied.Length == 0 || applied.Except(db.Database.GetMigrations()).Any()) throw new InvalidDataException("Unknown SQLite schema. Use a compatible ISLA version.");
            await db.Database.MigrateAsync();
            await ValidateCoreAsync();
        }
        catch (Exception ex) when (ex is SqliteException or DbUpdateException)
        { throw new IOException("SQLite storage could not be accessed. Check the data directory, permissions and database integrity.", ex); }
        finally { gate.Release(); }
    }
    // BEGIN IMMEDIATE acquires the SQLite write lock before reading. The callback
    // therefore observes the latest committed state and cannot lose another write.
    public async Task<T> ExecuteAsync<T>(Func<HouseholdDbContext, Task<T>> action)
    {
        CheckMissing();
        await gate.WaitAsync();
        try
        {
            await using var db = Open();
            await db.Database.OpenConnectionAsync();
            await using var transaction = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: false);
            await db.Database.UseTransactionAsync(transaction);
            var result = await action(db);
            await transaction.CommitAsync();
            return result;
        }
        catch (Exception ex) when (ex is SqliteException or DbUpdateException)
        { throw new IOException("SQLite storage could not be accessed. Check the data directory, permissions and database integrity.", ex); }
        finally { gate.Release(); }
    }
}

public sealed class SqliteHouseholdPersistence(SqliteStorage database) : IHouseholdPersistence
{
    public Task<RecipeData> ReadAsync()
    {
        database.CheckMissing();
        return !database.Exists ? Task.FromResult(new RecipeData()) : database.ExecuteAsync(ReadAsync);
    }
    private static async Task<List<T>> Rows<T>(HouseholdDbContext db) where T : class =>
        await db.Set<T>().OrderBy(x => EF.Property<int>(x, "Position")).ToListAsync();
    private static async Task<RecipeData> ReadAsync(HouseholdDbContext db)
    {
        var recipes = await db.Set<Recipe>().Include(x => x.Ingredients).OrderBy(x => EF.Property<int>(x, "Position")).ToListAsync();
        foreach (var recipe in recipes) recipe.Ingredients = recipe.Ingredients.OrderBy(x => db.Entry(x).Property<int>("Position").CurrentValue).ToList();
        return new RecipeData
        {
            Recipes = recipes, Goods = await Rows<Good>(db), Meals = await Rows<PlannedMeal>(db),
            ExtraItems = await Rows<ExtraShoppingItem>(db), ShoppingOverrides = await Rows<ShoppingOverride>(db),
            Pantry = await Rows<PantryEntry>(db), Purchases = await Rows<Purchase>(db), Prices = await Rows<GoodPrice>(db)
        };
    }
    public async Task UpdateAsync(Func<RecipeData, RecipeData> update)
    {
        await database.ExecuteAsync(async db =>
        {
            var data = update(await ReadAsync(db));
            // Delete and reinsert within the same transaction. Every current domain
            // operation is a household snapshot mutation; no partial state is visible.
            db.ChangeTracker.Clear();
            await db.Set<PlannedMeal>().ExecuteUpdateAsync(s => s.SetProperty(x => x.LeftoversFromMealId, (Guid?)null));
            await db.Set<PlannedMeal>().ExecuteDeleteAsync();
            await db.Set<ExtraShoppingItem>().ExecuteDeleteAsync();
            await db.Set<Recipe>().ExecuteDeleteAsync();
            await db.Set<Good>().ExecuteDeleteAsync();
            await db.Set<ShoppingOverride>().ExecuteDeleteAsync();
            await db.Set<PantryEntry>().ExecuteDeleteAsync();
            await db.Set<Purchase>().ExecuteDeleteAsync();
            await db.Set<GoodPrice>().ExecuteDeleteAsync();
            Add(db, data.Goods); Add(db, data.Recipes); Add(db, data.Meals); Add(db, data.ExtraItems);
            Add(db, data.ShoppingOverrides); Add(db, data.Pantry); Add(db, data.Purchases); Add(db, data.Prices);
            await db.SaveChangesAsync();
            return true;
        });
    }
    private static void Add<T>(HouseholdDbContext db, List<T> rows) where T : class
    {
        for (var i = 0; i < rows.Count; i++)
        {
            // Populate shadow keys before tracking (several rows share default keys).
            var entry = db.Entry(rows[i]);
            entry.Property<int>("Position").CurrentValue = i;
            if (entry.Metadata.FindProperty("NormalizedName") is not null)
                entry.Property<string>("NormalizedName").CurrentValue = ((string)entry.Property("Name").CurrentValue!).ToUpperInvariant();
            entry.State = EntityState.Added;
            if (rows[i] is Recipe recipe)
                for (var n = 0; n < recipe.Ingredients.Count; n++)
                {
                    var ingredient = db.Entry(recipe.Ingredients[n]);
                    ingredient.Property<Guid>("RecipeId").CurrentValue = recipe.Id;
                    ingredient.Property<int>("Position").CurrentValue = n;
                    ingredient.Property<string>("NormalizedName").CurrentValue = recipe.Ingredients[n].Name.ToUpperInvariant();
                    ingredient.State = EntityState.Added;
                }
        }
    }
}

public sealed class SqliteIdentityRepository(SqliteStorage database) : IIdentityRepository
{
    public async Task<T> ExecuteTransactionAsync<T>(Func<IdentityData, Task<T>> operation)
    {
        database.CheckMissing();
        if (!database.Exists)
        {
            var empty = new IdentityData();
            var result = await operation(empty);
            if (empty.Accounts.Count + empty.Sessions.Count > 0) throw new InvalidOperationException("Validate SQLite storage before creating accounts.");
            return result;
        }
        return await database.ExecuteAsync(async db =>
        {
            var state = await db.Set<DatabaseInstallation>().SingleAsync();
            var data = new IdentityData { Accounts = await db.Set<Account>().OrderBy(a => a.Id).ToListAsync(), Sessions = await db.Set<LoginSession>().ToListAsync() };
            if (state.SchemaVersion != 1 || (state.Initialized && !data.Accounts.Any(a => a.Administrator && !a.Disabled)))
                throw new InvalidDataException("SQLite identity state is invalid. Restore a compatible backup.");
            var before = System.Text.Json.JsonSerializer.Serialize(data);
            var result = await operation(data);
            if (before != System.Text.Json.JsonSerializer.Serialize(data))
            {
                db.ChangeTracker.Clear();
                await db.Set<LoginSession>().ExecuteDeleteAsync();
                await db.Set<Account>().ExecuteDeleteAsync();
                foreach (var account in data.Accounts)
                {
                    db.Entry(account).Property<string>("NormalizedName").CurrentValue = account.Name.ToUpperInvariant();
                    db.Entry(account).State = EntityState.Added;
                }
                db.AddRange(data.Sessions);
                if (data.Accounts.Count > 0)
                    await db.Set<DatabaseInstallation>().ExecuteUpdateAsync(s => s.SetProperty(x => x.Initialized, true));
                await db.SaveChangesAsync();
            }
            return result;
        });
    }
}
