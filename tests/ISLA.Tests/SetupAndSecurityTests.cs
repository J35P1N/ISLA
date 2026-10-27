using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ISLA.Models;
using ISLA.Security;
using ISLA.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace ISLA.Tests;

public sealed class SetupAndSecurityTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "isla-security-" + Guid.NewGuid());
    private readonly List<IDisposable> resources = [];
    private const string Password = "A household password 123!";
    private IConfiguration Config => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DataDirectory"] = directory, ["Setup:Token"] = "operator-secret" }).Build();
    private Installation Installation() { var value = new Installation(Config, new TestEnvironment()); resources.Add(value); return value; }
    private (Accounts Accounts, SetupService Setup, IHouseholdPersistence Data) Open()
    {
        var installation = Installation();
        var accounts = new Accounts(new JsonIdentityRepository(installation));
        var data = new JsonHouseholdPersistence(Config, new TestEnvironment());
        return (accounts, new SetupService(installation, accounts, data), data);
    }
    [Fact]
    public void FirstRunGeneratesConsoleTokenOnceAndRotatesItOnRestart()
    {
        var config = new ConfigurationBuilder().AddConfiguration(Config)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Setup:Token"] = null }).Build();
        string token;
        using (var installation = new Installation(config, new TestEnvironment()))
        {
            using var output = new StringWriter();
            installation.EnsureSetupToken(output);
            token = output.ToString().Split('\n').Single(line => line.StartsWith("ISLA first-run setup token: "))
                .Split(": ")[1].Trim();
            Assert.Equal(64, token.Length);
            Assert.True(installation.TokenMatches(token));
            Assert.False(installation.TokenMatches("wrong-token"));
            var printed = output.ToString();
            installation.EnsureSetupToken(output);
            Assert.Equal(printed, output.ToString());
            Assert.False(File.Exists(Path.Combine(directory, "identity.json")));
        }
        using var restarted = new Installation(config, new TestEnvironment());
        using var nextOutput = new StringWriter();
        restarted.EnsureSetupToken(nextOutput);
        Assert.True(restarted.HasToken);
        Assert.False(restarted.TokenMatches(token));
    }
    [Fact]
    public void ExplicitTokenIsPreservedAndNeverPrinted()
    {
        using var installation = new Installation(Config, new TestEnvironment());
        using var output = new StringWriter();
        installation.EnsureSetupToken(output);
        Assert.Empty(output.ToString());
        Assert.True(installation.TokenMatches("operator-secret"));
    }
    [Fact]
    public void MountedTokenIsPreservedAndNeverPrinted()
    {
        Directory.CreateDirectory(directory);
        var tokenFile = Path.Combine(directory, "mounted-token");
        File.WriteAllText(tokenFile, "mounted-secret\n");
        var config = new ConfigurationBuilder().AddConfiguration(Config)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Setup:Token"] = null, ["Setup:TokenFile"] = tokenFile }).Build();
        using var installation = new Installation(config, new TestEnvironment());
        using var output = new StringWriter();
        installation.EnsureSetupToken(output);
        Assert.Empty(output.ToString());
        Assert.True(installation.TokenMatches("mounted-secret"));
    }
    [Fact]
    public async Task TokenRequiredAndSetupCannotRunTwice()
    {
        var (accounts, setup, _) = Open();
        await Assert.ThrowsAsync<ArgumentException>(() => setup.CompleteAsync("wrong", "admin", Password));
        Assert.False(await accounts.IsConfiguredAsync());
        await setup.CompleteAsync("operator-secret", "admin", Password);
        Assert.True(await accounts.IsConfiguredAsync());
        Assert.False(await setup.VerifyAsync("operator-secret"));
        await Assert.ThrowsAsync<ArgumentException>(() => setup.CompleteAsync("operator-secret", "other", Password));
        Assert.Single(await accounts.ListAsync());
        var text = await File.ReadAllTextAsync(Path.Combine(directory, "identity.json"));
        Assert.DoesNotContain(Password, text);
        Assert.DoesNotContain("operator-secret", text);
    }
    [Fact]
    public async Task ConcurrentSetupCommitsOnlyOneAdministrator()
    {
        var (accounts, setup, _) = Open();
        var tasks = Enumerable.Range(0, 8).Select(async n => { try { await setup.CompleteAsync("operator-secret", "admin" + n, Password); return true; } catch (ArgumentException) { return false; } });
        Assert.Single(await Task.WhenAll(tasks), x => x);
        Assert.Single(await accounts.ListAsync());
    }
    [Fact]
    public async Task SecondInstanceRefusedWithConciseMessageAndNonzeroExitCode()
    {
        Installation();
        Assert.Throws<DataDirectoryInUseException>(() => new Installation(Config, new TestEnvironment()));
        var start = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = directory
        };
        start.ArgumentList.Add(typeof(RecipeStore).Assembly.Location);
        start.ArgumentList.Add("--DataDirectory=" + directory);
        using var process = System.Diagnostics.Process.Start(start)!;
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await process.WaitForExitAsync(timeout.Token);
        var error = await errorTask;
        Assert.Equal(1, process.ExitCode);
        Assert.Contains("ISLA could not start: Simple JSON mode requires exclusive access", error);
        Assert.Single(error.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.DoesNotContain("Unhandled exception", error);
        Assert.DoesNotContain(" at ", error);
        Assert.Empty(await outputTask);
    }
    [Fact]
    public async Task ExistingHouseholdIsPreservedAndBackedUp()
    {
        Directory.CreateDirectory(directory);
        var legacy = "{\"Ingredients\":[\"Milk\"],\"Recipes\":[]}";
        await File.WriteAllTextAsync(Path.Combine(directory, "recipes.json"), legacy);
        var (_, setup, data) = Open();
        await setup.CompleteAsync("operator-secret", "admin", Password);
        Assert.Equal(legacy, await File.ReadAllTextAsync(Path.Combine(directory, "recipes.json")));
        Assert.Equal(legacy, await File.ReadAllTextAsync(Path.Combine(directory, "recipes.json.pre-setup.bak")));
        Assert.Equal("Milk", Assert.Single((await data.ReadAsync()).Goods).Name);
    }
    [Fact]
    public async Task StorageValidationPrecedesAccountCreationAndDoesNotChangeHousehold()
    {
        var (accounts, setup, _) = Open();
        var path = Path.Combine(directory, "recipes.json");
        const string legacy = "{\"Ingredients\":[\"Milk\"]}";
        await File.WriteAllTextAsync(path, legacy);
        await Assert.ThrowsAsync<ArgumentException>(() => setup.ValidateStorageAsync("wrong"));
        await setup.ValidateStorageAsync("operator-secret");
        Assert.False(await accounts.IsConfiguredAsync());
        Assert.Equal(legacy, await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(directory, ".setup-check-*"));
        Assert.False(File.Exists(Path.Combine(directory, "identity.json")));
    }
    [Fact]
    public async Task DamagedHouseholdPreventsSetupWithoutCreatingIdentity()
    {
        var (accounts, setup, _) = Open();
        await File.WriteAllTextAsync(Path.Combine(directory, "recipes.json"), "broken");
        await Assert.ThrowsAsync<JsonException>(() => setup.ValidateStorageAsync("operator-secret"));
        await Assert.ThrowsAsync<JsonException>(() => setup.CompleteAsync("operator-secret", "admin", Password));
        Assert.False(await accounts.IsConfiguredAsync());
    }
    [Fact]
    public async Task RememberedSessionHasFixedNinetyDayExpiryAndCanBeRevoked()
    {
        var (accounts, setup, _) = Open();
        await setup.CompleteAsync("operator-secret", "admin", Password);
        var before = DateTimeOffset.UtcNow;
        var principal = (await accounts.LoginAsync("ADMIN", Password, true))!;
        Assert.True(await accounts.ValidAsync(principal, true));
        var data = JsonSerializer.Deserialize<IdentityData>(await File.ReadAllTextAsync(Path.Combine(directory, "identity.json")))!;
        Assert.InRange(Assert.Single(data.Sessions).Expires, before.AddDays(90), DateTimeOffset.UtcNow.AddDays(90));
        var expiry = data.Sessions[0].Expires;
        await accounts.ValidAsync(principal);
        data = JsonSerializer.Deserialize<IdentityData>(await File.ReadAllTextAsync(Path.Combine(directory, "identity.json")))!;
        Assert.Equal(expiry, data.Sessions[0].Expires);
        await accounts.RevokeAsync(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
        Assert.False(await accounts.ValidAsync(principal));
    }
    [Fact]
    public async Task DisableResetAndRoleChangesRevokeSessionsAndLastAdminIsProtected()
    {
        var (accounts, setup, _) = Open();
        await setup.CompleteAsync("operator-secret", "admin", Password);
        var admin = (await accounts.ListAsync()).Single();
        await Assert.ThrowsAsync<ArgumentException>(() => accounts.UpdateAsync(admin.Id, false, false));
        await Assert.ThrowsAsync<ArgumentException>(() => accounts.UpdateAsync(admin.Id, true, true));
        var id = await accounts.CreateAsync("member", Password, false);
        var member = (await accounts.LoginAsync("member", Password, true))!;
        Assert.False(await accounts.ValidAsync(member, true));
        await accounts.UpdateAsync(id, false, true);
        Assert.False(await accounts.ValidAsync(member));
        Assert.Null(await accounts.LoginAsync("member", Password, false));
        await accounts.UpdateAsync(id, false, false);
        member = (await accounts.LoginAsync("member", Password, false))!;
        await accounts.PasswordAsync(id, "Another household password 456!");
        Assert.False(await accounts.ValidAsync(member));
        Assert.Null(await accounts.LoginAsync("member", Password, false));
    }
    [Fact]
    public async Task LockoutAndExpiredSessionsAreEnforced()
    {
        var (accounts, setup, _) = Open();
        await setup.CompleteAsync("operator-secret", "admin", Password);
        for (var i = 0; i < 5; i++) Assert.Null(await accounts.LoginAsync("admin", "incorrect", false));
        Assert.Null(await accounts.LoginAsync("admin", Password, false));
        var id = (await accounts.ListAsync()).Single().Id;
        await accounts.PasswordAsync(id, Password);
        var principal = (await accounts.LoginAsync("admin", Password, false))!;
        var path = Path.Combine(directory, "identity.json");
        var data = JsonSerializer.Deserialize<IdentityData>(await File.ReadAllTextAsync(path))!;
        data.Sessions[0].Expires = DateTimeOffset.UtcNow.AddMinutes(-1);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(data));
        Assert.False(await accounts.ValidAsync(principal));
    }
    [Fact]
    public async Task ExportRoundTripsAllHouseholdDataAndExcludesIdentity()
    {
        var (_, setup, data) = Open();
        await setup.CompleteAsync("operator-secret", "admin", Password);
        var store = new RecipeStore(data);
        var week = new DateOnly(2026, 10, 26);
        var recipe = new Recipe { Name = "Soup", Ingredients = [new() { Name = "Carrot", Quantity = 2 }] };
        await store.SaveAsync(recipe);
        await store.AddMealAsync(week, recipe.Id);
        await store.CreateGoodAndAddExtraAsync(week, "Soap", false, 1, "item");
        await store.SetShoppingQuantityAsync(week, "Carrot", "item", 3);
        await store.SetPurchasedAsync(week, "Carrot", "item", true, 1.50m);
        await store.SetInStockAsync(week, "Soap", "item", true);
        var transfer = new HouseholdTransfer(data);
        var bytes = await transfer.ExportAsync();
        Assert.DoesNotContain("PasswordHash", Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain("Sessions", Encoding.UTF8.GetString(bytes));
        await data.UpdateAsync(_ => new());
        await transfer.ImportAsync(new MemoryStream(bytes));
        Assert.Equal(bytes, await transfer.ExportAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => transfer.ImportAsync(new MemoryStream(bytes)));
        Assert.Equal(bytes, await transfer.ExportAsync());
    }
    [Theory]
    [InlineData("{\"SchemaVersion\":9,\"Household\":{}}")]
    [InlineData("{\"SchemaVersion\":1,\"Household\":null}")]
    [InlineData("{\"SchemaVersion\":1,\"Household\":{\"Meals\":[{\"RecipeId\":\"00000000-0000-0000-0000-000000000001\"}]}}")]
    [InlineData("{\"SchemaVersion\":1,\"Household\":{\"Goods\":[{\"Name\":\"Milk\"},{\"Name\":\"MILK\"}]}}")]
    [InlineData("broken")]
    public async Task InvalidImportsLeaveDestinationUntouched(string input)
    {
        var (_, _, data) = Open();
        var transfer = new HouseholdTransfer(data);
        await Assert.ThrowsAsync<ArgumentException>(() => transfer.ImportAsync(new MemoryStream(Encoding.UTF8.GetBytes(input))));
        Assert.True(HouseholdTransfer.Empty(await data.ReadAsync()));
    }
    [Fact]
    public async Task ServiceRejectsAnonymousAndRevokedSessionsBeforeReadingOrWriting()
    {
        var (accounts, setup, data) = Open();
        await setup.CompleteAsync("operator-secret", "admin", Password);
        var state = new TestAuthenticationState();
        var service = new HouseholdService(new RecipeStore(data), new AccessGuard(state, accounts));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReadAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveGoodAsync("Hidden", true));
        state.User = (await accounts.LoginAsync("admin", Password, false))!;
        await service.SaveGoodAsync("Visible", true);
        var id = state.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await accounts.RevokeAsync(id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReadAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveGoodAsync("Hidden", true));
        Assert.Equal("Visible", Assert.Single((await data.ReadAsync()).Goods).Name);
    }
    [Fact]
    public async Task ImportCompetingWithHouseholdWriteNeverOverwritesExistingData()
    {
        var (_, _, data) = Open();
        var store = new RecipeStore(data);
        await store.SaveGoodAsync("Existing", true);
        var transfer = new HouseholdTransfer(data);
        var source = new HouseholdExport { Household = new RecipeData { Goods = [new Good { Name = "Imported" }] } };
        await Assert.ThrowsAsync<ArgumentException>(() => transfer.ImportAsync(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(source))));
        Assert.Equal("Existing", Assert.Single((await data.ReadAsync()).Goods).Name);
    }
    [Fact]
    public async Task OperatorRecoveryUsesExplicitDataDirectoryAndRevokesSessions()
    {
        var (accounts, setup, _) = Open();
        await setup.CompleteAsync("operator-secret", "admin", Password);
        var oldSession = (await accounts.LoginAsync("admin", Password, true))!;
        foreach (var resource in resources) resource.Dispose();
        var passwordPath = Path.Combine(directory, "operator-password");
        const string replacement = "Replacement household password 456!";
        await File.WriteAllTextAsync(passwordPath, replacement);
        var start = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = directory
        };
        start.ArgumentList.Add(typeof(RecipeStore).Assembly.Location);
        start.ArgumentList.Add("--reset-admin");
        start.ArgumentList.Add("--DataDirectory=" + directory);
        start.Environment["ISLA_RESET_USERNAME"] = "admin";
        start.Environment["ISLA_RESET_PASSWORD_FILE"] = passwordPath;
        using var process = System.Diagnostics.Process.Start(start)!;
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await process.WaitForExitAsync(timeout.Token);
        var output = await outputTask;
        var error = await errorTask;
        Assert.True(process.ExitCode == 0, error);
        Assert.DoesNotContain(replacement, output + error);
        Assert.False(await accounts.ValidAsync(oldSession));
        Assert.Null(await accounts.LoginAsync("admin", Password, false));
        Assert.NotNull(await accounts.LoginAsync("admin", replacement, false));
    }
    [Theory]
    [InlineData("schema")]
    [InlineData("identity")]
    [InlineData("household")]
    public async Task ConfiguredStorageFailureServesRecoveryAndKeepsSetupClosed(string failure)
    {
        var (_, setup, _) = Open();
        await setup.CompleteAsync("operator-secret", "admin", Password);
        foreach (var resource in resources) resource.Dispose();
        if (failure == "schema") await File.WriteAllTextAsync(Path.Combine(directory, "installation.json"), "{\"SchemaVersion\":99,\"Provider\":\"Json\"}");
        if (failure == "identity") File.Delete(Path.Combine(directory, "identity.json"));
        if (failure == "household") File.Delete(Path.Combine(directory, "recipes.json"));
        var start = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = directory
        };
        start.ArgumentList.Add(typeof(RecipeStore).Assembly.Location);
        start.ArgumentList.Add("--DataDirectory=" + directory);
        start.ArgumentList.Add("--urls=http://127.0.0.1:0");
        using var process = System.Diagnostics.Process.Start(start)!;
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            string? address = null;
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                const string marker = "Now listening on: ";
                var position = line.IndexOf(marker, StringComparison.Ordinal);
                if (position >= 0) { address = line[(position + marker.Length)..].Trim(); break; }
            }
            Assert.True(address is not null, process.HasExited ? await errorTask : "Server did not report a listening address.");
            using var client = new HttpClient();
            var response = await client.GetAsync(address + "/setup", timeout.Token);
            Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains("Setup remains closed", await response.Content.ReadAsStringAsync(timeout.Token));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }
    private sealed class TestAuthenticationState : Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider
    {
        public ClaimsPrincipal User { get; set; } = new(new ClaimsIdentity());
        public override Task<Microsoft.AspNetCore.Components.Authorization.AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(User));
    }
    public void Dispose() { foreach (var item in resources) item.Dispose(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "/tmp/isla-test-webroot";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
