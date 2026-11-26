using System.Net.Mail;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NeAvito.Data;
using NeAvito.Models;

namespace NeAvito.Services;

public static class DemoSetup
{
    public static async Task RunAsync(IServiceProvider services, IHostEnvironment environment, IConfiguration config)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (!environment.IsDevelopment() || !config.GetValue<bool>("Demo:Enabled") ||
            db.Database.GetDbConnection().Database != "neavito_demo")
            throw new InvalidOperationException("Demo setup requires Development, Demo:Enabled and database neavito_demo.");
        Validate(config);
        await db.Database.MigrateAsync();
        await SeedAccountsAsync(scope.ServiceProvider, config);
        Console.WriteLine("Demo database ready. Confirm the moderator email before signing in. Existing passwords preserved.");
    }

    public static void Validate(IConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config["Demo:Password"]))
            throw new InvalidOperationException("Demo:Password is required.");
        var email = config["Demo:ModeratorEmail"]?.Trim();
        if (!MailAddress.TryCreate(email, out var address) || address.Address != email ||
            address.Host.Equals("demo.test", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Configure a real Demo:ModeratorEmail address.");
        if (string.IsNullOrWhiteSpace(config["Demo:ModeratorPassword"]) || string.IsNullOrWhiteSpace(config["Demo:ModeratorDisplayName"]))
            throw new InvalidOperationException("Configure Demo:ModeratorPassword and ModeratorDisplayName.");
    }

    public static async Task SeedAccountsAsync(IServiceProvider services, IConfiguration config)
    {
        Validate(config);
        var users = services.GetRequiredService<UserManager<AppUser>>();
        var roles = services.GetRequiredService<RoleManager<IdentityRole>>();
        if (!await roles.RoleExistsAsync(ModerationRoles.Moderator))
            Check(await roles.CreateAsync(new IdentityRole(ModerationRoles.Moderator)));
        // Retire the old shared moderator without deleting its history or account.
        var legacy = await users.FindByEmailAsync("moderator@demo.test");
        if (legacy is not null && await users.IsInRoleAsync(legacy, ModerationRoles.Moderator))
        {
            Check(await users.RemoveFromRoleAsync(legacy, ModerationRoles.Moderator));
            Check(await users.UpdateSecurityStampAsync(legacy));
        }
        foreach (var (email, name) in new[] { ("seller@demo.test", "Продавец"), ("buyer@demo.test", "Покупатель") })
        {
            if (await users.FindByEmailAsync(email) is null)
                Check(await users.CreateAsync(new AppUser { UserName = email, Email = email, DisplayName = name, EmailConfirmed = true }, config["Demo:Password"]!));
        }
        var moderatorEmail = config["Demo:ModeratorEmail"]!.Trim();
        var moderator = await users.FindByEmailAsync(moderatorEmail);
        if (moderator is null)
        {
            moderator = new AppUser { UserName = moderatorEmail, Email = moderatorEmail, DisplayName = config["Demo:ModeratorDisplayName"]!.Trim() };
            Check(await users.CreateAsync(moderator, config["Demo:ModeratorPassword"]!));
        }
        if (moderator.IsBlocked) throw new InvalidOperationException("Configured moderator is blocked; resolve the block before setup.");
        if (!await users.IsInRoleAsync(moderator, ModerationRoles.Moderator))
        {
            Check(await users.AddToRoleAsync(moderator, ModerationRoles.Moderator));
            Check(await users.UpdateSecurityStampAsync(moderator));
        }
        // Never auto-confirm a real address or reset an existing user's password.
        if (!moderator.EmailConfirmed)
        {
            var token = await users.GenerateEmailConfirmationTokenAsync(moderator);
            if (!await services.GetRequiredService<IAccountEmail>().SendLinkAsync(moderatorEmail, "confirm-email", moderator.Id, token, CancellationToken.None))
                throw new InvalidOperationException("Moderator confirmation delivery failed. Check SMTP settings and rerun setup.");
        }
    }

    private static void Check(IdentityResult result)
    {
        if (!result.Succeeded) throw new InvalidOperationException(string.Join("; ", result.Errors.Select(e => e.Description)));
    }
}
