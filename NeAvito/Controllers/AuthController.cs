using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NeAvito.Models;
namespace NeAvito.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(UserManager<AppUser> users, SignInManager<AppUser> signIn) : ControllerBase
{
    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest request)
    {
        var email = request.Email.Trim();
        var user = new AppUser { UserName = email, Email = email, DisplayName = request.DisplayName.Trim() };
        if (string.IsNullOrWhiteSpace(user.DisplayName))
            return BadRequest(new { message = "Укажите имя." });
        var result = await users.CreateAsync(user, request.Password);
        if (!result.Succeeded)
            return BadRequest(new { errors = result.Errors.Select(e => new { e.Code, e.Description }) });
        return StatusCode(StatusCodes.Status201Created, new { user.Id, user.Email, user.DisplayName });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        var result = await signIn.PasswordSignInAsync(request.Email.Trim(), request.Password, false, true);
        return result.Succeeded ? Ok(new { message = "Вход выполнен." })
            : Unauthorized(new { message = "Неверные учетные данные или вход временно заблокирован." });
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
}

public sealed record RegisterRequest(
    [Required, EmailAddress, MaxLength(256)] string Email,
    [Required, MinLength(8), MaxLength(128)] string Password,
    [Required, MaxLength(100)] string DisplayName);
public sealed record LoginRequest(
    [Required, EmailAddress, MaxLength(256)] string Email,
    [Required, MaxLength(128)] string Password);

