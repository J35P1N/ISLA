using ISLA.Security;
using Microsoft.JSInterop;
using Xunit;

namespace ISLA.Tests;

public sealed class SignInNavigationTests
{
    [Fact]
    public async Task PrerenderAndDisconnectedCircuitsDoNotInvokeBrowserNavigation()
    {
        var browser = new Browser();
        var navigation = new SignInNavigation(browser);
        await navigation.GoToLoginAsync();
        await navigation.OnConnectionUpAsync(null!, default);
        await navigation.OnConnectionDownAsync(null!, default);
        await navigation.GoToLoginAsync();
        Assert.Equal(0, browser.Calls);
    }

    [Fact]
    public async Task RedirectIsRequestedOnlyOncePerCircuit()
    {
        var browser = new Browser();
        var navigation = new SignInNavigation(browser);
        await navigation.OnConnectionUpAsync(null!, default);
        await navigation.GoToLoginAsync();
        await navigation.GoToLoginAsync();
        Assert.Equal(1, browser.Calls);
        Assert.Equal("Blazor.navigateTo", browser.Identifier);
        Assert.Equal("/login", browser.Arguments![0]);
        var options = System.Text.Json.JsonSerializer.Serialize(browser.Arguments[1]);
        Assert.Contains("\"forceLoad\":true", options);
        Assert.Contains("\"replaceHistoryEntry\":true", options);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedNavigationDoesNotFaultCircuitEvenIfConnectionReturns(bool reconnect)
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var browser = new Browser { Invoke = () => pending.Task };
        var navigation = new SignInNavigation(browser);
        await navigation.OnConnectionUpAsync(null!, default);
        var redirect = navigation.GoToLoginAsync();
        await navigation.GoToLoginAsync();
        Assert.Equal(1, browser.Calls);
        await navigation.OnConnectionDownAsync(null!, default);
        if (reconnect) await navigation.OnConnectionUpAsync(null!, default);
        pending.SetCanceled();
        await redirect;
        if (!reconnect) await navigation.OnConnectionUpAsync(null!, default);
        browser.Invoke = () => Task.CompletedTask;
        await navigation.GoToLoginAsync();
        Assert.Equal(2, browser.Calls);
    }

    [Fact]
    public async Task BrowserDisconnectionDuringNavigationIsHandled()
    {
        var browser = new Browser { Invoke = () => Task.FromException(new JSDisconnectedException("Browser left")) };
        var navigation = new SignInNavigation(browser);
        await navigation.OnConnectionUpAsync(null!, default);
        await navigation.GoToLoginAsync();
    }

    [Fact]
    public async Task ConnectedBrowserErrorsAreNotSuppressed()
    {
        var browser = new Browser { Invoke = () => Task.FromException(new TaskCanceledException("Interop timed out")) };
        var navigation = new SignInNavigation(browser);
        await navigation.OnConnectionUpAsync(null!, default);
        await Assert.ThrowsAsync<TaskCanceledException>(() => navigation.GoToLoginAsync());
    }

    private sealed class Browser : IJSRuntime
    {
        public int Calls { get; private set; }
        public string? Identifier { get; private set; }
        public object?[]? Arguments { get; private set; }
        public Func<Task> Invoke { get; set; } = () => Task.CompletedTask;
        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Calls++; Identifier = identifier; Arguments = args;
            await Invoke();
            return default!;
        }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }
}
