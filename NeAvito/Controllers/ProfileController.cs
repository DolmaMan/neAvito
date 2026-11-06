using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NeAvito.Models;
namespace NeAvito.Controllers;

[Authorize]
[ApiController]
[Route("api/profile")]
public sealed class ProfileController(UserManager<AppUser> users) : ControllerBase
{
    private static object View(AppUser user) => new
    { user.Id, user.Email, user.DisplayName, Phone = user.PhoneNumber ?? "", user.DeliveryAddress, Version = user.ConcurrencyStamp };

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var user = await users.GetUserAsync(User);
        return user is null ? Unauthorized() : Ok(View(user));
    }

    [HttpPut]
    public async Task<IActionResult> Update(ProfileRequest request)
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Unauthorized();
        if (user.ConcurrencyStamp != request.Version)
            return Conflict(new { message = "Профиль изменился. Обновите страницу перед повторным сохранением." });
        if (string.IsNullOrWhiteSpace(request.DisplayName)) return BadRequest(new { message = "Укажите имя." });
        user.DisplayName = request.DisplayName.Trim();
        user.PhoneNumber = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim();
        user.DeliveryAddress = request.DeliveryAddress?.Trim() ?? "";
        var result = await users.UpdateAsync(user);
        if (!result.Succeeded)
            return Conflict(new { message = "Не удалось сохранить профиль. Обновите страницу и повторите действие." });
        return Ok(View(user));
    }
}
public sealed class ProfileRequest
{
    [Required, MaxLength(100)] public string DisplayName { get; set; } = "";
    [Phone, MaxLength(30)] public string? Phone { get; set; }
    [MaxLength(500)] public string? DeliveryAddress { get; set; }
    [Required, MaxLength(100)] public string Version { get; set; } = "";
}
