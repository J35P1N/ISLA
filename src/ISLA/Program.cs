using ISLA.Components;
using ISLA.Security;
using ISLA.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;

var configurationArgs = args.Where(argument => argument != "--reset-admin").ToArray();
var builder = WebApplication.CreateBuilder(configurationArgs);
if (builder.Configuration["ISLA_CONFIG_FILE"] is { Length: > 0 } configFile)
    builder.Configuration.AddJsonFile(Path.GetFullPath(configFile), optional: false, reloadOnChange: false)
        .AddEnvironmentVariables().AddCommandLine(configurationArgs);
Installation installation;
try { installation = new Installation(builder.Configuration, builder.Environment); }
catch (DataDirectoryInUseException ex)
{
    Console.Error.WriteLine("ISLA could not start: " + ex.Message);
    Environment.ExitCode = 1;
    return;
}
builder.Services.AddSingleton(installation);
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(installation.KeysPath)).SetApplicationName("ISLA");
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, SessionAuthenticationStateProvider>();
builder.Services.AddAuthentication("Cookies").AddCookie("Cookies", options =>
{
    options.LoginPath = "/login";
    options.AccessDeniedPath = "/login";
    options.SlidingExpiration = false;
    options.ExpireTimeSpan = TimeSpan.FromDays(90);
    options.Cookie.Name = "ISLA.Session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Events.OnValidatePrincipal = async context =>
    {
        if (!await context.HttpContext.RequestServices.GetRequiredService<Accounts>().ValidAsync(context.Principal!))
        {
            context.RejectPrincipal();
            await Microsoft.AspNetCore.Authentication.AuthenticationHttpContextExtensions.SignOutAsync(context.HttpContext, "Cookies");
        }
    };
});
builder.Services.AddAuthorization(options => options.AddPolicy("Administrator", policy => policy.RequireRole("Administrator")));
builder.Services.AddSingleton<IIdentityRepository, JsonIdentityRepository>();
builder.Services.AddSingleton<Accounts>();
builder.Services.AddSingleton<IHouseholdPersistence, JsonHouseholdPersistence>();
builder.Services.AddSingleton(sp => new RecipeStore(sp.GetRequiredService<IHouseholdPersistence>()));
builder.Services.AddSingleton<HouseholdTransfer>();
builder.Services.AddSingleton<SetupService>();
builder.Services.AddScoped<AccessGuard>();
builder.Services.AddScoped<SignInNavigation>();
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler>(services => services.GetRequiredService<SignInNavigation>());
builder.Services.AddScoped<IHouseholdService, HouseholdService>();
var app = builder.Build();

if (args.Contains("--reset-admin"))
{
    var accounts = app.Services.GetRequiredService<Accounts>();
    var username = Environment.GetEnvironmentVariable("ISLA_RESET_USERNAME") ?? "";
    var passwordFile = Environment.GetEnvironmentVariable("ISLA_RESET_PASSWORD_FILE") ?? throw new InvalidOperationException("Set ISLA_RESET_USERNAME and ISLA_RESET_PASSWORD_FILE to a restricted password file; stop the server first.");
    var account = (await accounts.ListAsync()).SingleOrDefault(a => a.Name.Equals(username, StringComparison.OrdinalIgnoreCase) && a.Administrator)
        ?? throw new InvalidOperationException("Administrator not found.");
    await accounts.PasswordAsync(account.Id, (await File.ReadAllTextAsync(passwordFile)).TrimEnd('\r', '\n'));
    await accounts.UpdateAsync(account.Id, true, false);
    Console.WriteLine("Administrator password reset and sessions revoked.");
    installation.Dispose();
    return;
}
// Validate configured files at startup. Never silently create a fresh installation on corruption.
try
{
    var configured = await app.Services.GetRequiredService<Accounts>().IsConfiguredAsync();
    var settingsPath = Path.Combine(installation.DirectoryPath, "installation.json");
    if (File.Exists(settingsPath))
    {
        using var settings = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath));
        if (settings.RootElement.GetProperty("SchemaVersion").GetInt32() != 1 || settings.RootElement.GetProperty("Provider").GetString() != "Json")
            throw new InvalidDataException("Unsupported installation storage configuration.");
    }
    if (configured && !File.Exists(Path.Combine(installation.DirectoryPath, "recipes.json")))
        throw new InvalidDataException("Configured household storage is missing. Restore recipes.json from backup.");
    await app.Services.GetRequiredService<IHouseholdPersistence>().ReadAsync();
    if (!configured) installation.EnsureSetupToken(Console.Out);
}
catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException or InvalidOperationException or KeyNotFoundException)
{
    app.Logger.LogError("ISLA storage validation failed ({FailureType}). Restore a compatible private data backup and restart; setup remains closed.", ex.GetType().Name);
    app.Run(async context =>
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync("<!doctype html><html lang='en'><title>ISLA storage recovery</title><h1>ISLA storage needs attention</h1><p>The configured data could not be opened. Ask the operator to check mounted files, permissions and schema compatibility, restore a complete backup if necessary, and restart ISLA. Setup remains closed and no data has been replaced.</p></html>");
    });
    try { await app.RunAsync(); }
    finally { installation.Dispose(); }
    return;
}
if (!app.Environment.IsDevelopment()) { app.UseExceptionHandler("/Error", createScopeForErrors: true); app.UseHsts(); }
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    if (!Path.HasExtension(path)) context.Response.Headers.CacheControl = "no-cache, no-store";
    if (HttpMethods.IsGet(context.Request.Method) && !path.StartsWithSegments("/_blazor") && !path.StartsWithSegments("/_framework") && !Path.HasExtension(path))
    {
        var configured = await context.RequestServices.GetRequiredService<Accounts>().IsConfiguredAsync();
        if (!configured && path != "/setup") { context.Response.Redirect("/setup"); return; }
        if (configured && path == "/setup")
        {
            context.Response.Redirect(context.User.Identity?.IsAuthenticated == true ? "/" : "/login");
            return;
        }
        if (configured && context.User.Identity?.IsAuthenticated != true && path != "/login" && path != "/Error") { context.Response.Redirect("/login"); return; }
    }
    await next();
});
app.UseAntiforgery();
app.MapStaticAssets();
app.MapAccountEndpoints();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
try { await app.RunAsync(); }
finally { installation.Dispose(); }
