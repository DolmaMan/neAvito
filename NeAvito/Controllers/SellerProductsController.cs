using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NeAvito.Data;
using NeAvito.Models;
using NeAvito.Services;

namespace NeAvito.Controllers;

[Authorize]
[ApiController]
[Route("api/seller/products")]
public sealed class SellerProductsController(AppDbContext db, ProductImageStorage storage) : ControllerBase
{
    private string SellerId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    private IQueryable<Product> Owned => db.Products.Where(x => x.SellerId == SellerId);
    private static object View(Product p) => new
    {
        p.Id, p.Name, p.Description, Price = p.PriceMinor / 100m, Currency = "RUB", p.Stock,
        p.CategoryId, Status = p.Status.ToString(), p.CreatedAtUtc, p.Version,
        Images = p.Images.OrderBy(i => i.Position).Select(i => new { i.Id, i.Url, i.Position })
    };

    [HttpGet]
    public async Task<IActionResult> List([FromQuery, Range(1, 1000000)] int page = 1,
        [FromQuery, Range(1, 100)] int pageSize = 20, CancellationToken cancellationToken = default)
    {
        var total = await Owned.CountAsync(cancellationToken);
        var items = await Owned.AsNoTracking().Include(x => x.Images).OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return Ok(new { page, pageSize, totalCount = total, totalPages = (int)Math.Ceiling((double)total / pageSize), items = items.Select(View) });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var product = await Owned.AsNoTracking().Include(x => x.Images).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return product is null ? NotFound() : Ok(View(product));
    }

    [HttpPost]
    public async Task<IActionResult> Create(ProductRequest request, CancellationToken cancellationToken)
    {
        var error = await Validate(request, cancellationToken);
        if (error is not null) return BadRequest(new { message = error });
        var product = new Product { SellerId = SellerId };
        Apply(product, request);
        db.Products.Add(product);
        await db.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = product.Id }, View(product));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateProductRequest request, CancellationToken cancellationToken)
    {
        var product = await Owned.Include(x => x.Images).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (product is null) return NotFound();
        if (product.Version != request.Version) return Changed();
        var error = await Validate(request, cancellationToken);
        if (error is not null) return BadRequest(new { message = error });
        Apply(product, request);
        return await Save(product, cancellationToken) ? Ok(View(product)) : Changed();
    }

    [HttpPost("{id:guid}/publish")]
    public async Task<IActionResult> Publish(Guid id, CancellationToken cancellationToken) => await SetStatus(id, ProductStatus.Published, cancellationToken);

    [HttpPost("{id:guid}/unpublish")]
    public async Task<IActionResult> Unpublish(Guid id, CancellationToken cancellationToken) => await SetStatus(id, ProductStatus.Unpublished, cancellationToken);

    private async Task<IActionResult> SetStatus(Guid id, ProductStatus status, CancellationToken cancellationToken)
    {
        var product = await Owned.Include(x => x.Images).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (product is null) return NotFound();
        product.Status = status;
        return await Save(product, cancellationToken) ? Ok(View(product)) : Changed();
    }

    [HttpPost("{id:guid}/images")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(6 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 6 * 1024 * 1024)]
    public async Task<IActionResult> Upload(Guid id, [FromForm] IFormFile file, CancellationToken cancellationToken)
    {
        var product = await Owned.Include(x => x.Images).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (product is null) return NotFound();
        if (product.Images.Count >= 10) return BadRequest(new { message = "Допустимо не больше 10 фотографий." });
        var image = new ProductImage { Product = product, Position = product.Images.Count == 0 ? 0 : product.Images.Max(x => x.Position) + 1 };
        image.Url = $"/api/product-images/{image.Id}";
        try { await storage.SaveAsync(image.Id, file, cancellationToken); }
        catch (InvalidImageException exception) { return BadRequest(new { message = exception.Message }); }
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            // Lock the product version before inserting an image to avoid competing positions.
            if (!await Save(product, cancellationToken)) { storage.Delete(image.Id); return Changed(); }
            db.ProductImages.Add(image);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch { storage.Delete(image.Id); throw; }
        return Created(image.Url, new { image.Id, image.Url, image.Position, product.Version });
    }

    [HttpDelete("{id:guid}/images/{imageId:guid}")]
    public async Task<IActionResult> DeleteImage(Guid id, Guid imageId, CancellationToken cancellationToken)
    {
        var product = await Owned.Include(x => x.Images).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        var image = product?.Images.SingleOrDefault(x => x.Id == imageId);
        if (product is null || image is null) return NotFound();
        db.ProductImages.Remove(image);
        if (!await Save(product, cancellationToken)) return Changed();
        storage.Delete(imageId);
        return NoContent();
    }

    private async Task<bool> Save(Product product, CancellationToken cancellationToken)
    {
        product.Version = Guid.NewGuid();
        try { await db.SaveChangesAsync(cancellationToken); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
    }
    private ConflictObjectResult Changed() => Conflict(new { message = "Товар изменился. Обновите его данные и повторите действие." });
    private async Task<string?> Validate(ProductRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Description))
            return "Укажите название и описание.";
        if (decimal.Round(request.Price, 2) != request.Price) return "Цена должна содержать не больше двух знаков после запятой.";
        return await db.Categories.AnyAsync(x => x.Id == request.CategoryId, cancellationToken) ? null : "Категория не найдена.";
    }
    private static void Apply(Product product, ProductRequest request)
    {
        product.Name = request.Name.Trim(); product.Description = request.Description.Trim();
        product.PriceMinor = (long)(request.Price * 100); product.Stock = request.Stock; product.CategoryId = request.CategoryId;
    }
}

public class ProductRequest
{
    [Required, MaxLength(200)] public string Name { get; set; } = string.Empty;
    [Required, MaxLength(10000)] public string Description { get; set; } = string.Empty;
    [Range(typeof(decimal), "0.01", "1000000000", ParseLimitsInInvariantCulture = true)] public decimal Price { get; set; }
    [Range(0, int.MaxValue)] public int Stock { get; set; }
    [Range(1, int.MaxValue)] public int CategoryId { get; set; }
}
public sealed class UpdateProductRequest : ProductRequest
{
    public Guid Version { get; set; }
}



