using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NeAvito.Data;
using Npgsql;

namespace NeAvito.Tests;

public sealed class PostgreSqlFactAttribute : FactAttribute
{
    public PostgreSqlFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("NEAVITO_TEST_POSTGRES") != "1")
            Skip = "Для проверки реального PostgreSQL задайте NEAVITO_TEST_POSTGRES=1 и настройте User Secrets API.";
    }
}

public sealed class PostgreSqlOrderTests
{
    [PostgreSqlFact]
    public async Task ConcurrentBuyersCannotPurchaseTheSameLastItem()
    {
        using var app = new PostgreSqlFactory();
        app.Initialize();
        using var seller = await OrderTests.Account(app, "seller");
        using var buyer1 = await OrderTests.Account(app, "buyer1");
        using var buyer2 = await OrderTests.Account(app, "buyer2");
        var product = await OrderTests.Product(seller, 1);
        await buyer1.PutAsJsonAsync($"/api/cart/items/{product}", new { quantity = 1 });
        await buyer2.PutAsJsonAsync($"/api/cart/items/{product}", new { quantity = 1 });
        var request1 = await OrderTests.Request(buyer1); var request2 = await OrderTests.Request(buyer2);
        var results = await Task.WhenAll(buyer1.PostAsJsonAsync("/api/orders/checkout", request1),
            buyer2.PostAsJsonAsync("/api/orders/checkout", request2));
        Assert.Single(results, x => x.StatusCode == HttpStatusCode.Created);
        Assert.Single(results, x => x.StatusCode == HttpStatusCode.Conflict);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(0, db.Products.Single(x => x.Id == product).Stock);
            Assert.Equal(1, db.Orders.Count());
        }
        var winner = results[0].StatusCode == HttpStatusCode.Created ? buyer1 : buyer2;
        var success = results.Single(x => x.StatusCode == HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await success.Content.ReadAsStringAsync());
        var orderId = doc.RootElement.GetProperty("orders")[0].GetProperty("id").GetGuid();
        var cancelResults = await Task.WhenAll(winner.PostAsync($"/api/orders/{orderId}/cancel", null),
            seller.PostAsJsonAsync($"/api/seller/orders/{orderId}/status", new { status = "Cancelled" }));
        Assert.All(cancelResults, r => Assert.True(r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict));
        Assert.Equal(HttpStatusCode.OK, (await winner.PostAsync($"/api/orders/{orderId}/cancel", null)).StatusCode);
        using var after = app.Services.CreateScope();
        Assert.Equal(1, after.ServiceProvider.GetRequiredService<AppDbContext>().Products.Single(x => x.Id == product).Stock);
    }

    [PostgreSqlFact]
    public async Task ConcurrentDuplicateCheckoutCreatesOnlyOneOrder()
    {
        using var app = new PostgreSqlFactory(); app.Initialize();
        using var seller = await OrderTests.Account(app, "seller"); using var buyer = await OrderTests.Account(app, "buyer");
        var product = await OrderTests.Product(seller, 3);
        await buyer.PutAsJsonAsync($"/api/cart/items/{product}", new { quantity = 1 });
        var request = await OrderTests.Request(buyer);
        var results = await Task.WhenAll(buyer.PostAsJsonAsync("/api/orders/checkout", request), buyer.PostAsJsonAsync("/api/orders/checkout", request));
        Assert.Single(results, x => x.StatusCode == HttpStatusCode.Created);
        Assert.All(results, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK or HttpStatusCode.Conflict));
        Assert.Equal(HttpStatusCode.OK, (await buyer.PostAsJsonAsync("/api/orders/checkout", request)).StatusCode);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, db.Orders.Count()); Assert.Equal(2, db.Products.Single(x => x.Id == product).Stock);
    }
}

internal sealed class PostgreSqlFactory : WebApplicationFactory<Program>
{
    private readonly string schema = "neavito_test_" + Guid.NewGuid().ToString("N");
    private readonly string baseConnection = new ConfigurationBuilder().AddUserSecrets<Program>().Build()
        .GetConnectionString("Postgres") ?? throw new InvalidOperationException("Настройте User Secrets API.");
    private bool created;
    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.Add("X-NeAvito-Request", "1");
    }
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var connection = new NpgsqlConnectionStringBuilder(baseConnection) { SearchPath = schema }.ConnectionString;
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = connection }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<AppDbContext>(); services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
            services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connection));
        });
    }
    public void Initialize()
    {
        using var connection = new NpgsqlConnection(baseConnection); connection.Open();
        using var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", connection); create.ExecuteNonQuery(); created = true;
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.ExecuteSqlRaw(db.Database.GenerateCreateScript());
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && created)
        {
            using var connection = new NpgsqlConnection(baseConnection); connection.Open();
            using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", connection); drop.ExecuteNonQuery();
        }
    }
}


