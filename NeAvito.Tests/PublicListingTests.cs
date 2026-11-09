using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NeAvito.Data;
using NeAvito.Models;
using NeAvito.Services;

namespace NeAvito.Tests;

public sealed class PublicListingTests
{
    private static Listing Seed(AuthFactory app, string owner, long price = 0, ListingStatus status = ListingStatus.Published)
    {
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var listing = new Listing { AuthorId = db.Users.Single(u => u.UserName == owner + "@example.com").Id,
            Title = "Велосипед", Description = "Редкая модель Trek", PriceMinor = price, CategoryId = 5, CityId = 1,
            Condition = ItemCondition.Used, Status = status, PublishedAtUtc = DateTime.UtcNow,
            Contact = new() { Phone = "+79991234567", ConsentedAtUtc = DateTime.UtcNow }, Images = [new() { Position = 0 }] };
        db.Listings.Add(listing); db.SaveChanges();
        var path = scope.ServiceProvider.GetRequiredService<ProductImageStorage>().PathFor(listing.Images[0].Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, [1, 2, 3]);
        return listing;
    }
    private static void Change(AuthFactory app, Guid id, Action<Listing, AppDbContext> action)
    {
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        action(db.Listings.Single(x => x.Id == id), db); db.SaveChanges();
    }
    private static async Task<JsonElement> Json(HttpClient client, string path) => await client.GetFromJsonAsync<JsonElement>(path);

    [Fact]
    public async Task PublicDataHasNoContactAndCombinedFiltersSearchDescription()
    {
        using var app = new AuthFactory(enableLegacy: false); using var guest = app.Client();
        using var owner = await OrderTests.Account(app, "owner"); var free = Seed(app, "owner"); Seed(app, "owner", 10000);
        Seed(app, "owner", status: ListingStatus.Draft);
        var result = await Json(guest, "/api/listings?search=TREK&categoryId=5&cityId=1&condition=Used&minPrice=0&maxPrice=0");
        Assert.Equal(1, result.GetProperty("totalCount").GetInt32()); Assert.Equal(free.Id, result.GetProperty("items")[0].GetProperty("id").GetGuid());
        var detail = await guest.GetStringAsync($"/api/listings/{free.Id}");
        foreach (var text in new[] { result.ToString(), detail })
        { Assert.DoesNotContain("+7999", text); Assert.DoesNotContain("phone", text); Assert.DoesNotContain("consent", text); Assert.DoesNotContain("@example", text); }
        foreach (var filter in new[] { "cityId=2", "categoryId=3", "condition=New", "search=missing" })
            Assert.Equal(0, (await Json(guest, "/api/listings?" + filter)).GetProperty("totalCount").GetInt32());
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync($"/api/listings/{free.Id}/contact")).StatusCode);
        var response = await owner.GetAsync($"/api/listings/{free.Id}/contact");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
    }

    [Theory]
    [InlineData("page=0")][InlineData("page=1000001")][InlineData("pageSize=101")]
    [InlineData("minPrice=-1")][InlineData("minPrice=2&maxPrice=1")][InlineData("sort=other")]
    [InlineData("condition=other")][InlineData("cityId=0")]
    public async Task InvalidFiltersAreRejected(string query)
    { using var app = new AuthFactory(enableLegacy: false); using var guest = app.Client(); Assert.Equal(HttpStatusCode.BadRequest, (await guest.GetAsync("/api/listings?" + query)).StatusCode); }

    [Fact]
    public async Task PaginationAndPriceSortingAreStable()
    {
        using var app = new AuthFactory(enableLegacy: false); using var guest = app.Client(); using var owner = await OrderTests.Account(app, "owner");
        Seed(app, "owner", 100); Seed(app, "owner", 100); Seed(app, "owner", 200);
        var one = await Json(guest, "/api/listings?sort=price_asc&pageSize=1");
        var two = await Json(guest, "/api/listings?sort=price_asc&pageSize=1&page=2");
        Assert.NotEqual(one.GetProperty("items")[0].GetProperty("id").GetGuid(), two.GetProperty("items")[0].GetProperty("id").GetGuid());
        Assert.Equal(one.ToString(), (await Json(guest, "/api/listings?sort=price_asc&pageSize=1")).ToString());
        Assert.Equal(2, (await Json(guest, "/api/listings?sort=price_desc")).GetProperty("items")[0].GetProperty("price").GetDecimal());
        Assert.Empty((await Json(guest, "/api/listings?page=99")).GetProperty("items").EnumerateArray());
    }

    [Theory]
    [InlineData("sold")][InlineData("hidden")][InlineData("blocked")][InlineData("unconfirmed")][InlineData("archived")]
    public async Task VisibilityChangesImmediatelyCloseCardImageAndContact(string change)
    {
        using var app = new AuthFactory(enableLegacy: false); using var guest = app.Client();
        using var owner = await OrderTests.Account(app, "owner"); using var viewer = await OrderTests.Account(app, "viewer"); var listing = Seed(app, "owner");
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/listings/{listing.Id}/contact")).StatusCode);
        Change(app, listing.Id, (x, db) => {
            if (change == "sold") x.Status = ListingStatus.Sold;
            if (change == "archived") x.Status = ListingStatus.Archived;
            if (change == "hidden") x.ModerationHidden = true;
            if (change == "blocked") db.Users.Single(u => u.Id == x.AuthorId).IsBlocked = true;
            if (change == "unconfirmed") db.Users.Single(u => u.Id == x.AuthorId).EmailConfirmed = false;
        });
        Assert.Equal(0, (await Json(guest, "/api/listings")).GetProperty("totalCount").GetInt32());
        foreach (var url in new[] { $"/api/listings/{listing.Id}", $"/api/listing-images/{listing.Images[0].Id}", $"/api/listings/{listing.Id}/contact" })
            Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync(url)).StatusCode);
        if (change == "blocked") {
            Assert.Equal(HttpStatusCode.Unauthorized, (await owner.GetAsync("/api/profile")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await owner.PostAsJsonAsync("/api/auth/login", new { email = "owner@example.com", password = "TestPassword123!" })).StatusCode);
        }
    }

    [Fact]
    public async Task RevokedConsentAndAccountRateLimitAreEnforced()
    {
        using var app = new AuthFactory(enableLegacy: false); using var guest = app.Client();
        using var owner = await OrderTests.Account(app, "owner"); var listing = Seed(app, "owner");
        using (var scope = app.Services.CreateScope()) { var db = scope.ServiceProvider.GetRequiredService<AppDbContext>(); db.Set<ListingContact>().Single().ConsentedAtUtc = null; db.SaveChanges(); }
        for (var i = 0; i < 10; i++) Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/listings/{listing.Id}/contact")).StatusCode);
        var limited = await owner.GetAsync($"/api/listings/{listing.Id}/contact"); Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode); Assert.NotNull(limited.Headers.RetryAfter);
    }

    [Fact]
    public async Task IpLimitAppliesAcrossAccounts()
    {
        using var app = new AuthFactory(enableLegacy: false); using var guest = app.Client();
        for (var account = 0; account < 4; account++) {
            using var client = await OrderTests.Account(app, "viewer" + account);
            for (var i = 0; i < (account == 3 ? 1 : 10); i++) Assert.Equal(account == 3 ? HttpStatusCode.TooManyRequests : HttpStatusCode.NotFound,
                (await client.GetAsync($"/api/listings/{Guid.NewGuid()}/contact")).StatusCode);
        }
    }

    [Fact]
    public async Task LegacyWritesAreGoneButHistoryRemainsPrivateAndOldLinksRedirect()
    {
        using var app = new AuthFactory(enableLegacy: false); using var guest = app.Client();
        using var buyer = await OrderTests.Account(app, "buyer"); using var seller = await OrderTests.Account(app, "seller"); using var other = await OrderTests.Account(app, "other");
        Guid id;
        using (var scope = app.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>(); var buyerId = db.Users.Single(u => u.UserName == "buyer@example.com").Id;
            var order = new Order { BuyerId = buyerId, SellerId = db.Users.Single(u => u.UserName == "seller@example.com").Id, Status = OrderStatus.Completed,
                Phone = "+79990000000", CheckoutBatch = new() { BuyerId = buyerId, RequestId = Guid.NewGuid(), Fingerprint = "fixture" } };
            db.Orders.Add(order); db.SaveChanges(); id = order.Id;
        }
        Assert.Equal(HttpStatusCode.OK, (await buyer.GetAsync($"/api/orders/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await seller.GetAsync($"/api/seller/orders/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/orders/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync($"/api/orders/{id}")).StatusCode);
        foreach (var url in new[] { "/api/cart", "/api/seller/products", "/api/orders/checkout", $"/api/orders/{id}/cancel", $"/api/seller/orders/{id}/status" })
            Assert.Equal(HttpStatusCode.Gone, (await buyer.PostAsJsonAsync(url, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await buyer.GetAsync("/api/cart")).StatusCode);
        Assert.Equal($"/api/listings/{id}", (await guest.GetAsync($"/api/catalog/products/{id}")).Headers.Location!.ToString());
        Assert.Equal($"/api/listing-images/{id}", (await guest.GetAsync($"/api/product-images/{id}")).Headers.Location!.ToString());
    }
}
