using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using ISLA.Services;

namespace ISLA.Security;

public sealed class Account
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool Administrator { get; set; }
    public bool Disabled { get; set; }
    public int Failures { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
}
public sealed class LoginSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string AccountId { get; set; } = "";
    public DateTimeOffset Expires { get; set; }
}
public sealed class IdentityData
{
    public int SchemaVersion { get; set; } = 1;
    public List<Account> Accounts { get; set; } = [];
    public List<LoginSession> Sessions { get; set; } = [];
}
public interface IIdentityRepository
{
    Task<T> ExecuteTransactionAsync<T>(Func<IdentityData, Task<T>> operation);
    Task<T> ExecuteAsync<T>(Func<IdentityData, T> operation) => ExecuteTransactionAsync(d => Task.FromResult(operation(d)));
}
public sealed class JsonIdentityRepository(Installation installation) : IIdentityRepository
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private string PathName => Path.Combine(installation.DirectoryPath, "identity.json");
    public async Task<T> ExecuteTransactionAsync<T>(Func<IdentityData, Task<T>> operation)
    {
        await gate.WaitAsync();
        try
        {
            if (!File.Exists(PathName) && File.Exists(Path.Combine(installation.DirectoryPath, "installation.json")))
                throw new InvalidDataException("Configured identity storage is missing. Restore identity.json from backup.");
            var data = File.Exists(PathName) ? JsonSerializer.Deserialize<IdentityData>(await File.ReadAllTextAsync(PathName)) ?? throw new InvalidDataException("Identity data is invalid.") : new();
            if (data.Accounts is null || data.Sessions is null || data.SchemaVersion != 1) throw new InvalidDataException("Unsupported identity schema. Restore a compatible backup or upgrade ISLA.");
            if (File.Exists(Path.Combine(installation.DirectoryPath, "installation.json")) && !data.Accounts.Any(a => a.Administrator && !a.Disabled))
                throw new InvalidDataException("Configured installation has no active administrator. Restore identity storage.");
            var before = JsonSerializer.Serialize(data);
            var result = await operation(data);
            // Keep revocations and lockout state durable before returning.
            if (before != JsonSerializer.Serialize(data)) await PrivateFiles.WriteAsync(PathName, data);
            return result;
        }
        finally { gate.Release(); }
    }
}
public sealed class Accounts(IIdentityRepository repository)
{
    public Task<bool> IsConfiguredAsync() => repository.ExecuteAsync(d => d.Accounts.Count > 0);
    public Task<List<Account>> ListAsync() => repository.ExecuteAsync(d => d.Accounts.Select(a => new Account { Id = a.Id, Name = a.Name, Administrator = a.Administrator, Disabled = a.Disabled }).ToList());
    public static void Validate(string name, string password)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(c => !char.IsLetterOrDigit(c) && c is not '-' and not '_' and not '.')) throw new ArgumentException("Use a username of 1–100 letters, numbers, dots, hyphens or underscores.");
        if (password.Length < 12 || password.Length > 256) throw new ArgumentException("Use a password of 12–256 characters.");
    }
    public Task<string> CreateAsync(string name, string password, bool administrator, bool initial = false) => repository.ExecuteTransactionAsync(async d =>
    {
        Validate(name, password);
        if (initial && d.Accounts.Count != 0) throw new ArgumentException("Setup has already completed.");
        if (d.Accounts.Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("That username is already in use.");
        var account = new Account { Name = name, Administrator = administrator };
        using var manager = IdentityAccountStore.CreateManager(d);
        RequireSuccess(await manager.CreateAsync(account, password));
        return account.Id;
    });
    public sealed record LoginResult(ClaimsPrincipal? Principal = null, bool LockedOut = false);

    public async Task<ClaimsPrincipal?> LoginAsync(string name, string password, bool remember) =>
        (await AuthenticateAsync(name, password, remember)).Principal;

    public Task<LoginResult> AuthenticateAsync(string name, string password, bool remember) => repository.ExecuteTransactionAsync<LoginResult>(async d =>
    {
        if (name.Length > 100 || password.Length > 256) return new();
        using var manager = IdentityAccountStore.CreateManager(d);
        var account = await manager.FindByNameAsync(name);
        if (account is null || account.Disabled) return new();
        if (await manager.IsLockedOutAsync(account)) return new(LockedOut: true);
        if (!await manager.CheckPasswordAsync(account, password))
        {
            RequireSuccess(await manager.AccessFailedAsync(account));
            return new(LockedOut: await manager.IsLockedOutAsync(account));
        }
        RequireSuccess(await manager.ResetAccessFailedCountAsync(account));
        RequireSuccess(await manager.SetLockoutEndDateAsync(account, null));
        d.Sessions.RemoveAll(s => s.Expires <= DateTimeOffset.UtcNow);
        var session = new LoginSession { AccountId = account.Id, Expires = DateTimeOffset.UtcNow.AddDays(remember ? 90 : 1) };
        d.Sessions.Add(session);
        return new(Principal(account, session));
    });
    public Task<bool> ValidAsync(ClaimsPrincipal principal, bool admin = false) => repository.ExecuteAsync(d =>
    {
        var id = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var session = principal.FindFirstValue("session");
        var account = d.Accounts.Find(a => a.Id == id && !a.Disabled);
        return principal.Identity?.IsAuthenticated == true && account is not null && (!admin || account.Administrator) && d.Sessions.Any(s => s.Id == session && s.AccountId == id && s.Expires > DateTimeOffset.UtcNow) && principal.IsInRole("Administrator") == account.Administrator;
    });
    public Task RevokeAsync(string accountId, string? sessionId = null) => repository.ExecuteAsync(d => { d.Sessions.RemoveAll(s => s.AccountId == accountId && (sessionId is null || s.Id == sessionId)); return true; });
    public Task UpdateAsync(string id, bool administrator, bool disabled) => repository.ExecuteAsync(d =>
    {
        var a = d.Accounts.Single(a => a.Id == id);
        if (a.Administrator && !a.Disabled && (!administrator || disabled) && d.Accounts.Count(x => x.Administrator && !x.Disabled) <= 1) throw new ArgumentException("Keep at least one active administrator.");
        a.Administrator = administrator; a.Disabled = disabled;
        d.Sessions.RemoveAll(s => s.AccountId == id);
        return true;
    });
    public Task PasswordAsync(string id, string password, string? current = null) => repository.ExecuteTransactionAsync(async d =>
    {
        var a = d.Accounts.Single(a => a.Id == id);
        Validate(a.Name, password);
        using var manager = IdentityAccountStore.CreateManager(d);
        if (current is not null)
            RequireSuccess(await manager.ChangePasswordAsync(a, current, password));
        else
        {
            // Removing and replacing the hash remains inside the repository transaction.
            RequireSuccess(await manager.RemovePasswordAsync(a));
            RequireSuccess(await manager.AddPasswordAsync(a, password));
        }
        RequireSuccess(await manager.ResetAccessFailedCountAsync(a));
        RequireSuccess(await manager.SetLockoutEndDateAsync(a, null));
        d.Sessions.RemoveAll(s => s.AccountId == id);
        return true;
    });
    private static void RequireSuccess(IdentityResult result)
    {
        if (!result.Succeeded) throw new ArgumentException(string.Join(" ", result.Errors.Select(e => e.Description)));
    }
    private static ClaimsPrincipal Principal(Account a, LoginSession s) => new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, a.Id), new Claim(ClaimTypes.Name, a.Name), new Claim(ClaimTypes.Role, a.Administrator ? "Administrator" : "Member"), new Claim("session", s.Id) }, "Cookies"));
}
