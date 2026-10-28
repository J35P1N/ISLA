using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ISLA.Security;

// This adapter is scoped to one repository transaction. Both JSON and SQLite
// commit the resulting accounts and sessions together, including failed logins.
internal sealed class IdentityAccountStore(IdentityData data) : IUserPasswordStore<Account>, IUserLockoutStore<Account>
{
    private static readonly UpperInvariantLookupNormalizer Normalizer = new();
    public static UserManager<Account> CreateManager(IdentityData data)
    {
        var options = new IdentityOptions();
        options.User.AllowedUserNameCharacters = ""; // Accounts.Validate preserves the existing username policy.
        options.Password.RequiredLength = 12;
        options.Password.RequiredUniqueChars = 1;
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        return new UserManager<Account>(new IdentityAccountStore(data), Options.Create(options), new PasswordHasher<Account>(),
            [new UserValidator<Account>()], [new PasswordValidator<Account>()], Normalizer, new IdentityErrorDescriber(),
            null!, NullLogger<UserManager<Account>>.Instance);
    }

    public void Dispose() { }
    public Task<string> GetUserIdAsync(Account user, CancellationToken cancellationToken) => Task.FromResult(user.Id);
    public Task<string?> GetUserNameAsync(Account user, CancellationToken cancellationToken) => Task.FromResult<string?>(user.Name);
    public Task SetUserNameAsync(Account user, string? userName, CancellationToken cancellationToken)
    { user.Name = userName ?? ""; return Task.CompletedTask; }
    public Task<string?> GetNormalizedUserNameAsync(Account user, CancellationToken cancellationToken) => Task.FromResult<string?>(Normalizer.NormalizeName(user.Name));
    public Task SetNormalizedUserNameAsync(Account user, string? normalizedName, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<IdentityResult> CreateAsync(Account user, CancellationToken cancellationToken)
    { data.Accounts.Add(user); return Task.FromResult(IdentityResult.Success); }
    public Task<IdentityResult> UpdateAsync(Account user, CancellationToken cancellationToken) => Task.FromResult(IdentityResult.Success);
    public Task<IdentityResult> DeleteAsync(Account user, CancellationToken cancellationToken)
    { data.Accounts.Remove(user); data.Sessions.RemoveAll(s => s.AccountId == user.Id); return Task.FromResult(IdentityResult.Success); }
    public Task<Account?> FindByIdAsync(string userId, CancellationToken cancellationToken) => Task.FromResult(data.Accounts.SingleOrDefault(a => a.Id == userId));
    public Task<Account?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) =>
        Task.FromResult(data.Accounts.SingleOrDefault(a => Normalizer.NormalizeName(a.Name) == normalizedUserName));
    public Task SetPasswordHashAsync(Account user, string? passwordHash, CancellationToken cancellationToken)
    { user.PasswordHash = passwordHash ?? ""; return Task.CompletedTask; }
    public Task<string?> GetPasswordHashAsync(Account user, CancellationToken cancellationToken) => Task.FromResult<string?>(user.PasswordHash.Length == 0 ? null : user.PasswordHash);
    public Task<bool> HasPasswordAsync(Account user, CancellationToken cancellationToken) => Task.FromResult(user.PasswordHash.Length > 0);
    public Task<DateTimeOffset?> GetLockoutEndDateAsync(Account user, CancellationToken cancellationToken) => Task.FromResult(user.LockedUntil);
    public Task SetLockoutEndDateAsync(Account user, DateTimeOffset? lockoutEnd, CancellationToken cancellationToken)
    { user.LockedUntil = lockoutEnd; return Task.CompletedTask; }
    public Task<int> IncrementAccessFailedCountAsync(Account user, CancellationToken cancellationToken) => Task.FromResult(++user.Failures);
    public Task ResetAccessFailedCountAsync(Account user, CancellationToken cancellationToken)
    { user.Failures = 0; return Task.CompletedTask; }
    public Task<int> GetAccessFailedCountAsync(Account user, CancellationToken cancellationToken) => Task.FromResult(user.Failures);
    // Lockout is mandatory for every ISLA account; there is no per-user opt-out.
    public Task<bool> GetLockoutEnabledAsync(Account user, CancellationToken cancellationToken) => Task.FromResult(true);
    public Task SetLockoutEnabledAsync(Account user, bool enabled, CancellationToken cancellationToken) => Task.CompletedTask;
}
