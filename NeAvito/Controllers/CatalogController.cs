using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NeAvito.Data;
using NeAvito.Models;

namespace NeAvito.Controllers;

[ApiController]
[Route("api/catalog")]
public sealed class CatalogController(AppDbContext db) : ControllerBase
{
    [HttpGet("categories")]
    public async Task<IActionResult> Categories(CancellationToken cancellationToken) => Ok(
        await db.Categories.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.Name }).ToListAsync(cancellationToken));

    [HttpGet("products")]
    public async Task<IActionResult> Products([FromQuery] CatalogQuery request, CancellationToken cancellationToken)
    {
        if (request.MinPrice > request.MaxPrice)
            return BadRequest(new { message = "Минимальная цена не может быть больше максимальной." });
        var sort = request.Sort.Trim().ToLowerInvariant();
        if (sort is not ("newest" or "oldest" or "price_asc" or "price_desc"))
            return BadRequest(new { message = "Допустимая сортировка: newest, oldest, price_asc, price_desc." });

        var query = db.Products.AsNoTracking().Where(x => x.Status == ProductStatus.Published);
        var search = request.Search?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(search))
            query = query.Where(x => x.Name.ToLower().Contains(search));
        if (request.CategoryId.HasValue) query = query.Where(x => x.CategoryId == request.CategoryId.Value);
        if (request.MinPrice.HasValue)
        {
            var minimum = (long)decimal.Ceiling(request.MinPrice.Value * 100);
            query = query.Where(x => x.PriceMinor >= minimum);
        }
        if (request.MaxPrice.HasValue)
        {
            var maximum = (long)decimal.Floor(request.MaxPrice.Value * 100);
            query = query.Where(x => x.PriceMinor <= maximum);
        }
        var total = await query.CountAsync(cancellationToken);
        var ordered = sort switch
        {
            "oldest" => query.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id),
            "price_asc" => query.OrderBy(x => x.PriceMinor).ThenBy(x => x.Id),
            "price_desc" => query.OrderByDescending(x => x.PriceMinor).ThenBy(x => x.Id),
            _ => query.OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.Id)
        };
        var rows = await ordered.Skip((request.Page - 1) * request.PageSize).Take(request.PageSize)
            .Select(x => new
            {
                x.Id, x.Name, x.PriceMinor, x.Stock, x.CategoryId,
                CategoryName = x.Category.Name,
                SellerId = x.Seller.Id, SellerName = x.Seller.DisplayName,
                ImageUrl = x.Images.OrderBy(i => i.Position).Select(i => i.Url).FirstOrDefault(),
                x.CreatedAtUtc
            }).ToListAsync(cancellationToken);
        return Ok(new
        {
            request.Page, request.PageSize, TotalCount = total,
            TotalPages = (int)Math.Ceiling((double)total / request.PageSize),
            Items = rows.Select(x => new
            {
                x.Id, x.Name, Price = x.PriceMinor / 100m, Currency = "RUB", x.Stock,
                IsAvailable = x.Stock > 0, x.CategoryId, x.CategoryName,
                Seller = new { Id = x.SellerId, DisplayName = x.SellerName }, x.ImageUrl, x.CreatedAtUtc
            })
        });
    }

    [HttpGet("products/{id:guid}")]
    public async Task<IActionResult> Product(Guid id, CancellationToken cancellationToken)
    {
        var product = await db.Products.AsNoTracking()
            .Include(x => x.Category).Include(x => x.Seller).Include(x => x.Images)
            .SingleOrDefaultAsync(x => x.Id == id && x.Status == ProductStatus.Published, cancellationToken);
        if (product is null) return NotFound(new { message = "Товар не найден." });
        return Ok(new
        {
            product.Id, product.Name, product.Description, Price = product.PriceMinor / 100m,
            Currency = "RUB", product.Stock, IsAvailable = product.Stock > 0,
            Category = new { product.Category.Id, product.Category.Name },
            Seller = new { product.Seller.Id, product.Seller.DisplayName },
            Status = product.Status.ToString(), product.CreatedAtUtc,
            Images = product.Images.OrderBy(x => x.Position).Select(x => new { x.Id, x.Url, x.Position })
        });
    }
}

public sealed class CatalogQuery
{
    [MaxLength(200)] public string? Search { get; set; }
    [Range(1, int.MaxValue)] public int? CategoryId { get; set; }
    [Range(typeof(decimal), "0", "1000000000")] public decimal? MinPrice { get; set; }
    [Range(typeof(decimal), "0", "1000000000")] public decimal? MaxPrice { get; set; }
    [Range(1, 1000000)] public int Page { get; set; } = 1;
    [Range(1, 100)] public int PageSize { get; set; } = 20;
    [Required, MaxLength(20)] public string Sort { get; set; } = "newest";
}
