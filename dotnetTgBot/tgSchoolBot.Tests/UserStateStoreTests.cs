using dotnetTgBot.Services;

namespace tgSchoolBot.Tests;

public class UserStateStoreTests
{
    [Fact]
    public void TryGetValue_ReturnsStoredState()
    {
        var store = new UserStateStore(TimeSpan.FromMinutes(5));
        var state = new UserState { Step = VerificationStep.WaitingForFullName };

        store[123] = state;

        Assert.True(store.TryGetValue(123, out var stored));
        Assert.Same(state, stored);
    }

    [Fact]
    public void TryRemove_RemovesStoredState()
    {
        var store = new UserStateStore(TimeSpan.FromMinutes(5));
        store[123] = new UserState { Step = VerificationStep.WaitingForPhoneNumber };

        Assert.True(store.TryRemove(123, out var removed));
        Assert.Equal(VerificationStep.WaitingForPhoneNumber, removed.Step);
        Assert.False(store.TryGetValue(123, out _));
    }

    [Fact]
    public async Task TryGetValue_RemovesExpiredState()
    {
        var store = new UserStateStore(TimeSpan.FromMilliseconds(10));
        store[123] = new UserState { Step = VerificationStep.WaitingForFullName };

        await Task.Delay(50);

        Assert.False(store.TryGetValue(123, out _));
    }
}
