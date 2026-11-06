using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
namespace NeAvito.Tests;

public sealed class ProfileTests
{
    [Fact]
    public async Task ProfileIsPrivateEditableAndDetectsStaleUpdates()
    {
        using var app = new AuthFactory(); using var visitor = app.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.GetAsync("/api/profile")).StatusCode);
        using var first = await OrderTests.Account(app, "first"); using var second = await OrderTests.Account(app, "second");
        using var original = JsonDocument.Parse(await first.GetStringAsync("/api/profile"));
        var version = original.RootElement.GetProperty("version").GetString();
        var update = new { displayName = "Новое имя", phone = "+79991234567", deliveryAddress = "Место получения", version };
        Assert.Equal(HttpStatusCode.OK, (await first.PutAsJsonAsync("/api/profile", update)).StatusCode);
        using var saved = JsonDocument.Parse(await first.GetStringAsync("/api/profile"));
        Assert.Equal("Новое имя", saved.RootElement.GetProperty("displayName").GetString());
        Assert.Equal("+79991234567", saved.RootElement.GetProperty("phone").GetString());
        Assert.Equal("Место получения", saved.RootElement.GetProperty("deliveryAddress").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await first.PutAsJsonAsync("/api/profile", update)).StatusCode);
        using var other = JsonDocument.Parse(await second.GetStringAsync("/api/profile"));
        Assert.Equal("second", other.RootElement.GetProperty("displayName").GetString());
        Assert.Equal("", other.RootElement.GetProperty("deliveryAddress").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await first.PutAsJsonAsync("/api/profile", new
        { displayName = " ", phone = "invalid", version = saved.RootElement.GetProperty("version").GetString() })).StatusCode);
    }

    [Fact]
    public async Task BrowserMutationsNeedCustomHeader()
    {
        using var app = new AuthFactory(); using var client = app.Client();
        client.DefaultRequestHeaders.Remove("X-NeAvito-Request");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/auth/register", new
        { email = "new@example.com", password = "TestPassword123!", displayName = "New" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync("/api/cart")).StatusCode);
    }

    [Fact]
    public async Task CabinetAssetsAreServedWithoutPrivateData()
    {
        using var app = new AuthFactory(); using var client = app.Client();
        var page = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Личный кабинет", await page.Content.ReadAsStringAsync());
        Assert.True(page.Headers.Contains("Content-Security-Policy"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/app.js")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/app.css")).StatusCode);
    }
}
