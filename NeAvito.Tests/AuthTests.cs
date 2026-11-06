using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NeAvito.Data;

namespace NeAvito.Tests;

public sealed class AuthTests
{
    [Fact]
    public async Task RegisterLoginProfileLogout()
    {
        using var app = new AuthFactory();
        using var client = app.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Register(client)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(client, "TestPassword123!")).StatusCode);
        var profile = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        Assert.Contains("buyer@example.com", await profile.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task DuplicateEmailIsRejectedIgnoringCase()
    {
        using var app = new AuthFactory();
        using var client = app.Client();
        Assert.Equal(HttpStatusCode.Created, (await Register(client)).StatusCode);
        var duplicate = await client.PostAsJsonAsync("/api/auth/register", new
        { email = "BUYER@example.com", password = "TestPassword123!", displayName = "Other" });
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
    }

    [Fact]
    public async Task InvalidRegistrationIsRejected()
    {
        using var app = new AuthFactory();
        using var client = app.Client();
        var response = await client.PostAsJsonAsync("/api/auth/register", new
        { email = "invalid", password = "123", displayName = " " });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task FiveWrongPasswordsLockTheAccount()
    {
        using var app = new AuthFactory();
        using var client = app.Client();
        Assert.Equal(HttpStatusCode.Created, (await Register(client)).StatusCode);
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, "WrongPassword123!")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, "TestPassword123!")).StatusCode);
    }

    private static Task<HttpResponseMessage> Register(HttpClient client) => client.PostAsJsonAsync("/api/auth/register",
        new { email = "buyer@example.com", password = "TestPassword123!", displayName = "Buyer" });
    private static Task<HttpResponseMessage> Login(HttpClient client, string password) => client.PostAsJsonAsync("/api/auth/login",
        new { email = "buyer@example.com", password });
}

internal sealed class AuthFactory : WebApplicationFactory<Program>
{
    private readonly string imageRoot = Path.Combine(Path.GetTempPath(), "neavito-tests", Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.Add("X-NeAvito-Request", "1");
    }
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        connection.Open();
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = "Host=unused", ["Images:StoragePath"] = imageRoot }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<AppDbContext>();
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite(connection));
        });
    }
    public HttpClient Client()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true });
        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
        return client;
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            connection.Dispose();
            if (Directory.Exists(imageRoot)) Directory.Delete(imageRoot, true);
        }
    }
}


