using dotnetTgBot.Services;

namespace tgSchoolBot.Tests;

public class TelegramHtmlTests
{
    [Fact]
    public void Encode_EscapesTelegramHtmlControlCharacters()
    {
        var encoded = TelegramHtml.Encode("<b>A & B</b> \"quote\"");

        Assert.Equal("&lt;b&gt;A &amp; B&lt;/b&gt; &quot;quote&quot;", encoded);
    }

    [Fact]
    public void Encode_ReturnsEmptyStringForNull()
    {
        Assert.Equal(string.Empty, TelegramHtml.Encode(null));
    }
}
