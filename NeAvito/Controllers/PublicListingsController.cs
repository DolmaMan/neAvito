using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using NeAvito.Data;
using NeAvito.Models;

namespace NeAvito.Controllers;

[ApiController, Route("api/listings")]
public sealed class PublicListingsController(AppDbContext db, ILogger<PublicListingsController> logger) : ControllerBase
{
    private IQueryable<Listing> Visible() => db.Listings.AsNoTracking().Where(x =>
        x.Status == ListingStatus.Published && !x.ModerationHidden &&
        db.Users.Any(u => u.Id == x.AuthorId && !u.IsBlocked && u.EmailConfirmed));

    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] PublicListingQuery request, CancellationToken ct)
    {
        if (request.MinPrice > request.MaxPrice) return BadRequest(new { message = "Цена от превышает цену до." });
        if (request.Sort is not ("newest" or "oldest" or "price_asc" or "price_desc"))
            return BadRequest(new { message = "Неизвестная сортировка." });
        if (request.Condition is not (null or "New" or "Used"))
            return BadRequest(new { message = "Неизвестное состояние вещи." });
        var query = Visible();
        var search = request.Search?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(search)) query = query.Where(x => x.Title.ToLower().Contains(search) || x.Description.ToLower().Contains(search));
        if (request.CategoryId.HasValue) query = query.Where(x => x.CategoryId == request.CategoryId);
        if (request.CityId.HasValue) query = query.Where(x => x.CityId == request.CityId);
        if (request.Condition is not null) { var condition = Enum.Parse<ItemCondition>(request.Condition); query = query.Where(x => x.Condition == condition); }
        if (request.MinPrice.HasValue) { var min = (long)decimal.Ceiling(request.MinPrice.Value * 100); query = query.Where(x => x.PriceMinor >= min); }
        if (request.MaxPrice.HasValue) { var max = (long)decimal.Floor(request.MaxPrice.Value * 100); query = query.Where(x => x.PriceMinor <= max); }
        var count = await query.CountAsync(ct);
        var ordered = request.Sort switch
        {
            "oldest" => query.OrderBy(x => x.PublishedAtUtc).ThenBy(x => x.Id),
            "price_asc" => query.OrderBy(x => x.PriceMinor).ThenBy(x => x.Id),
            "price_desc" => query.OrderByDescending(x => x.PriceMinor).ThenBy(x => x.Id),
            _ => query.OrderByDescending(x => x.PublishedAtUtc).ThenBy(x => x.Id)
        };
        var rows = await ordered.Skip((request.Page - 1) * request.PageSize).Take(request.PageSize)
            .Select(x => new { x.Id, x.Title, x.PriceMinor, x.CategoryId, x.CityId, x.Condition, x.PublishedAtUtc,
                CategoryName = db.Categories.Where(c => c.Id == x.CategoryId).Select(c => c.Name).FirstOrDefault(),
                CityName = db.Cities.Where(c => c.Id == x.CityId).Select(c => c.Name).FirstOrDefault(),
                SellerName = db.Users.Where(u => u.Id == x.AuthorId).Select(u => u.DisplayName).FirstOrDefault(),
                ImageId = x.Images.OrderBy(i => i.Position).Select(i => (Guid?)i.Id).FirstOrDefault() }).ToListAsync(ct);
        return Ok(new { request.Page, request.PageSize, TotalCount = count, TotalPages = (int)Math.Ceiling(count / (double)request.PageSize),
            Items = rows.Select(x => new { x.Id, x.Title, Price = x.PriceMinor / 100m, Currency = "RUB", x.CategoryId, x.CategoryName,
                x.CityId, x.CityName, Condition = x.Condition.ToString(), x.SellerName, x.PublishedAtUtc,
                ImageUrl = x.ImageId.HasValue ? $"/api/listing-images/{x.ImageId}" : null }) });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Detail(Guid id, CancellationToken ct)
    {
        var row = await Visible().Where(x => x.Id == id).Select(x => new {
            x.Id, x.Title, x.Description, x.PriceMinor, x.Condition, x.PublishedAtUtc,
            Category = db.Categories.Where(c => c.Id == x.CategoryId).Select(c => new { c.Id, c.Name }).FirstOrDefault(),
            City = db.Cities.Where(c => c.Id == x.CityId).Select(c => new { c.Id, c.Name }).FirstOrDefault(),
            Seller = db.Users.Where(u => u.Id == x.AuthorId).Select(u => new { u.Id, u.DisplayName }).FirstOrDefault(),
            Images = x.Images.OrderBy(i => i.Position).Select(i => new { i.Id, i.Position }).ToList()
        }).SingleOrDefaultAsync(ct);
        if (row is null) return NotFound(new { message = "Объявление недоступно." });
        return Ok(new { row.Id, row.Title, row.Description, Price = row.PriceMinor / 100m, Currency = "RUB", Condition = row.Condition.ToString(),
            row.PublishedAtUtc, row.Category, row.City, row.Seller, Images = row.Images.Select(i => new { i.Id, i.Position, Url = $"/api/listing-images/{i.Id}" }) });
    }

    [Authorize, HttpGet("{id:guid}/contact"), EnableRateLimiting("contact-account")]
    public async Task<IActionResult> Contact(Guid id, CancellationToken ct)
    {
        var phone = await Visible().Where(x => x.Id == id && x.Contact.ConsentedAtUtc != null && x.Contact.Phone != "")
            .Select(x => x.Contact.Phone).SingleOrDefaultAsync(ct);
        logger.LogInformation("Listing contact {ListingId}, viewer {ViewerId}, available {Available}", id, User.FindFirstValue(ClaimTypes.NameIdentifier), phone is not null);
        Response.Headers.CacheControl = "no-store";
        return phone is null ? NotFound(new { message = "Контакт недоступен." }) : Ok(new { phone });
    }
}

public sealed class PublicListingQuery
{
    [MaxLength(200)] public string? Search { get; set; }
    [Range(1, int.MaxValue)] public int? CategoryId { get; set; }
    [Range(1, int.MaxValue)] public int? CityId { get; set; }
    [MaxLength(20)] public string? Condition { get; set; }
    [Range(typeof(decimal), "0", "1000000000")] public decimal? MinPrice { get; set; }
    [Range(typeof(decimal), "0", "1000000000")] public decimal? MaxPrice { get; set; }
    [Range(1, 1000000)] public int Page { get; set; } = 1;
    [Range(1, 100)] public int PageSize { get; set; } = 12;
    [Required, MaxLength(20)] public string Sort { get; set; } = "newest";
}
