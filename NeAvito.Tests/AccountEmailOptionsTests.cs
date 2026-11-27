using NeAvito.Services;

namespace NeAvito.Tests;

public class AccountEmailOptionsTests
{
    [Theory]
    [InlineData("http://localhost:8025")]
    [InlineData("smtp.gmail.com:587")]
    [InlineData("smtp.gmail.com/path")]
    public void SmtpHostMustNotBeAWebAddress(string host)
    {
        var options = new AccountEmailOptions
        {
            PublicOrigin = "https://localhost:7353",
            From = "sender@example.com",
            Host = host,
            Port = 587,
        };
        Assert.False(options.IsValid(true));
        options.Host = "smtp.gmail.com";
        Assert.True(options.IsValid(true));
    }

    [Fact]
    public void PlainSmtpIsAllowedOnlyForDevelopment()
    {
        var options = new AccountEmailOptions
        {
            PublicOrigin = "https://localhost:7353",
            From = "neavito@demo.test",
            Host = "mailpit",
            Port = 1025,
            EnableSsl = false,
        };
        Assert.True(options.IsValid(true));
        Assert.False(options.IsValid(false));
        options.EnableSsl = true;
        Assert.True(options.IsValid(false));
    }
}
