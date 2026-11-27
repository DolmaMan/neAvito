using System.Net;

namespace NeAvito.Tests;

public sealed class StaticAssetTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/catalog.js?v=p2-1")]
    [InlineData("/app.js?v=p2-1")]
    [InlineData("/app.css?v=p2-1")]
    public async Task InterfaceFilesMustRevalidateBeforeReuse(string path)
    {
        using var app = new AuthFactory(enableLegacy: false);
        using var client = app.Client();
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoCache);
        Assert.NotNull(response.Headers.ETag);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.IfNoneMatch.Add(response.Headers.ETag!);
        using var unchanged = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        Assert.True(unchanged.Headers.CacheControl?.NoCache);
    }
}
