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
    Task<T> ExecuteAsync<T>(Func<IdentityData, T> operation);
}
public sealed class JsonIdentityRepository(Installation installation) : IIdentityRepository
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private string PathName => Path.Combine(installation.DirectoryPath, "identity.json");
    public async Task<T> ExecuteAsync<T>(Func<IdentityData, T> operation)
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
            var result = operation(data);
            // Keep revocations and lockout state durable before returning.
            if (before != JsonSerializer.Serialize(data)) await PrivateFiles.WriteAsync(PathName, data);
            return result;
        }
        finally { gate.Release(); }
    }
}
public sealed class Accounts(IIdentityRepository repository)
{
    private readonly PasswordHasher<Account> hasher = new();
    public Task<bool> IsConfiguredAsync() => repository.ExecuteAsync(d => d.Accounts.Count > 0);
    public Task<List<Account>> ListAsync() => repository.ExecuteAsync(d => d.Accounts.Select(a => new Account { Id = a.Id, Name = a.Name, Administrator = a.Administrator, Disabled = a.Disabled }).ToList());
    public static void Validate(string name, string password)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(c => !char.IsLetterOrDigit(c) && c is not '-' and not '_' and not '.')) throw new ArgumentException("Use a username of 1–100 letters, numbers, dots, hyphens or underscores.");
        if (password.Length < 12 || password.Length > 256) throw new ArgumentException("Use a password of 12–256 characters.");
    }
    public Task<string> CreateAsync(string name, string password, bool administrator, bool initial = false) => repository.ExecuteAsync(d =>
    {
        Validate(name, password);
        if (initial && d.Accounts.Count != 0) throw new ArgumentException("Setup has already completed.");
        if (d.Accounts.Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("That username is already in use.");
        var account = new Account { Name = name, Administrator = administrator };
        account.PasswordHash = hasher.HashPassword(account, password);
        d.Accounts.Add(account);
        return account.Id;
    });
    public Task<ClaimsPrincipal?> LoginAsync(string name, string password, bool remember) => repository.ExecuteAsync(d =>
    {
        if (name.Length > 100 || password.Length > 256) return null;
        var account = d.Accounts.Find(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (account is null || account.Disabled || account.LockedUntil > DateTimeOffset.UtcNow) return null;
        var verification = hasher.VerifyHashedPassword(account, account.PasswordHash, password);
        if (verification == PasswordVerificationResult.Failed)
        {
            if (++account.Failures >= 5) { account.LockedUntil = DateTimeOffset.UtcNow.AddMinutes(15); account.Failures = 0; }
            return null;
        }
        account.Failures = 0; account.LockedUntil = null;
        if (verification == PasswordVerificationResult.SuccessRehashNeeded) account.PasswordHash = hasher.HashPassword(account, password);
        d.Sessions.RemoveAll(s => s.Expires <= DateTimeOffset.UtcNow);
        var session = new LoginSession { AccountId = account.Id, Expires = DateTimeOffset.UtcNow.AddDays(remember ? 90 : 1) };
        d.Sessions.Add(session);
        return Principal(account, session);
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
    public Task PasswordAsync(string id, string password, string? current = null) => repository.ExecuteAsync(d =>
    {
        var a = d.Accounts.Single(a => a.Id == id);
        Validate(a.Name, password);
        if (current is not null && hasher.VerifyHashedPassword(a, a.PasswordHash, current) == PasswordVerificationResult.Failed) throw new ArgumentException("Current password is incorrect.");
        a.PasswordHash = hasher.HashPassword(a, password); a.Failures = 0; a.LockedUntil = null;
        d.Sessions.RemoveAll(s => s.AccountId == id);
        return true;
    });
    private static ClaimsPrincipal Principal(Account a, LoginSession s) => new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, a.Id), new Claim(ClaimTypes.Name, a.Name), new Claim(ClaimTypes.Role, a.Administrator ? "Administrator" : "Member"), new Claim("session", s.Id) }, "Cookies"));
}
