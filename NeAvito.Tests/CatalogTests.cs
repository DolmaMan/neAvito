using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NeAvito.Data;
using NeAvito.Models;

namespace NeAvito.Tests;

public sealed class CatalogTests
{
    private static readonly Guid AvailableId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid SoldOutId = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid HiddenId = Guid.Parse("10000000-0000-0000-0000-000000000003");

    private static HttpClient Seed(AuthFactory app)
    {
        var client = app.Client();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var seller = new AppUser { Id = "seller", UserName = "seller@example.com", Email = "seller@example.com", DisplayName = "Seller" };
        var date = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        db.Users.Add(seller);
        db.Products.AddRange(
            new Product { Id = AvailableId, Name = "Phone Alpha", Description = "Description", PriceMinor = 100050, Stock = 2, CategoryId = 3, Seller = seller, Status = ProductStatus.Published, CreatedAtUtc = date,
                Images = [new ProductImage { Url = "/images/second.jpg", Position = 1 }, new ProductImage { Url = "/images/first.jpg", Position = 0 }] },
            new Product { Id = SoldOutId, Name = "Phone Beta", PriceMinor = 200000, Stock = 0, CategoryId = 3, Seller = seller, Status = ProductStatus.Published, CreatedAtUtc = date.AddDays(1) },
            new Product { Id = HiddenId, Name = "Hidden Phone", PriceMinor = 10000, Stock = 10, CategoryId = 3, Seller = seller, Status = ProductStatus.Draft, CreatedAtUtc = date.AddDays(2) },
            new Product { Name = "Unpublished Phone", PriceMinor = 10000, Stock = 10, CategoryId = 3, Seller = seller, Status = ProductStatus.Unpublished, CreatedAtUtc = date.AddDays(3) },
            new Product { Name = "Jacket", PriceMinor = 300000, Stock = 1, CategoryId = 1, Seller = seller, Status = ProductStatus.Published, CreatedAtUtc = date.AddDays(4) });
        db.SaveChanges();
        return client;
    }

    [Fact]
    public async Task CatalogHidesUnpublishedAndPaginates()
    {
        using var app = new AuthFactory();
        using var client = Seed(app);
        var response = await client.GetAsync("/api/catalog/products?pageSize=1&sort=price_asc");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal(3, root.GetProperty("totalCount").GetInt32());
        Assert.Equal(3, root.GetProperty("totalPages").GetInt32());
        var item = root.GetProperty("items")[0];
        Assert.Equal(AvailableId, item.GetProperty("id").GetGuid());
        Assert.Equal(1000.50m, item.GetProperty("price").GetDecimal());
        Assert.Equal("/images/first.jpg", item.GetProperty("imageUrl").GetString());
        using var next = JsonDocument.Parse(await client.GetStringAsync("/api/catalog/products?pageSize=1&page=2&sort=price_asc"));
        Assert.Equal(SoldOutId, next.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid());
        Assert.False(next.RootElement.GetProperty("items")[0].GetProperty("isAvailable").GetBoolean());
    }

    [Fact]
    public async Task SearchCategoryAndPricesWorkTogether()
    {
        using var app = new AuthFactory();
        using var client = Seed(app);
        using var json = JsonDocument.Parse(await client.GetStringAsync(
            "/api/catalog/products?search=PHONE&categoryId=3&minPrice=1000.50&maxPrice=1000.50"));
        Assert.Equal(1, json.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(AvailableId, json.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid());
        using var wildcard = JsonDocument.Parse(await client.GetStringAsync("/api/catalog/products?search=%25"));
        Assert.Equal(0, wildcard.RootElement.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task DetailProtectsHiddenProductsAndPrivateSellerData()
    {
        using var app = new AuthFactory();
        using var client = Seed(app);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/catalog/products/{HiddenId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/catalog/products/{Guid.NewGuid()}")).StatusCode);
        var text = await client.GetStringAsync($"/api/catalog/products/{AvailableId}");
        Assert.DoesNotContain("seller@example.com", text);
        using var json = JsonDocument.Parse(text);
        Assert.Equal("Description", json.RootElement.GetProperty("description").GetString());
        Assert.Equal("/images/first.jpg", json.RootElement.GetProperty("images")[0].GetProperty("url").GetString());
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("pageSize=101")]
    [InlineData("minPrice=-1")]
    [InlineData("minPrice=100&maxPrice=10")]
    [InlineData("sort=invalid")]
    public async Task InvalidQueryIsRejected(string query)
    {
        using var app = new AuthFactory();
        using var client = app.Client();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/catalog/products?" + query)).StatusCode);
    }

    [Theory]
    [InlineData("newest", "Jacket")]
    [InlineData("oldest", "Phone Alpha")]
    [InlineData("price_desc", "Jacket")]
    public async Task SortingWorks(string sort, string firstName)
    {
        using var app = new AuthFactory();
        using var client = Seed(app);
        using var json = JsonDocument.Parse(await client.GetStringAsync("/api/catalog/products?sort=" + sort));
        Assert.Equal(firstName, json.RootElement.GetProperty("items")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task CategoriesAndEmptyPageAreAvailable()
    {
        using var app = new AuthFactory();
        using var client = Seed(app);
        using var categories = JsonDocument.Parse(await client.GetStringAsync("/api/catalog/categories"));
        Assert.Equal(6, categories.RootElement.GetArrayLength());
        using var page = JsonDocument.Parse(await client.GetStringAsync("/api/catalog/products?page=100"));
        Assert.Equal(0, page.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(3, page.RootElement.GetProperty("totalCount").GetInt32());
    }
}
