using System.Net;
using System.Net.Http.Json;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NeAvito.Data;
using NeAvito.Services;

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
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, "TestPassword123!")).StatusCode);
        await TestAccountEmail.Confirm(app, client, "buyer@example.com");
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
        await TestAccountEmail.Confirm(app, client, "buyer@example.com");
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, "WrongPassword123!")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, "TestPassword123!")).StatusCode);

        const string email = "buyer@example.com";
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/auth/forgot-password", new { email })).StatusCode);
        var (userId, token) = TestAccountEmail.From(app).Links[(email, "reset-password")];
        const string password = "UpdatedPassword456!";
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/auth/reset-password",
            new { userId, token, password })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(client, password)).StatusCode);
    }

    [Fact]
    public async Task LogoutRevokesACopiedCookieAndKeepsOtherSessions()
    {
        using var app = new AuthFactory();
        using var client = app.Client();
        using var secondSession = app.Client();
        using var replay = app.Client(handleCookies: false);
        Assert.Equal(HttpStatusCode.Created, (await Register(client)).StatusCode);
        await TestAccountEmail.Confirm(app, client, "buyer@example.com");
        using var login = await Login(client, "TestPassword123!");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"), value => value.StartsWith("NeAvito.Auth="));
        Assert.Contains("; secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        replay.DefaultRequestHeaders.Add("Cookie", cookie.Split(';', 2)[0]);
        Assert.Equal(HttpStatusCode.OK, (await replay.GetAsync("/api/profile")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(secondSession, "TestPassword123!")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.GetAsync("/api/profile")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await secondSession.GetAsync("/api/profile")).StatusCode);
    }

    [Fact]
    public async Task ResendRecoversFromMailFailureAndDoesNotDiscloseUnknownAccounts()
    {
        using var app = new AuthFactory();
        using var client = app.Client();
        var mail = TestAccountEmail.From(app);
        mail.Available = false;
        using var registration = await Register(client);
        Assert.Equal(HttpStatusCode.Created, registration.StatusCode);
        using var json = JsonDocument.Parse(await registration.Content.ReadAsStringAsync());
        Assert.False(json.RootElement.GetProperty("confirmationSent").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, "TestPassword123!")).StatusCode);

        mail.Available = true;
        using var unknown = await client.PostAsJsonAsync("/api/auth/resend-confirmation", new { email = "unknown@example.com" });
        using var existing = await client.PostAsJsonAsync("/api/auth/resend-confirmation", new { email = "BUYER@example.com" });
        Assert.Equal(HttpStatusCode.Accepted, unknown.StatusCode);
        Assert.Equal(unknown.StatusCode, existing.StatusCode);
        Assert.Equal(await unknown.Content.ReadAsStringAsync(), await existing.Content.ReadAsStringAsync());
        Assert.False(mail.Links.ContainsKey(("unknown@example.com", "confirm-email")));
        await TestAccountEmail.Confirm(app, client, "buyer@example.com");
        Assert.Equal(HttpStatusCode.OK, (await Login(client, "TestPassword123!")).StatusCode);
    }

    [Fact]
    public async Task PasswordResetRejectsInvalidAndUsedTokensAndRevokesOtherSessions()
    {
        using var app = new AuthFactory();
        using var client = app.Client();
        using var otherSession = app.Client();
        Assert.Equal(HttpStatusCode.Created, (await Register(client)).StatusCode);
        await TestAccountEmail.Confirm(app, client, "buyer@example.com");
        Assert.Equal(HttpStatusCode.OK, (await Login(client, "TestPassword123!")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(otherSession, "TestPassword123!")).StatusCode);

        const string email = "buyer@example.com";
        using var request = await client.PostAsJsonAsync("/api/auth/forgot-password", new { email });
        using var unknown = await client.PostAsJsonAsync("/api/auth/forgot-password", new { email = "unknown@example.com" });
        Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);
        Assert.Equal(request.StatusCode, unknown.StatusCode);
        Assert.Equal(await request.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
        var (userId, token) = TestAccountEmail.From(app).Links[(email, "reset-password")];
        const string password = "UpdatedPassword456!";
        Task<HttpResponseMessage> Reset(string value) => client.PostAsJsonAsync(
            "/api/auth/reset-password", new { userId, token = value, password });

        Assert.Equal(HttpStatusCode.BadRequest, (await Reset(token + "invalid")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Reset(token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await otherSession.GetAsync("/api/profile")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Reset(token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, "TestPassword123!")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(client, password)).StatusCode);
    }

    [Fact]
    public async Task EmailRequestsAreRateLimited()
    {
        using var app = new AuthFactory();
        using var client = app.Client();
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync(
                "/api/auth/forgot-password", new { email = "unknown@example.com" })).StatusCode);
        using var limited = await client.PostAsJsonAsync("/api/auth/forgot-password", new { email = "unknown@example.com" });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
    }

    [Fact]
    public async Task DevelopmentEmailContainsAWorkingConfirmationLink()
    {
        using var app = new AuthFactory(useLocalMail: true);
        using var client = app.Client();
        Assert.Equal(HttpStatusCode.Created, (await Register(client)).StatusCode);
        var file = Assert.Single(Directory.GetFiles(app.MailDirectory, "*.eml"));
        var message = await File.ReadAllTextAsync(file);
        var parts = message.Replace("\r\n", "\n").Split("\n\n", 2);
        Assert.Contains("Content-Transfer-Encoding: base64", parts[0], StringComparison.OrdinalIgnoreCase);
        var body = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]));
        var link = new Uri(Assert.Single(body.Split('\n'), line => line.StartsWith("https://")).Trim());
        Assert.Equal("https://localhost", link.GetLeftPart(UriPartial.Authority));
        Assert.StartsWith("#confirm-email?", link.Fragment);
        var query = QueryHelpers.ParseQuery(link.Fragment.Split('?', 2)[1]);
        using var response = await client.PostAsJsonAsync("/api/auth/confirm-email",
            new { userId = query["userId"].ToString(), token = query["token"].ToString() });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(client, "TestPassword123!")).StatusCode);
    }

    [Fact]
    public async Task ChangePasswordRevokesEverySessionAndRequiresNewCredentials()
    {
        using var app = new AuthFactory();
        using var client = app.Client();
        using var other = app.Client();
        await Register(client);
        await TestAccountEmail.Confirm(app, client, "buyer@example.com");
        await Login(client, "TestPassword123!");
        await Login(other, "TestPassword123!");
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/auth/change-password",
            new { currentPassword = "TestPassword123!", newPassword = "UpdatedPassword456!" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/profile")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, "TestPassword123!")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(client, "UpdatedPassword456!")).StatusCode);
    }

    [Theory]
    [InlineData("WrongPassword123!", "UpdatedPassword456!")]
    [InlineData("TestPassword123!", "weak")]
    [InlineData("TestPassword123!", "TestPassword123!")]
    public async Task InvalidPasswordChangePreservesCredentialsAndSessions(string currentPassword, string newPassword)
    {
        using var app = new AuthFactory();
        using var client = app.Client();
        using var other = app.Client();
        await Register(client);
        await TestAccountEmail.Confirm(app, client, "buyer@example.com");
        await Login(client, "TestPassword123!");
        await Login(other, "TestPassword123!");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/change-password",
            new { currentPassword, newPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/profile")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/profile")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(client, "TestPassword123!")).StatusCode);
    }

    [Fact]
    public async Task LogoutAllRevokesCopiedCookiesButDoesNotAffectAnotherAccount()
    {
        using var app = new AuthFactory();
        using var client = app.Client();
        using var other = app.Client();
        using var replay = app.Client(handleCookies: false);
        using var unrelated = await OrderTests.Account(app, "unrelated");
        await Register(client);
        await TestAccountEmail.Confirm(app, client, "buyer@example.com");
        using var login = await Login(client, "TestPassword123!");
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"), value => value.StartsWith("NeAvito.Auth="));
        replay.DefaultRequestHeaders.Add("Cookie", cookie.Split(';', 2)[0]);
        await Login(other, "TestPassword123!");
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout-all", null)).StatusCode);
        foreach (var session in new[] { client, other, replay })
            Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("/api/profile")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await unrelated.GetAsync("/api/profile")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(client, "TestPassword123!")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.GetAsync("/api/profile")).StatusCode);
    }

    [Fact]
    public async Task SecurityActionsRequireAuthenticationAndBrowserMutationHeader()
    {
        using var app = new AuthFactory();
        using var client = app.Client();
        var change = new { currentPassword = "TestPassword123!", newPassword = "UpdatedPassword456!" };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/change-password", change)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/auth/logout-all", null)).StatusCode);
        await Register(client);
        await TestAccountEmail.Confirm(app, client, "buyer@example.com");
        await Login(client, "TestPassword123!");
        client.DefaultRequestHeaders.Remove("X-NeAvito-Request");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/auth/change-password", change)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/auth/logout-all", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    private static Task<HttpResponseMessage> Register(HttpClient client) => client.PostAsJsonAsync("/api/auth/register",
        new { email = "buyer@example.com", password = "TestPassword123!", displayName = "Buyer" });
    private static Task<HttpResponseMessage> Login(HttpClient client, string password) => client.PostAsJsonAsync("/api/auth/login",
        new { email = "buyer@example.com", password });
}

internal sealed class AuthFactory(bool useLocalMail = false, bool enableLegacy = true) : WebApplicationFactory<Program>
{
    private readonly string imageRoot = Path.Combine(Path.GetTempPath(), "neavito-tests", Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    public string MailDirectory => Path.Combine(imageRoot, "mail");
    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.Add("X-NeAvito-Request", "1");
    }
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        connection.Open();
        builder.UseEnvironment(useLocalMail ? "Development" : "Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            TestAccountEmail.Settings().Concat(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = "Host=unused", ["Images:StoragePath"] = imageRoot,
                ["LegacyCommerce:Enabled"] = enableLegacy.ToString(),
                ["AccountEmail:PickupDirectory"] = useLocalMail ? MailDirectory : ""
            })));
        builder.ConfigureServices(services =>
        {
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            if (!useLocalMail) services.AddSingleton<IAccountEmail, TestAccountEmail>();
            services.RemoveAll<AppDbContext>();
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite(connection));
        });
    }
    public HttpClient Client(bool handleCookies = true)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = handleCookies });
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

internal sealed class TestAccountEmail : IAccountEmail
{
    public ConcurrentDictionary<(string Email, string Route), (string UserId, string Token)> Links { get; } = new();
    public bool Available { get; set; } = true;

    public Task<bool> SendLinkAsync(string email, string route, string userId, string token, CancellationToken cancellation)
    {
        if (Available) Links[(email, route)] = (userId, token);
        return Task.FromResult(Available);
    }

    public static TestAccountEmail From(WebApplicationFactory<Program> app) =>
        (TestAccountEmail)app.Services.GetRequiredService<IAccountEmail>();

    public static Dictionary<string, string?> Settings() => new()
    {
        ["AccountEmail:PublicOrigin"] = "https://localhost",
        ["AccountEmail:From"] = "neavito@example.com",
        ["AccountEmail:Host"] = "unused"
    };

    public static async Task Confirm(WebApplicationFactory<Program> app, HttpClient client, string email)
    {
        var (userId, token) = From(app).Links[(email, "confirm-email")];
        using var response = await client.PostAsJsonAsync("/api/auth/confirm-email", new { userId, token });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}


