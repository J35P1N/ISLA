using ISLA.Security;
using ISLA.Services;
using ISLA.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace ISLA.Tests;

public sealed class IdentityProviderTests
{
    [Theory]
    [InlineData("Json")]
    [InlineData("Sqlite")]
    public async Task LegacyHashesAndIdentityPasswordChangesKeepAtomicSessionSemantics(string provider)
    {
        var directory = Path.Combine(Path.GetTempPath(), "isla-identity-" + Guid.NewGuid());
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["DataDirectory"] = directory, ["Storage:Provider"] = provider, ["Setup:Token"] = "operator-secret" }).Build();
        try
        {
            using var installation = new Installation(configuration, new TestEnvironment());
            var storage = new StorageConfiguration(installation, configuration);
            var database = new SqliteStorage(storage);
            if (provider == "Sqlite") await database.InitializeAsync();
            IIdentityRepository repository = new ConfiguredIdentityRepository(storage, new JsonIdentityRepository(installation), new SqliteIdentityRepository(database));
            var accounts = new Accounts(repository);
            const string original = "Legacy password 123!";
            const string replacement = "aaaaaaaaaaaa"; // Preserve the existing length-based policy.
            var id = await accounts.CreateAsync("admin", original, true, initial: true);
            // Emulate the password format used before UserManager was introduced.
            await repository.ExecuteAsync(data =>
            {
                var account = data.Accounts.Single();
                account.PasswordHash = new PasswordHasher<Account>().HashPassword(account, original);
                return true;
            });
            var session = (await accounts.LoginAsync("ADMIN", original, true))!;
            Assert.NotNull(session);
            await Assert.ThrowsAsync<ArgumentException>(() => accounts.PasswordAsync(id, replacement, "wrong current password"));
            Assert.True(await accounts.ValidAsync(session));
            Assert.NotNull(await accounts.LoginAsync("admin", original, false));
            await accounts.PasswordAsync(id, replacement, original);
            Assert.False(await accounts.ValidAsync(session));
            Assert.Null(await accounts.LoginAsync("admin", original, false));
            Assert.NotNull(await accounts.LoginAsync("admin", replacement, false));
            await Assert.ThrowsAsync<ArgumentException>(() => accounts.PasswordAsync(id, "short"));
            Assert.NotNull(await accounts.LoginAsync("admin", replacement, false));
            // Failure counts are persisted across manager/repository instances.
            for (var n = 0; n < 5; n++)
            {
                var failure = await new Accounts(repository).AuthenticateAsync("admin", "wrong", false);
                Assert.Null(failure.Principal);
                Assert.Equal(n == 4, failure.LockedOut);
            }
            var locked = await new Accounts(repository).AuthenticateAsync("admin", replacement, false);
            Assert.Null(locked.Principal);
            Assert.True(locked.LockedOut);
            Assert.False((await accounts.AuthenticateAsync("unknown", replacement, false)).LockedOut);
            await accounts.PasswordAsync(id, original);
            Assert.NotNull(await accounts.LoginAsync("admin", original, false));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "ISLA";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string WebRootPath { get; set; } = "/tmp/isla-identity-webroot";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
