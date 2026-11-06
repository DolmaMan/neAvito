using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SkiaSharp;
namespace NeAvito.Tests;

public sealed class SellerProductsTests
{
    private static object Data(decimal price = 123.45m, int stock = 2, int categoryId = 3) =>
        new { name = "Телефон", description = "Описание телефона", price, stock, categoryId };

    private static async Task<HttpClient> Seller(AuthFactory app, string name)
    {
        var client = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true });
        var email = name + "@example.com";
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register",
            new { email, password = "TestPassword123!", displayName = name })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login",
            new { email, password = "TestPassword123!" })).StatusCode);
        return client;
    }
    private static async Task<(Guid Id, Guid Version)> Create(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/seller/products", Data());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("id").GetGuid(), doc.RootElement.GetProperty("version").GetGuid());
    }
    private static MultipartFormDataContent Photo(bool valid = true)
    {
        byte[] bytes;
        if (valid)
        {
            using var bitmap = new SKBitmap(2, 2);
            bitmap.Erase(SKColors.Red);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            bytes = data.ToArray();
        }
        else bytes = "fake image"u8.ToArray();
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(bytes), "file", "photo.png");
        return form;
    }

    [Fact]
    public async Task DraftEditPublishUnpublish()
    {
        using var app = new AuthFactory(); using var visitor = app.Client(); using var seller = await Seller(app, "seller");
        var (id, version) = await Create(seller);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/api/catalog/products/{id}")).StatusCode);
        var edited = await seller.PutAsJsonAsync($"/api/seller/products/{id}", new
        { name = "Новый телефон", description = "Новое описание", price = 500.25m, stock = 0, categoryId = 3, version });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await seller.PostAsync($"/api/seller/products/{id}/publish", null)).StatusCode);
        using var detail = JsonDocument.Parse(await visitor.GetStringAsync($"/api/catalog/products/{id}"));
        Assert.Equal(500.25m, detail.RootElement.GetProperty("price").GetDecimal());
        Assert.False(detail.RootElement.GetProperty("isAvailable").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await seller.PostAsync($"/api/seller/products/{id}/unpublish", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/api/catalog/products/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await seller.GetAsync($"/api/seller/products/{id}")).StatusCode);
    }

    [Fact]
    public async Task ForeignUserCannotAccessOrModifyProduct()
    {
        using var app = new AuthFactory(); using var visitor = app.Client();
        using var owner = await Seller(app, "owner"); using var other = await Seller(app, "other");
        var (id, _) = await Create(owner);
        Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.PostAsJsonAsync("/api/seller/products", Data())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/seller/products/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"/api/seller/products/{id}", Data())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/seller/products/{id}/publish", null)).StatusCode);
        using var photo = Photo();
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/seller/products/{id}/images", photo)).StatusCode);
        using var list = JsonDocument.Parse(await other.GetStringAsync("/api/seller/products"));
        Assert.Equal(0, list.RootElement.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task InvalidFieldsAndStaleEditsAreRejected()
    {
        using var app = new AuthFactory(); using var visitor = app.Client(); using var seller = await Seller(app, "seller");
        foreach (var data in new[] { Data(0), Data(1.001m), Data(stock: -1), Data(categoryId: 999) })
            Assert.Equal(HttpStatusCode.BadRequest, (await seller.PostAsJsonAsync("/api/seller/products", data)).StatusCode);
        var (id, version) = await Create(seller);
        await seller.PostAsync($"/api/seller/products/{id}/publish", null);
        Assert.Equal(HttpStatusCode.Conflict, (await seller.PutAsJsonAsync($"/api/seller/products/{id}", new
        { name = "Телефон", description = "Описание", price = 123m, stock = 1, categoryId = 3, version })).StatusCode);
    }

    [Fact]
    public async Task PhotoIsValidatedVisibleOnlyWhenPublishedAndCanBeRemoved()
    {
        using var app = new AuthFactory(); using var visitor = app.Client(); using var seller = await Seller(app, "seller");
        var (id, _) = await Create(seller);
        using var bad = Photo(false);
        Assert.Equal(HttpStatusCode.BadRequest, (await seller.PostAsync($"/api/seller/products/{id}/images", bad)).StatusCode);
        using var form = Photo();
        var uploaded = await seller.PostAsync($"/api/seller/products/{id}/images", form);
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        using var doc = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync());
        var url = doc.RootElement.GetProperty("url").GetString()!;
        var imageId = doc.RootElement.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync(url)).StatusCode);
        var privateImage = await seller.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, privateImage.StatusCode);
        Assert.Equal("image/webp", privateImage.Content.Headers.ContentType!.MediaType);
        await seller.PostAsync($"/api/seller/products/{id}/publish", null);
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync(url)).StatusCode);
        await seller.PostAsync($"/api/seller/products/{id}/unpublish", null);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await seller.DeleteAsync($"/api/seller/products/{id}/images/{imageId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await seller.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task MoreThanTenPhotosAreRejected()
    {
        using var app = new AuthFactory(); using var visitor = app.Client(); using var seller = await Seller(app, "seller");
        var (id, _) = await Create(seller);
        for (var i = 0; i < 10; i++)
        {
            using var photo = Photo();
            Assert.Equal(HttpStatusCode.Created, (await seller.PostAsync($"/api/seller/products/{id}/images", photo)).StatusCode);
        }
        using var extra = Photo();
        Assert.Equal(HttpStatusCode.BadRequest, (await seller.PostAsync($"/api/seller/products/{id}/images", extra)).StatusCode);
    }
}
