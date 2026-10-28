using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using ISLA.Services;

namespace ISLA.Security;

public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this WebApplication app)
    {
        app.MapPost("/account/login", async (HttpContext context, Accounts accounts, IAntiforgery antiforgery) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context)) return Results.BadRequest("Invalid antiforgery token.");
            var form = await context.Request.ReadFormAsync();
            var remember = form["remember"] == "true";
            var result = await accounts.AuthenticateAsync(form["username"].ToString(), form["password"].ToString(), remember);
            if (result.Principal is null) return Results.LocalRedirect(result.LockedOut ? "/login?failed=locked" : "/login?failed=true");
            await context.SignInAsync("Cookies", result.Principal, new AuthenticationProperties { IsPersistent = remember, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(remember ? 90 : 1), AllowRefresh = false });
            return Results.LocalRedirect("/");
        }).AllowAnonymous();
        app.MapPost("/account/logout", async (HttpContext context, Accounts accounts, IAntiforgery antiforgery) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context)) return Results.BadRequest("Invalid antiforgery token.");
            await accounts.RevokeAsync(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!, context.User.FindFirstValue("session"));
            await context.SignOutAsync("Cookies");
            return Results.LocalRedirect("/login");
        }).RequireAuthorization();
        app.MapGet("/admin/export", async (HouseholdTransfer transfer) => Results.File(await transfer.ExportAsync(), "application/json", "isla-household.json")).RequireAuthorization("Administrator");
        app.MapPost("/admin/import", async (HttpContext context, HouseholdTransfer transfer, IAntiforgery antiforgery) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context)) return Results.BadRequest("Invalid antiforgery token.");
            var form = await context.Request.ReadFormAsync();
            var file = form.Files.GetFile("household");
            if (file is null || file.Length == 0 || file.Length > 10 * 1024 * 1024) return Results.LocalRedirect("/settings?transfer=invalid");
            try { await using var stream = file.OpenReadStream(); await transfer.ImportAsync(stream); }
            catch (ArgumentException) { return Results.LocalRedirect("/settings?transfer=rejected"); }
            return Results.LocalRedirect("/settings?transfer=imported");
        }).RequireAuthorization("Administrator");
    }
}
