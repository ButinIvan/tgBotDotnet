using dotnetTgBot.Services;

namespace tgSchoolBot.Tests;

public class AdminLoginCodeServiceTests
{
    [Fact]
    public void CreateCode_ReturnsSixDigitNumericCode()
    {
        var service = new AdminLoginCodeService();

        var code = service.CreateCode(123);

        Assert.Matches("^[0-9]{6}$", code);
    }

    [Fact]
    public void TryConsumeCode_AcceptsCodeOnlyOnce()
    {
        var service = new AdminLoginCodeService();
        var code = service.CreateCode(123);

        Assert.True(service.TryConsumeCode(123, code));
        Assert.False(service.TryConsumeCode(123, code));
    }

    [Fact]
    public void TryConsumeCode_RejectsCodeForAnotherUser()
    {
        var service = new AdminLoginCodeService();
        var code = service.CreateCode(123);

        Assert.False(service.TryConsumeCode(456, code));
        Assert.True(service.TryConsumeCode(123, code));
    }

    [Fact]
    public void CreateCode_ReplacesPreviousCodeForSameUser()
    {
        var service = new AdminLoginCodeService();
        var oldCode = service.CreateCode(123);
        var newCode = service.CreateCode(123);

        Assert.False(service.TryConsumeCode(123, oldCode));
        Assert.True(service.TryConsumeCode(123, newCode));
    }
}
