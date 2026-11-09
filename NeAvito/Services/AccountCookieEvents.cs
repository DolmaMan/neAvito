using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NeAvito.Data;
using NeAvito.Models;

namespace NeAvito.Services;

internal sealed class AccountCookieEvents(AppDbContext db, SignInManager<AppUser> signIn) : CookieAuthenticationEvents
{
    private const string SessionProperty = "neavito-session";
    private Guid? validatedSession;

    public override async Task SigningIn(CookieSigningInContext context)
    {
        var cancellation = context.HttpContext.RequestAborted;
        var now = context.Options.TimeProvider!.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellation);
        await db.AccountSessions.Where(session => session.ExpiresAtUtc <= now.UtcDateTime
            || session.Id == validatedSession).ExecuteDeleteAsync(cancellation);

        var session = new AccountSession
        {
            Id = Guid.NewGuid(),
            UserId = signIn.UserManager.GetUserId(context.Principal!)!,
            ExpiresAtUtc = context.Properties.ExpiresUtc!.Value.UtcDateTime
        };
        db.AccountSessions.Add(session);
        await db.SaveChangesAsync(cancellation);
        await transaction.CommitAsync(cancellation);
        context.Properties.Items[SessionProperty] = session.Id.ToString("N");
    }

    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var userId = context.Principal is null ? null : signIn.UserManager.GetUserId(context.Principal);
        var now = context.Options.TimeProvider!.GetUtcNow().UtcDateTime;
        if (userId is not null && context.Properties.Items.TryGetValue(SessionProperty, out var value)
            && Guid.TryParseExact(value, "N", out var sessionId)
            && await db.AccountSessions.AnyAsync(session => session.Id == sessionId && session.UserId == userId
                && session.ExpiresAtUtc > now, context.HttpContext.RequestAborted)
            && await signIn.ValidateSecurityStampAsync(context.Principal) is { EmailConfirmed: true } user)
        {
            validatedSession = sessionId;
            context.ReplacePrincipal(await signIn.CreateUserPrincipalAsync(user));
            return;
        }

        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
    }

    public override async Task SigningOut(CookieSigningOutContext context)
    {
        if (validatedSession is Guid sessionId)
            await db.AccountSessions.Where(session => session.Id == sessionId)
                .ExecuteDeleteAsync(context.HttpContext.RequestAborted);
    }

    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}
