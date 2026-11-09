using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NeAvito.Controllers;
using NeAvito.Data;
using NeAvito.Models;

namespace NeAvito.Tests;

public sealed class OrderTests
{
    internal static async Task<HttpClient> Account(WebApplicationFactory<Program> app, string name)
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true });
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register", new
        { email = name + "@example.com", password = "TestPassword123!", displayName = name })).StatusCode);
        await TestAccountEmail.Confirm(app, client, name + "@example.com");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login", new
        { email = name + "@example.com", password = "TestPassword123!" })).StatusCode);
        return client;
    }
    internal static async Task<Guid> Product(HttpClient seller, int stock = 5)
    {
        var created = await seller.PostAsJsonAsync("/api/seller/products", new
        { name = "Original phone", description = "Description", price = 100.50m, stock, categoryId = 3 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = doc.RootElement.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await seller.PostAsync($"/api/seller/products/{id}/publish", null)).StatusCode);
        return id;
    }
    internal static async Task<CheckoutRequest> Request(HttpClient buyer, Guid? requestId = null)
    {
        using var cart = JsonDocument.Parse(await buyer.GetStringAsync("/api/cart"));
        return new CheckoutRequest
        {
            RequestId = requestId ?? Guid.NewGuid(), RecipientName = "Buyer", Phone = "+79991234567", DeliveryAddress = "Pickup location",
            Items = cart.RootElement.GetProperty("items").EnumerateArray().Select(x => new CheckoutItemRequest
            {
                ProductId = x.GetProperty("productId").GetGuid(), Version = x.GetProperty("version").GetGuid(),
                Quantity = x.GetProperty("quantity").GetInt32(), UnitPrice = x.GetProperty("price").GetDecimal()
            }).ToList()
        };
    }
    private static async Task<Guid> OrderId(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("orders")[0].GetProperty("id").GetGuid();
    }
    private static int Stock(AuthFactory app, Guid id)
    {
        using var scope = app.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AppDbContext>().Products.Single(x => x.Id == id).Stock;
    }

    [Fact]
    public async Task CheckoutSplitsBySellerClearsCartAndIsIdempotent()
    {
        using var app = new AuthFactory(); using var visitor = app.Client();
        using var seller1 = await Account(app, "seller1"); using var seller2 = await Account(app, "seller2"); using var buyer = await Account(app, "buyer");
        var product1 = await Product(seller1); var product2 = await Product(seller2);
        await buyer.PutAsJsonAsync($"/api/cart/items/{product1}", new { quantity = 2 });
        await buyer.PutAsJsonAsync($"/api/cart/items/{product2}", new { quantity = 1 });
        var request = await Request(buyer);
        var response = await buyer.PostAsJsonAsync("/api/orders/checkout", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, result.RootElement.GetProperty("orders").GetArrayLength());
        Assert.Equal(3, Stock(app, product1)); Assert.Equal(4, Stock(app, product2));
        using var cart = JsonDocument.Parse(await buyer.GetStringAsync("/api/cart"));
        Assert.Equal(0, cart.RootElement.GetProperty("itemCount").GetInt32());
        var repeated = await buyer.PostAsJsonAsync("/api/orders/checkout", request);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(await OrderId(response), await OrderId(repeated));
        Assert.Equal(3, Stock(app, product1));
        request.DeliveryAddress = "Other address";
        Assert.Equal(HttpStatusCode.Conflict, (await buyer.PostAsJsonAsync("/api/orders/checkout", request)).StatusCode);
        using var list = JsonDocument.Parse(await seller1.GetStringAsync("/api/seller/orders"));
        Assert.Equal(1, list.RootElement.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task ChangedPriceOrStockRollsBackEntireCheckout()
    {
        using var app = new AuthFactory(); using var visitor = app.Client();
        using var seller = await Account(app, "seller"); using var buyer = await Account(app, "buyer");
        var first = await Product(seller); var second = await Product(seller);
        await buyer.PutAsJsonAsync($"/api/cart/items/{first}", new { quantity = 1 });
        await buyer.PutAsJsonAsync($"/api/cart/items/{second}", new { quantity = 2 });
        var request = await Request(buyer);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var p = db.Products.Single(x => x.Id == second); p.PriceMinor = 20000; p.Version = Guid.NewGuid(); db.SaveChanges();
        }
        Assert.Equal(HttpStatusCode.Conflict, (await buyer.PostAsJsonAsync("/api/orders/checkout", request)).StatusCode);
        Assert.Equal(5, Stock(app, first));
        var updated = await Request(buyer);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var p = db.Products.Single(x => x.Id == second); p.Stock = 1; p.Version = Guid.NewGuid(); db.SaveChanges();
        }
        Assert.Equal(HttpStatusCode.Conflict, (await buyer.PostAsJsonAsync("/api/orders/checkout", updated)).StatusCode);
        using var cart = JsonDocument.Parse(await buyer.GetStringAsync("/api/cart"));
        Assert.Equal(2, cart.RootElement.GetProperty("itemCount").GetInt32());
        using var orders = JsonDocument.Parse(await buyer.GetStringAsync("/api/orders"));
        Assert.Equal(0, orders.RootElement.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task CancellationRestoresStockOnceAndProtectsAccess()
    {
        using var app = new AuthFactory(); using var visitor = app.Client();
        using var seller = await Account(app, "seller"); using var buyer = await Account(app, "buyer"); using var outsider = await Account(app, "outsider");
        var product = await Product(seller);
        await buyer.PutAsJsonAsync($"/api/cart/items/{product}", new { quantity = 2 });
        var response = await buyer.PostAsJsonAsync("/api/orders/checkout", await Request(buyer));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = await OrderId(response);
        Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.GetAsync($"/api/orders/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/orders/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsync($"/api/orders/{id}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsJsonAsync($"/api/seller/orders/{id}/status", new { status = "Cancelled" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await seller.PostAsJsonAsync($"/api/seller/orders/{id}/status", new { status = "Confirmed" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await buyer.PostAsync($"/api/orders/{id}/cancel", null)).StatusCode);
        Assert.Equal(5, Stock(app, product));
        Assert.Equal(HttpStatusCode.OK, (await seller.PostAsJsonAsync($"/api/seller/orders/{id}/status", new { status = "Cancelled" })).StatusCode);
        Assert.Equal(5, Stock(app, product));
        Assert.Equal(HttpStatusCode.Conflict, (await seller.PostAsJsonAsync($"/api/seller/orders/{id}/status", new { status = "Confirmed" })).StatusCode);
    }

    [Fact]
    public async Task CompletedOrderKeepsSnapshotAndCannotBeCancelled()
    {
        using var app = new AuthFactory(); using var visitor = app.Client();
        using var seller = await Account(app, "seller"); using var buyer = await Account(app, "buyer");
        var product = await Product(seller);
        await buyer.PutAsJsonAsync($"/api/cart/items/{product}", new { quantity = 1 });
        var response = await buyer.PostAsJsonAsync("/api/orders/checkout", await Request(buyer));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = await OrderId(response);
        Assert.Equal(HttpStatusCode.Conflict, (await seller.PostAsJsonAsync($"/api/seller/orders/{id}/status", new { status = "Completed" })).StatusCode);
        await seller.PostAsJsonAsync($"/api/seller/orders/{id}/status", new { status = "Confirmed" });
        Assert.Equal(HttpStatusCode.OK, (await seller.PostAsJsonAsync($"/api/seller/orders/{id}/status", new { status = "Completed" })).StatusCode);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var p = db.Products.Single(x => x.Id == product); p.Name = "Changed phone"; p.PriceMinor = 99900;
            p.Status = ProductStatus.Unpublished; p.Version = Guid.NewGuid(); db.SaveChanges();
        }
        using var order = JsonDocument.Parse(await buyer.GetStringAsync($"/api/orders/{id}"));
        Assert.Equal("Original phone", order.RootElement.GetProperty("items")[0].GetProperty("productName").GetString());
        Assert.Equal(100.50m, order.RootElement.GetProperty("total").GetDecimal());
        Assert.Equal(HttpStatusCode.Conflict, (await buyer.PostAsync($"/api/orders/{id}/cancel", null)).StatusCode);
        Assert.Equal(4, Stock(app, product));
    }

    [Fact]
    public async Task ChangedCartAndInvalidContactsDoNotCreateOrders()
    {
        using var app = new AuthFactory(); using var visitor = app.Client();
        using var seller = await Account(app, "seller"); using var buyer = await Account(app, "buyer");
        var product = await Product(seller);
        await buyer.PutAsJsonAsync($"/api/cart/items/{product}", new { quantity = 1 });
        var request = await Request(buyer);
        await buyer.PutAsJsonAsync($"/api/cart/items/{product}", new { quantity = 2 });
        Assert.Equal(HttpStatusCode.Conflict, (await buyer.PostAsJsonAsync("/api/orders/checkout", request)).StatusCode);
        request = await Request(buyer); request.Phone = "invalid";
        Assert.Equal(HttpStatusCode.BadRequest, (await buyer.PostAsJsonAsync("/api/orders/checkout", request)).StatusCode);
        Assert.Equal(5, Stock(app, product));
    }
}
