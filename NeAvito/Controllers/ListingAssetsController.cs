using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NeAvito.Data;
using NeAvito.Models;
using NeAvito.Services;

namespace NeAvito.Controllers;

[ApiController]
public sealed class ListingAssetsController(AppDbContext db, ProductImageStorage storage) : ControllerBase
{
    [HttpGet("api/listings/cities")]
    public async Task<IActionResult> Cities(CancellationToken ct) => Ok(await db.Cities.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct));
    [HttpGet("api/listings/categories")]
    public async Task<IActionResult> Categories(CancellationToken ct) => Ok(await db.Categories.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct));
    [HttpGet("api/listing-images/{id:guid}")]
    public async Task<IActionResult> Image(Guid id, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var visible = await db.ListingImages.AnyAsync(x => x.Id == id && (x.Listing.AuthorId == userId ||
            x.Listing.Status == ListingStatus.Published && !x.Listing.ModerationHidden), ct);
        var path = storage.PathFor(id);
        if (!visible || !System.IO.File.Exists(path)) return NotFound();
        Response.Headers.CacheControl = "no-store";
        return PhysicalFile(path, "image/webp");
    }
}
