using Microsoft.AspNetCore.Identity;
namespace NeAvito.Models;
public sealed class AppUser : IdentityUser
{
    public bool IsBlocked { get; set; }
    public string DeliveryAddress { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

