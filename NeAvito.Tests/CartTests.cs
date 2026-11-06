using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NeAvito.Data;
using NeAvito.Models;

namespace NeAvito.Tests;

public sealed class CartTests
{
    private static readonly Guid ProductId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static void Seed(AuthFactory app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var seller = new AppUser { Id = "cart-seller", DisplayName = "Seller", UserName = "cart-seller@example.com" };
        db.Users.Add(seller);
        db.Products.Add(new Product { Id = ProductId, Name = "Phone", Description = "Description", Seller = seller,
            CategoryId = 3, PriceMinor = 12345, Stock = 10, Status = ProductStatus.Published });
        db.SaveChanges();
    }
    private static async Task<HttpClient> Buyer(AuthFactory app, string name)
    {
        var client = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true });
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register", new
        { email = name + "@example.com", password = "TestPassword123!", displayName = name })).StatusCode);
        await Login(client, name);
        return client;
    }
    private static async Task Login(HttpClient client, string name) => Assert.Equal(HttpStatusCode.OK,
        (await client.PostAsJsonAsync("/api/auth/login", new { email = name + "@example.com", password = "TestPassword123!" })).StatusCode);
    private static async Task<JsonDocument> Cart(HttpClient client) => JsonDocument.Parse(await client.GetStringAsync("/api/cart"));
    private static Task<HttpResponseMessage> Set(HttpClient client, int quantity) =>
        client.PutAsJsonAsync($"/api/cart/items/{ProductId}", new { quantity });
    private static void ChangeProduct(AuthFactory app, Action<Product> change)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var product = db.Products.Single(x => x.Id == ProductId);
        change(product); product.Version = Guid.NewGuid(); db.SaveChanges();
    }

    [Fact]
    public async Task AddSetQuantityAndRemoveAreIdempotent()
    {
        using var app = new AuthFactory(); using var visitor = app.Client(); Seed(app);
        using var buyer = await Buyer(app, "buyer");
        using var empty = await Cart(buyer);
        Assert.Equal(0m, empty.RootElement.GetProperty("total").GetDecimal());
        Assert.False(empty.RootElement.GetProperty("canCheckout").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await Set(buyer, 2)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Set(buyer, 2)).StatusCode);
        using var added = await Cart(buyer);
        Assert.Equal(1, added.RootElement.GetProperty("itemCount").GetInt32());
        Assert.Equal(246.90m, added.RootElement.GetProperty("total").GetDecimal());
        Assert.True(added.RootElement.GetProperty("canCheckout").GetBoolean());
        await Set(buyer, 3);
        using var changed = await Cart(buyer);
        Assert.Equal(370.35m, changed.RootElement.GetProperty("total").GetDecimal());
        Assert.Equal(HttpStatusCode.NoContent, (await buyer.DeleteAsync($"/api/cart/items/{ProductId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await buyer.DeleteAsync($"/api/cart/items/{ProductId}")).StatusCode);
        using var removed = await Cart(buyer);
        Assert.Equal(0, removed.RootElement.GetProperty("itemCount").GetInt32());
    }

    [Fact]
    public async Task CartIsPrivateAndPersistsAfterLogin()
    {
        using var app = new AuthFactory(); using var visitor = app.Client(); Seed(app);
        using var first = await Buyer(app, "first"); using var second = await Buyer(app, "second");
        Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.GetAsync("/api/cart")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Set(visitor, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.DeleteAsync("/api/cart")).StatusCode);
        await Set(first, 2); await Set(second, 1);
        await second.DeleteAsync($"/api/cart/items/{ProductId}");
        using var firstCart = await Cart(first);
        Assert.Equal(2, firstCart.RootElement.GetProperty("totalQuantity").GetInt64());
        await first.PostAsync("/api/auth/logout", null);
        await Login(first, "first");
        using var restored = await Cart(first);
        Assert.Equal(2, restored.RootElement.GetProperty("totalQuantity").GetInt64());
        await second.DeleteAsync("/api/cart");
        using var preserved = await Cart(first);
        Assert.Equal(1, preserved.RootElement.GetProperty("itemCount").GetInt32());
        await first.DeleteAsync("/api/cart");
        using var cleared = await Cart(first);
        Assert.Equal(0, cleared.RootElement.GetProperty("itemCount").GetInt32());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10001)]
    public async Task InvalidQuantityIsRejected(int quantity)
    {
        using var app = new AuthFactory(); using var visitor = app.Client(); Seed(app);
        using var buyer = await Buyer(app, "buyer");
        Assert.Equal(HttpStatusCode.BadRequest, (await Set(buyer, quantity)).StatusCode);
    }

    [Fact]
    public async Task HiddenMissingAndInsufficientStockCannotBeAdded()
    {
        using var app = new AuthFactory(); using var visitor = app.Client(); Seed(app);
        using var buyer = await Buyer(app, "buyer");
        Assert.Equal(HttpStatusCode.Conflict, (await Set(buyer, 11)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await buyer.PutAsJsonAsync($"/api/cart/items/{Guid.NewGuid()}", new { quantity = 1 })).StatusCode);
        ChangeProduct(app, p => p.Status = ProductStatus.Unpublished);
        Assert.Equal(HttpStatusCode.NotFound, (await Set(buyer, 1)).StatusCode);
    }

    [Fact]
    public async Task PriceAndStockChangesAreShownWithoutDroppingItems()
    {
        using var app = new AuthFactory(); using var visitor = app.Client(); Seed(app);
        using var buyer = await Buyer(app, "buyer");
        await Set(buyer, 2);
        ChangeProduct(app, p => { p.PriceMinor = 15000; p.Stock = 1; });
        using var changed = await Cart(buyer);
        var root = changed.RootElement;
        Assert.True(root.GetProperty("hasPriceChanges").GetBoolean());
        Assert.Equal(300m, root.GetProperty("total").GetDecimal());
        Assert.Equal(0m, root.GetProperty("availableTotal").GetDecimal());
        Assert.False(root.GetProperty("canCheckout").GetBoolean());
        Assert.Equal(123.45m, root.GetProperty("items")[0].GetProperty("priceAtAddition").GetDecimal());
        ChangeProduct(app, p => p.Status = ProductStatus.Unpublished);
        using var hidden = await Cart(buyer);
        Assert.Equal(1, hidden.RootElement.GetProperty("itemCount").GetInt32());
        Assert.Contains("снят", hidden.RootElement.GetProperty("items")[0].GetProperty("unavailableReason").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await buyer.DeleteAsync($"/api/cart/items/{ProductId}")).StatusCode);
    }
}
