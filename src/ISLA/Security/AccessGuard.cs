using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using System.Security.Claims;

namespace ISLA.Security;

public sealed class AccessGuard(AuthenticationStateProvider state, Accounts accounts)
{
    public async Task<ClaimsPrincipal> RequireAsync(bool administrator = false)
    {
        var principal = (await state.GetAuthenticationStateAsync()).User;
        if (!await accounts.ValidAsync(principal, administrator)) throw new UnauthorizedAccessException("Your session has expired or permission was revoked. Sign in again.");
        return principal;
    }
}
public sealed class SessionAuthenticationStateProvider(ILoggerFactory loggerFactory, IServiceScopeFactory scopes) : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);
    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState state, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<Accounts>().ValidAsync(state.User);
    }
}
