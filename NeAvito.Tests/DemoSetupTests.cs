using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NeAvito.Models;
using NeAvito.Services;

namespace NeAvito.Tests;

public class DemoSetupTests
{
    private static IConfiguration Config(string moderatorPassword = "PersonalPassword123!") => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Demo:Password"] = "DemoPassword123!", ["Demo:ModeratorEmail"] = "personal@example.com",
            ["Demo:ModeratorPassword"] = moderatorPassword, ["Demo:ModeratorDisplayName"] = "Модератор"
        }).Build();

    [Fact]
    public async Task PersonalModeratorRequiresConfirmationAndRerunPreservesPassword()
    {
        using var app = new AuthFactory(); using var client = app.Client();
        using (var scope = app.Services.CreateScope()) await DemoSetup.SeedAccountsAsync(scope.ServiceProvider, Config());
        var mail = TestAccountEmail.From(app);
        Assert.Single(mail.Links);
        Assert.True(mail.Links.ContainsKey(("personal@example.com", "confirm-email")));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", new { email = "personal@example.com", password = "PersonalPassword123!" })).StatusCode);
        await TestAccountEmail.Confirm(app, client, "personal@example.com");
        using (var scope = app.Services.CreateScope())
        {
            await DemoSetup.SeedAccountsAsync(scope.ServiceProvider, Config("ChangedConfig123!"));
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var moderator = (await users.FindByEmailAsync("personal@example.com"))!;
            Assert.True(await users.CheckPasswordAsync(moderator, "PersonalPassword123!"));
            Assert.False(await users.CheckPasswordAsync(moderator, "ChangedConfig123!"));
            Assert.True(await users.IsInRoleAsync(moderator, ModerationRoles.Moderator));
            Assert.Null(await users.FindByEmailAsync("newcomer@demo.test"));
            foreach (var email in new[] { "seller@demo.test", "buyer@demo.test" })
            {
                var user = (await users.FindByEmailAsync(email))!;
                Assert.True(user.EmailConfirmed);
                Assert.False(await users.IsInRoleAsync(user, ModerationRoles.Moderator));
            }
        }
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login", new { email = "personal@example.com", password = "PersonalPassword123!" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/moderation/reports")).StatusCode);
    }

    [Fact]
    public async Task UpgradeRevokesSharedModeratorAndMailFailureCanBeRetried()
    {
        using var app = new AuthFactory(); using var client = app.Client();
        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        Assert.True((await roles.CreateAsync(new IdentityRole(ModerationRoles.Moderator))).Succeeded);
        var legacy = new AppUser { UserName = "moderator@demo.test", Email = "moderator@demo.test", EmailConfirmed = true };
        Assert.True((await users.CreateAsync(legacy, "DemoPassword123!")).Succeeded);
        Assert.True((await users.AddToRoleAsync(legacy, ModerationRoles.Moderator)).Succeeded);
        var stamp = legacy.SecurityStamp;
        var mail = TestAccountEmail.From(app); mail.Available = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => DemoSetup.SeedAccountsAsync(scope.ServiceProvider, Config()));
        Assert.False(await users.IsInRoleAsync(legacy, ModerationRoles.Moderator));
        Assert.NotEqual(stamp, legacy.SecurityStamp);
        Assert.False((await users.FindByEmailAsync("personal@example.com"))!.EmailConfirmed);
        mail.Available = true;
        await DemoSetup.SeedAccountsAsync(scope.ServiceProvider, Config());
        Assert.Single(mail.Links);
    }
}
