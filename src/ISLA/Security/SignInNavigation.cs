using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.JSInterop;

namespace ISLA.Security;

// Auth transitions need a full HTTP request to re-read the authentication cookie.
// Await the browser call so a disconnected circuit cannot fault a fire-and-forget
// RemoteNavigationManager task. Share the guard across components in the circuit.
public sealed class SignInNavigation(IJSRuntime browser) : CircuitHandler
{
    private bool connected;
    private bool requested;
    private long connectionVersion;

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        connected = true;
        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        connected = false;
        connectionVersion++;
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        connected = false;
        connectionVersion++;
        return Task.CompletedTask;
    }

    public async Task GoToLoginAsync()
    {
        if (!connected || requested) return;
        requested = true;
        var version = connectionVersion;
        try
        {
            await browser.InvokeVoidAsync("Blazor.navigateTo", "/login", new { forceLoad = true, replaceHistoryEntry = true });
        }
        catch (JSDisconnectedException)
        {
            requested = false;
        }
        catch (OperationCanceledException) when (!connected || version != connectionVersion)
        {
            requested = false;
        }
    }
}
