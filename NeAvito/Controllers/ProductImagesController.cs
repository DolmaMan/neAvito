using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NeAvito.Data;
using NeAvito.Models;
using NeAvito.Services;
namespace NeAvito.Controllers;

[ApiController]
[Route("api/product-images")]
public sealed class ProductImagesController(AppDbContext db, ProductImageStorage storage) : ControllerBase
{
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var visible = await db.ProductImages.AsNoTracking().AnyAsync(x => x.Id == id &&
            (x.Product.Status == ProductStatus.Published || x.Product.SellerId == userId), cancellationToken);
        var path = storage.PathFor(id);
        if (!visible || !System.IO.File.Exists(path)) return NotFound();
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return PhysicalFile(path, "image/webp");
    }
}
