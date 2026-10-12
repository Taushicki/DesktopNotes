using DesktopNotes.Infrastructure;
using Xunit;

namespace DesktopNotes.Tests;

public sealed class SingleInstanceTests
{
    [Fact]
    public void ProductionInstanceHoldsInstallerGuardForItsLifetime()
    {
        var name = "DesktopNotes-test-" + Guid.NewGuid().ToString("N");
        var installationMutex = "Local\\" + name;
        using (var instance = new SingleInstance(name, installationMutex))
        {
            Assert.True(instance.IsPrimary);
            Assert.True(Mutex.TryOpenExisting(installationMutex, out var guard));
            guard!.Dispose();
        }
        Assert.False(Mutex.TryOpenExisting(installationMutex, out var remaining));
        remaining?.Dispose();
    }

    [Fact]
    public async Task SecondaryForwardsCreateCommandToPrimary()
    {
        var name = "DesktopNotes-test-" + Guid.NewGuid().ToString("N");
        using var primary = new SingleInstance(name);
        using var secondary = new SingleInstance(name);
        Assert.True(primary.IsPrimary);
        Assert.False(secondary.IsPrimary);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.StartListening(command => { received.TrySetResult(command); return Task.CompletedTask; });
        await secondary.SendAsync("--new-note");
        Assert.Equal("--new-note", await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
