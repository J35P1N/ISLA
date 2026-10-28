using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using ISLA.Models;
using ISLA.Security;
using ISLA.Services;
using ISLA.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ISLA.Tests;

public sealed class SqliteStorageTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "isla-sqlite-" + Guid.NewGuid());
    private const string Password = "A household password 123!";
    private Installation? installation;
    private IConfiguration Config(string? provider = "Sqlite") => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    { ["DataDirectory"] = directory, ["Storage:Provider"] = provider, ["Setup:Token"] = "operator-secret" }).Build();
    private (StorageConfiguration Storage, SqliteStorage Database, Accounts Accounts, SetupService Setup, IHouseholdPersistence Data) Open(string? provider = "Sqlite")
    {
        installation?.Dispose();
        var config = Config(provider);
        installation = new Installation(config, new TestEnvironment());
        var storage = new StorageConfiguration(installation, config);
        var db = new SqliteStorage(storage);
        var data = new ConfiguredHouseholdPersistence(storage, new JsonHouseholdPersistence(config, new TestEnvironment()), new SqliteHouseholdPersistence(db));
        var accounts = new Accounts(new ConfiguredIdentityRepository(storage, new JsonIdentityRepository(installation), new SqliteIdentityRepository(db)));
        return (storage, db, accounts, new SetupService(installation, accounts, data, storage, db), data);
    }
    [Fact]
    public async Task SetupSelectionAndRestartPersistAccountsSessionsAndSchema()
    {
        var first = Open(null);
        Assert.Equal("Json", first.Storage.Provider);
        await first.Setup.ValidateStorageAsync("operator-secret", "Sqlite");
        var competing = Enumerable.Range(0, 5).Select(async n =>
        {
            try { await first.Setup.CompleteAsync("operator-secret", "admin" + n, Password); return true; }
            catch (ArgumentException) { return false; }
        });
        Assert.Single(await Task.WhenAll(competing), x => x);
        var account = Assert.Single(await first.Accounts.ListAsync());
        var principal = (await first.Accounts.LoginAsync(account.Name, Password, true))!;
        var second = Open(null);
        Assert.Equal("Sqlite", second.Storage.Provider);
        await second.Database.ValidateAsync();
        Assert.True(await second.Accounts.ValidAsync(principal, true));
        Assert.False(await second.Setup.VerifyAsync("operator-secret"));
        await using var db = second.Database.Open();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Single(await db.Database.GetAppliedMigrationsAsync());
        var session = Assert.Single(await db.Set<LoginSession>().ToListAsync());
        Assert.InRange(session.Expires, DateTimeOffset.UtcNow.AddDays(90).AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(90));
        Assert.False(File.Exists(Path.Combine(directory, "identity.json")));
        Assert.False(File.Exists(Path.Combine(directory, "recipes.json")));
        await Assert.ThrowsAsync<ArgumentException>(() => second.Accounts.UpdateAsync(account.Id, false, false));
        await second.Accounts.PasswordAsync(account.Id, "Another household password 456!");
        Assert.False(await second.Accounts.ValidAsync(principal));
    }
    [Fact]
    public async Task JsonTransferPreservesEveryCollectionDecimalsDatesAndOrder()
    {
        var first = Open(null);
        await first.Setup.CompleteAsync("operator-secret", "admin", Password);
        var store = new RecipeStore(first.Data);
        var week = new DateOnly(2026, 10, 26);
        var recipe = new Recipe { Name = "Soup", Ingredients = [new() { Name = "Carrot", Quantity = 0.1234567890123456789012345678m, Unit = "kg" }, new() { Name = "Salt", Quantity = 2, Unit = "g" }] };
        await store.SaveAsync(recipe);
        await store.AddMealAsync(week, recipe.Id);
        var meal = Assert.Single((await store.ReadAsync()).Meals);
        await store.AddMealAsync(week.AddDays(1), recipe.Id, meal.Id);
        await store.CreateGoodAndAddExtraAsync(week, "Soap", false, 1, "item");
        await store.CreateGoodAndAddExtraAsync(week, "Brush", false, 2, "item");
        await store.SetShoppingQuantityAsync(week, "Carrot", "g", 150);
        await store.SetShoppingQuantityAsync(week, "Salt", "g", 4);
        await store.SetPurchasedAsync(week, "Carrot", "g", true, 1.50m);
        await store.SetPurchasedAsync(week, "Salt", "g", true, 0m);
        await store.SetInStockAsync(week, "Soap", "item", true);
        await store.SetInStockAsync(week, "Brush", "item", true);
        var export = await new HouseholdTransfer(first.Data).ExportAsync();
        var originalJson = await File.ReadAllBytesAsync(Path.Combine(directory, "recipes.json"));
        await first.Storage.ScheduleAsync("Sqlite");
        Assert.Equal("Json", first.Storage.Provider);
        var second = Open(null);
        Assert.False(await second.Accounts.IsConfiguredAsync());
        await second.Setup.ValidateStorageAsync("operator-secret");
        await second.Setup.CompleteAsync("operator-secret", "destination-admin", Password);
        await new HouseholdTransfer(second.Data).ImportAsync(new MemoryStream(export));
        var sqliteExport = await new HouseholdTransfer(second.Data).ExportAsync();
        Assert.Equivalent(JsonSerializer.Deserialize<HouseholdExport>(export), JsonSerializer.Deserialize<HouseholdExport>(sqliteExport), strict: true);
        Assert.Equal(recipe.Ingredients[0].Quantity, (await second.Data.ReadAsync()).Recipes[0].Ingredients[0].Quantity);
        var restarted = Open(null);
        Assert.Equal(sqliteExport, await new HouseholdTransfer(restarted.Data).ExportAsync());
        Assert.Equal(originalJson, await File.ReadAllBytesAsync(Path.Combine(directory, "recipes.json")));
        Assert.Equal("destination-admin", Assert.Single(await restarted.Accounts.ListAsync()).Name);
        await Assert.ThrowsAsync<ArgumentException>(() => new HouseholdTransfer(restarted.Data).ImportAsync(new MemoryStream(export)));
        await Assert.ThrowsAsync<ArgumentException>(() => restarted.Storage.ScheduleAsync("Json"));
        // Both removal paths promote or remove leftovers under the same constraints.
        var migrated = new RecipeStore(restarted.Data);
        await migrated.RemoveMealAsync(meal.Id);
        Assert.Null(Assert.Single((await migrated.ReadAsync()).Meals).LeftoversFromMealId);
        await migrated.DeleteAsync(recipe.Id);
        Assert.Empty((await migrated.ReadAsync()).Meals);
    }
    [Fact]
    public async Task DatabaseConstraintFailureRollsBackWholeMutationAndImport()
    {
        var opened = Open();
        await opened.Setup.ValidateStorageAsync("operator-secret");
        var store = new RecipeStore(opened.Data);
        await store.SaveGoodAsync("Original", true);
        var before = await new HouseholdTransfer(opened.Data).ExportAsync();
        var failure = await Assert.ThrowsAsync<IOException>(() => opened.Data.UpdateAsync(d =>
        {
            d.Goods.Clear();
            d.Goods.Add(new Good { Name = "Duplicate" });
            d.ExtraItems.Add(new ExtraShoppingItem { GoodId = Guid.NewGuid(), Week = new DateOnly(2026, 10, 26) });
            return d;
        }));
        Assert.IsType<DbUpdateException>(failure.InnerException);
        Assert.Equal(before, await new HouseholdTransfer(opened.Data).ExportAsync());
        await opened.Data.UpdateAsync(_ => new());
        await opened.Database.ExecuteAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER RejectImport BEFORE INSERT ON Goods BEGIN SELECT RAISE(ABORT, 'simulated storage failure'); END");
            return true;
        });
        await Assert.ThrowsAsync<IOException>(() => new HouseholdTransfer(opened.Data).ImportAsync(new MemoryStream(before)));
        Assert.True(HouseholdTransfer.Empty(await opened.Data.ReadAsync()));
        await opened.Database.ExecuteAsync(async db => { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER RejectImport"); return true; });
        await new HouseholdTransfer(opened.Data).ImportAsync(new MemoryStream(before));
        await Assert.ThrowsAsync<InvalidOperationException>(() => opened.Data.UpdateAsync(d => throw new InvalidOperationException("Interrupted")));
        Assert.Equal(before, await new HouseholdTransfer(opened.Data).ExportAsync());
    }
    [Fact]
    public async Task IndependentConnectionsSerializeReadModifyWrite()
    {
        var opened = Open();
        await opened.Setup.ValidateStorageAsync("operator-secret");
        var other = new RecipeStore(new SqliteHouseholdPersistence(new SqliteStorage(opened.Storage)));
        var first = new RecipeStore(opened.Data);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(n => Task.Run(() => (n % 2 == 0 ? first : other).SaveGoodAsync("Good" + n, true))));
        Assert.Equal(20, (await first.ReadAsync()).Goods.Count);
    }
    [Fact]
    public async Task ExternalConfigurationWinsAndCannotBeChangedFromSetupOrSettings()
    {
        var opened = Open();
        Assert.True(opened.Storage.ExternallyControlled);
        await Assert.ThrowsAsync<ArgumentException>(() => opened.Setup.ValidateStorageAsync("operator-secret", "Json"));
        await Assert.ThrowsAsync<ArgumentException>(() => opened.Storage.ScheduleAsync("Json"));
        Assert.Equal("Sqlite", opened.Storage.Provider);
    }
    [Fact]
    public async Task LockoutDisableRoleChangesAndSessionExpiryRemainDurable()
    {
        var opened = Open();
        await opened.Setup.ValidateStorageAsync("operator-secret");
        await opened.Setup.CompleteAsync("operator-secret", "admin", Password);
        var id = await opened.Accounts.CreateAsync("member", Password, false);
        for (var n = 0; n < 5; n++) Assert.Null(await opened.Accounts.LoginAsync("member", "wrong", false));
        Assert.Null(await opened.Accounts.LoginAsync("member", Password, false));
        await opened.Accounts.PasswordAsync(id, Password);
        var user = (await opened.Accounts.LoginAsync("MEMBER", Password, false))!;
        Assert.False(await opened.Accounts.ValidAsync(user, true));
        await opened.Accounts.UpdateAsync(id, true, false);
        Assert.False(await opened.Accounts.ValidAsync(user));
        user = (await opened.Accounts.LoginAsync("member", Password, false))!;
        await opened.Accounts.UpdateAsync(id, true, true);
        Assert.False(await opened.Accounts.ValidAsync(user));
        Assert.Null(await opened.Accounts.LoginAsync("member", Password, false));
        await opened.Accounts.UpdateAsync(id, false, false);
        user = (await opened.Accounts.LoginAsync("member", Password, false))!;
        await opened.Database.ExecuteAsync(async db =>
        {
            await db.Set<LoginSession>().ExecuteUpdateAsync(s => s.SetProperty(x => x.Expires, DateTimeOffset.UtcNow.AddMinutes(-1)));
            return true;
        });
        Assert.False(await opened.Accounts.ValidAsync(user));
        await Assert.ThrowsAsync<ArgumentException>(() => opened.Accounts.CreateAsync("MEMBER", Password, false));
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("schema")]
    [InlineData("corrupt")]
    [InlineData("migration")]
    public async Task DamagedConfiguredDatabaseServesRecoveryWithoutReopeningSetup(string failure)
    {
        var opened = Open();
        await opened.Setup.ValidateStorageAsync("operator-secret");
        await opened.Setup.CompleteAsync("operator-secret", "admin", Password);
        if (failure == "schema")
            await opened.Database.ExecuteAsync(async db => { await db.Database.ExecuteSqlRawAsync("UPDATE Installation SET SchemaVersion=99"); return true; });
        if (failure == "migration")
            await opened.Database.ExecuteAsync(async db => { await db.Database.ExecuteSqlRawAsync("INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('99999999999999_Unknown', '99.0.0')"); return true; });
        installation!.Dispose();
        if (failure == "missing") File.Delete(opened.Storage.DatabasePath);
        if (failure == "corrupt") await File.WriteAllTextAsync(opened.Storage.DatabasePath, "broken");
        using var process = Start("--urls=http://127.0.0.1:0");
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            string? address = null;
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                const string marker = "Now listening on: ";
                if (line.Contains(marker)) { address = line[(line.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Trim(); break; }
            }
            Assert.NotNull(address);
            using var client = new HttpClient();
            var response = await client.GetAsync(address + "/setup", timeout.Token);
            Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains("Setup remains closed", await response.Content.ReadAsStringAsync(timeout.Token));
        }
        finally { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); await stderr; }
        Assert.False(File.Exists(Path.Combine(directory, "identity.json")));
    }
    [Fact]
    public async Task UpgradeAndOperatorRecoveryUseTheSelectedProviderAndRefuseSecondProcess()
    {
        var opened = Open();
        await opened.Setup.ValidateStorageAsync("operator-secret");
        await opened.Setup.CompleteAsync("operator-secret", "admin", Password);
        var principal = (await opened.Accounts.LoginAsync("admin", Password, true))!;
        using (var blocked = Start("--upgrade-storage"))
        {
            await blocked.WaitForExitAsync();
            Assert.Equal(1, blocked.ExitCode);
            Assert.Contains("exclusive access", await blocked.StandardError.ReadToEndAsync());
        }
        installation!.Dispose();
        using (var upgrade = Start("--upgrade-storage"))
        {
            var error = upgrade.StandardError.ReadToEndAsync();
            await upgrade.WaitForExitAsync();
            Assert.True(upgrade.ExitCode == 0, await error);
        }
        var passwordFile = Path.Combine(directory, "reset-password");
        await File.WriteAllTextAsync(passwordFile, "A replacement password 456!");
        using (var recovery = Start("--reset-admin", passwordFile))
        {
            var error = recovery.StandardError.ReadToEndAsync();
            await recovery.WaitForExitAsync();
            Assert.True(recovery.ExitCode == 0, await error);
        }
        var restarted = Open();
        Assert.False(await restarted.Accounts.ValidAsync(principal));
        Assert.NotNull(await restarted.Accounts.LoginAsync("admin", "A replacement password 456!", true));
    }
    [Theory]
    [InlineData("{\"SchemaVersion\":9,\"Household\":{}}")]
    [InlineData("{\"SchemaVersion\":1,\"Household\":{\"Goods\":[{\"Name\":\"Milk\"},{\"Name\":\"MILK\"}]}}")]
    [InlineData("{\"SchemaVersion\":1,\"Household\":{\"Meals\":[{\"RecipeId\":\"00000000-0000-0000-0000-000000000001\"}]}}")]
    [InlineData("broken")]
    public async Task InvalidImportsLeaveSqliteDestinationEmpty(string input)
    {
        var opened = Open();
        await opened.Setup.ValidateStorageAsync("operator-secret");
        await Assert.ThrowsAsync<ArgumentException>(() => new HouseholdTransfer(opened.Data).ImportAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(input))));
        Assert.True(HouseholdTransfer.Empty(await opened.Data.ReadAsync()));
    }
    [Fact]
    public async Task MissingLocalProviderSelectionFailsClosedAndEnvironmentCanRestoreIt()
    {
        var opened = Open(null);
        await opened.Setup.ValidateStorageAsync("operator-secret", "Sqlite");
        await opened.Setup.CompleteAsync("operator-secret", "admin", Password);
        File.Delete(Path.Combine(directory, "storage.json"));
        var missing = Open(null);
        Assert.NotNull(missing.Storage.ConfigurationError);
        var restored = Open("Sqlite");
        Assert.Null(restored.Storage.ConfigurationError);
        Assert.True(await restored.Accounts.IsConfiguredAsync());
    }
    [Fact]
    public async Task InterruptedSchemaCreationCanBeRetriedWithoutPublishingPartialDatabase()
    {
        var first = Open(null);
        await first.Storage.SelectForSetupAsync("Sqlite");
        await File.WriteAllTextAsync(first.Storage.DatabasePath + ".initializing", "interrupted migration");
        var restarted = Open(null);
        Assert.False(restarted.Database.Exists);
        Assert.False(await restarted.Accounts.IsConfiguredAsync());
        await restarted.Setup.ValidateStorageAsync("operator-secret");
        await restarted.Setup.CompleteAsync("operator-secret", "admin", Password);
        await restarted.Database.ValidateAsync();
        Assert.Single(await restarted.Accounts.ListAsync());
        Assert.False(File.Exists(restarted.Storage.DatabasePath + ".initializing"));
    }
    [Fact]
    public async Task SetupResumesAfterSchemaPublicationBeforeMarkerAndAccountCommit()
    {
        var first = Open(null);
        await first.Setup.ValidateStorageAsync("operator-secret", "Sqlite");
        File.Delete(Path.Combine(directory, "sqlite-installation.json"));
        var restarted = Open(null);
        Assert.False(await restarted.Accounts.IsConfiguredAsync());
        await restarted.Setup.ValidateStorageAsync("operator-secret");
        Assert.True(File.Exists(Path.Combine(directory, "sqlite-installation.json")));
        await restarted.Setup.CompleteAsync("operator-secret", "admin", Password);
        await Assert.ThrowsAsync<ArgumentException>(() => restarted.Setup.CompleteAsync("operator-secret", "second", Password));
        Assert.Single(await restarted.Accounts.ListAsync());
    }
    private Process Start(string option, string? passwordFile = null)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = directory };
        start.ArgumentList.Add(typeof(RecipeStore).Assembly.Location);
        start.ArgumentList.Add("--DataDirectory=" + directory);
        start.ArgumentList.Add("--Storage:Provider=Sqlite");
        start.ArgumentList.Add(option);
        if (passwordFile is not null) { start.Environment["ISLA_RESET_USERNAME"] = "admin"; start.Environment["ISLA_RESET_PASSWORD_FILE"] = passwordFile; }
        return Process.Start(start)!;
    }
    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "ISLA";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string WebRootPath { get; set; } = Path.Combine(Path.GetTempPath(), "wwwroot");
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
    public void Dispose()
    {
        installation?.Dispose();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
