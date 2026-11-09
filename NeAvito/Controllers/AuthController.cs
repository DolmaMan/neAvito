using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using NeAvito.Models;
using NeAvito.Services;
using Npgsql;
namespace NeAvito.Controllers;

[ApiController]
[Route("api/auth")]
[RequestSizeLimit(16_384)]
public sealed class AuthController(UserManager<AppUser> users, SignInManager<AppUser> signIn, IAccountEmail emailSender) : ControllerBase
{
    [HttpPost("register")]
    [EnableRateLimiting("auth-mail")]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken cancellation)
    {
        var email = request.Email.Trim();
        var user = new AppUser { UserName = email, Email = email, DisplayName = request.DisplayName.Trim() };
        if (string.IsNullOrWhiteSpace(user.DisplayName))
            return BadRequest(new { message = "Укажите имя." });
        try
        {
            var result = await users.CreateAsync(user, request.Password);
            if (!result.Succeeded) return IdentityErrors(result);
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "UserNameIndex" or "EmailIndex" })
        {
            return IdentityErrors(IdentityResult.Failed(users.ErrorDescriber.DuplicateEmail(email)));
        }

        var confirmationSent = await SendConfirmation(user, cancellation);
        return StatusCode(StatusCodes.Status201Created, new { user.Id, user.Email, user.DisplayName, confirmationSent });
    }

    [HttpPost("login")]
    [EnableRateLimiting("auth-login")]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        var result = await signIn.PasswordSignInAsync(request.Email.Trim(), request.Password, false, true);
        return result.Succeeded ? Ok(new { message = "Вход выполнен." })
            : Unauthorized(new { message = "Неверные учетные данные или вход временно заблокирован." });
    }

    [HttpPost("resend-confirmation")]
    [EnableRateLimiting("auth-mail")]
    public async Task<IActionResult> ResendConfirmation(EmailRequest request, CancellationToken cancellation)
    {
        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is { EmailConfirmed: false }) await SendConfirmation(user, cancellation);
        return Accepted(new { message = "Если этот адрес ожидает подтверждения, на него будет отправлена ссылка." });
    }

    [HttpPost("confirm-email")]
    [EnableRateLimiting("auth-token")]
    public async Task<IActionResult> ConfirmEmail(AccountTokenRequest request)
    {
        var user = await users.FindByIdAsync(request.UserId);
        if (user is null) return InvalidLink();
        var result = await users.ConfirmEmailAsync(user, request.Token);
        return result.Succeeded ? NoContent() : InvalidLink();
    }

    [HttpPost("forgot-password")]
    [EnableRateLimiting("auth-mail")]
    public async Task<IActionResult> ForgotPassword(EmailRequest request, CancellationToken cancellation)
    {
        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is { EmailConfirmed: true, Email: not null })
        {
            var token = await users.GeneratePasswordResetTokenAsync(user);
            await emailSender.SendLinkAsync(user.Email, "reset-password", user.Id, token, cancellation);
        }
        return Accepted(new { message = "Если для этого адреса доступно восстановление, на него будет отправлена ссылка." });
    }

    [HttpPost("reset-password")]
    [EnableRateLimiting("auth-token")]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest request)
    {
        var user = await users.FindByIdAsync(request.UserId);
        if (user is not { EmailConfirmed: true }) return InvalidLink();
        // Identity saves these changes together with the verified password reset and its new security stamp.
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        var result = await users.ResetPasswordAsync(user, request.Token, request.Password);
        if (result.Succeeded) return NoContent();
        if (result.Errors.Any(error => error.Code == nameof(IdentityErrorDescriber.InvalidToken))) return InvalidLink();
        return IdentityErrors(result);
    }

    [Authorize]
    [HttpPost("change-password")]
    [EnableRateLimiting("auth-login")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request)
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Unauthorized();
        if (request.CurrentPassword == request.NewPassword)
            return BadRequest(new { message = "Новый пароль должен отличаться от текущего." });
        // Identity verifies the current password and changes the security stamp atomically.
        var result = await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded) return IdentityErrors(result);
        await signIn.SignOutAsync();
        return NoContent();
    }

    [Authorize]
    [HttpPost("logout-all")]
    [EnableRateLimiting("auth-login")]
    public async Task<IActionResult> LogoutAll()
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Unauthorized();
        var result = await users.UpdateSecurityStampAsync(user);
        if (!result.Succeeded) return IdentityErrors(result);
        await signIn.SignOutAsync();
        return NoContent();
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await signIn.SignOutAsync();
        return NoContent();
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        var user = await users.GetUserAsync(User);
        return user is null ? Unauthorized() : Ok(new { user.Id, user.Email, user.DisplayName });
    }

    private async Task<bool> SendConfirmation(AppUser user, CancellationToken cancellation)
    {
        var token = await users.GenerateEmailConfirmationTokenAsync(user);
        return await emailSender.SendLinkAsync(user.Email!, "confirm-email", user.Id, token, cancellation);
    }

    private BadRequestObjectResult IdentityErrors(IdentityResult result) =>
        BadRequest(new { errors = result.Errors.Select(error => new { error.Code, error.Description }) });

    private BadRequestObjectResult InvalidLink() =>
        BadRequest(new { message = "Ссылка недействительна или устарела. Запросите новую." });
}

public sealed record RegisterRequest(
    [Required, EmailAddress, MaxLength(256)] string Email,
    [Required, MinLength(8), MaxLength(128)] string Password,
    [Required, MaxLength(100)] string DisplayName);
public sealed record LoginRequest(
    [Required, EmailAddress, MaxLength(256)] string Email,
    [Required, MaxLength(128)] string Password);
public sealed record EmailRequest([Required, EmailAddress, MaxLength(256)] string Email);
public sealed record ChangePasswordRequest(
    [Required, MaxLength(128)] string CurrentPassword,
    [Required, MinLength(8), MaxLength(128)] string NewPassword);
public sealed record AccountTokenRequest(
    [Required, MaxLength(450)] string UserId,
    [Required, MaxLength(4096)] string Token);
public sealed record ResetPasswordRequest(
    [Required, MaxLength(450)] string UserId,
    [Required, MaxLength(4096)] string Token,
    [Required, MinLength(8), MaxLength(128)] string Password);

