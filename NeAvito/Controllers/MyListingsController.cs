using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NeAvito.Data;
using NeAvito.Models;
using NeAvito.Services;

namespace NeAvito.Controllers;

[Authorize, ApiController, Route("api/me/listings")]
public sealed class MyListingsController(AppDbContext db, ProductImageStorage storage) : ControllerBase
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    private IQueryable<Listing> Owned => db.Listings.Include(x => x.Contact).Include(x => x.Images).Where(x => x.AuthorId == UserId);
    private static string[] Missing(Listing x)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(x.Title)) errors.Add("Укажите заголовок.");
        if (string.IsNullOrWhiteSpace(x.Description)) errors.Add("Укажите описание.");
        if (x.PriceMinor is null) errors.Add("Укажите цену (0 ₽ — бесплатно).");
        if (x.CategoryId is null) errors.Add("Выберите категорию.");
        if (x.CityId is null) errors.Add("Выберите город.");
        if (x.Condition is null) errors.Add("Выберите состояние вещи.");
        if (x.Images.Count == 0) errors.Add("Добавьте хотя бы одну фотографию.");
        if (x.Contact.Phone.Length == 0 || x.Contact.ConsentedAtUtc is null) errors.Add("Укажите телефон и разрешите его показ для этого объявления.");
        if (x.ModerationHidden) errors.Add("Объявление скрыто модератором.");
        return errors.ToArray();
    }
    private static object View(Listing x) => new
    {
        x.Id, x.Title, x.Description, Price = x.PriceMinor / 100m, Currency = "RUB", x.CategoryId, x.CityId,
        Condition = x.Condition?.ToString(), Status = x.Status.ToString(), x.Version, x.CreatedAtUtc, x.UpdatedAtUtc,
        x.PublishedAtUtc, x.SoldAtUtc, x.ArchivedAtUtc, x.ModerationHidden,
        Phone = x.Contact.Phone, ContactConsent = x.Contact.ConsentedAtUtc is not null, x.Contact.ConsentedAtUtc,
        PublicationErrors = Missing(x),
        Images = x.Images.OrderBy(i => i.Position).Select(i => new { i.Id, i.Position, Url = $"/api/listing-images/{i.Id}" })
    };
    [HttpGet]
    public async Task<IActionResult> List([FromQuery, Range(1, 1000000)] int page = 1,
        [FromQuery, Range(1, 100)] int pageSize = 12, [FromQuery] string? status = null, CancellationToken cancellationToken = default)
    {
        var query = Owned.AsNoTracking();
        if (status is not null)
        {
            if (!Enum.TryParse<ListingStatus>(status, out var parsed) || !Enum.IsDefined(parsed)) return BadRequest(new { message = "Неизвестный статус." });
            query = query.Where(x => x.Status == parsed);
        }
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return Ok(new { items = items.Select(View), page, pageSize, totalCount = total, totalPages = (int)Math.Ceiling(total / (double)pageSize) });
    }
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var listing = await Owned.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return listing is null ? NotFound() : Ok(View(listing));
    }
    [HttpPost]
    public async Task<IActionResult> Create(ListingDraftRequest request, CancellationToken ct)
    {
        var error = await Validate(request, ct);
        if (error is not null) return BadRequest(new { message = error });
        var listing = new Listing { AuthorId = UserId };
        Apply(listing, request);
        db.Listings.Add(listing);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = listing.Id }, View(listing));
    }
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, ListingUpdateRequest request, CancellationToken ct)
    {
        var listing = await Owned.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (listing is null) return NotFound();
        if (listing.Version != request.Version) return Changed();
        if (listing.Status is ListingStatus.Archived or ListingStatus.Sold) return Conflict(new { message = "Сначала верните проданное объявление в снятые; архив доступен только для просмотра." });
        var error = await Validate(request, ct);
        if (error is not null) return BadRequest(new { message = error });
        Apply(listing, request);
        if (listing.Status == ListingStatus.Published && Missing(listing).Length > 0) listing.Status = ListingStatus.Unpublished;
        return await Save(listing, ct) ? Ok(View(listing)) : Changed();
    }
    [HttpPost("{id:guid}/{command:regex(^(publish|unpublish|sold|reopen|archive)$)}")]
    public async Task<IActionResult> Transition(Guid id, string command, ListingVersionRequest request, CancellationToken ct)
    {
        var listing = await Owned.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (listing is null) return NotFound();
        if (listing.Version != request.Version) return Changed();
        var target = command switch { "publish" => ListingStatus.Published, "sold" => ListingStatus.Sold,
            "archive" => ListingStatus.Archived, _ => ListingStatus.Unpublished };
        if (listing.Status == target) return Ok(View(listing));
        var allowed = command switch
        {
            "publish" => listing.Status is ListingStatus.Draft or ListingStatus.Unpublished,
            "unpublish" or "sold" => listing.Status == ListingStatus.Published,
            "reopen" => listing.Status == ListingStatus.Sold,
            "archive" => listing.Status is ListingStatus.Unpublished or ListingStatus.Sold,
            _ => false
        };
        if (!allowed) return Conflict(new { message = "Этот переход статуса недопустим." });
        if (target == ListingStatus.Published && Missing(listing) is { Length: > 0 } errors)
            return BadRequest(new { message = string.Join(" ", errors), publicationErrors = errors });
        listing.Status = target;
        if (target == ListingStatus.Published) listing.PublishedAtUtc = DateTime.UtcNow;
        if (target == ListingStatus.Sold) listing.SoldAtUtc = DateTime.UtcNow;
        if (command == "reopen") listing.SoldAtUtc = null;
        if (target == ListingStatus.Archived) listing.ArchivedAtUtc = DateTime.UtcNow;
        return await Save(listing, ct) ? Ok(View(listing)) : Changed();
    }
    [HttpPost("{id:guid}/images"), Consumes("multipart/form-data")]
    [RequestSizeLimit(6 * 1024 * 1024), RequestFormLimits(MultipartBodyLengthLimit = 6 * 1024 * 1024)]
    public async Task<IActionResult> Upload(Guid id, [FromForm] IFormFile file, [FromForm] Guid version, CancellationToken ct)
    {
        var listing = await Owned.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (listing is null) return NotFound();
        if (listing.Version != version) return Changed();
        if (listing.Status is ListingStatus.Archived or ListingStatus.Sold) return Conflict(new { message = "Изменение фотографий недоступно в этом статусе." });
        if (listing.Images.Count >= 10) return BadRequest(new { message = "Допустимо не больше 10 фотографий." });
        var image = new ListingImage { Listing = listing, Position = listing.Images.Count == 0 ? 0 : listing.Images.Max(x => x.Position) + 1 };
        try { await storage.SaveAsync(image.Id, file, ct); }
        catch (InvalidImageException error) { return BadRequest(new { message = error.Message }); }
        listing.Images.Add(image);
        db.ListingImages.Add(image);
        try
        {
            if (!await Save(listing, ct)) { storage.Delete(image.Id); return Changed(); }
        }
        catch { storage.Delete(image.Id); throw; }
        return Ok(View(listing));
    }
    [HttpDelete("{id:guid}/images/{imageId:guid}")]
    public async Task<IActionResult> DeleteImage(Guid id, Guid imageId, [FromQuery] Guid version, CancellationToken ct)
    {
        var listing = await Owned.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (listing is null) return NotFound();
        if (listing.Version != version) return Changed();
        if (listing.Status is ListingStatus.Archived or ListingStatus.Sold) return Conflict(new { message = "Изменение фотографий недоступно в этом статусе." });
        var image = listing.Images.SingleOrDefault(x => x.Id == imageId);
        if (image is null) return NotFound();
        listing.Images.Remove(image);
        db.ListingImages.Remove(image);
        if (listing.Images.Count == 0 && listing.Status == ListingStatus.Published) listing.Status = ListingStatus.Unpublished;
        if (!await Save(listing, ct)) return Changed();
        storage.Delete(image.Id);
        return Ok(View(listing));
    }
    private async Task<string?> Validate(ListingDraftRequest request, CancellationToken ct)
    {
        if (request.Price is decimal price && decimal.Round(price, 2) != price) return "Цена должна содержать не больше двух знаков после запятой.";
        if (request.CategoryId is int category && !await db.Categories.AnyAsync(x => x.Id == category, ct)) return "Категория не найдена.";
        if (request.CityId is int city && !await db.Cities.AnyAsync(x => x.Id == city, ct)) return "Город не найден.";
        if (request.Condition is not null && request.Condition is not ("New" or "Used")) return "Состояние: New или Used.";
        var phone = NormalizePhone(request.Phone);
        if (phone.Length > 0 && !Regex.IsMatch(phone, @"^\+?[0-9]{7,15}$")) return "Укажите телефон: от 7 до 15 цифр, можно с + в начале.";
        if (request.ContactConsent && phone.Length == 0) return "Для согласия на показ укажите телефон.";
        return null;
    }
    private static string NormalizePhone(string? phone) => Regex.Replace(phone?.Trim() ?? "", @"[\s()\-]", "");
    private static void Apply(Listing listing, ListingDraftRequest request)
    {
        listing.Title = request.Title?.Trim() ?? "";
        listing.Description = request.Description?.Trim() ?? "";
        listing.PriceMinor = request.Price is decimal price ? (long)(price * 100) : null;
        listing.CategoryId = request.CategoryId; listing.CityId = request.CityId;
        listing.Condition = request.Condition is null ? null : Enum.Parse<ItemCondition>(request.Condition);
        var phone = NormalizePhone(request.Phone);
        if (!request.ContactConsent) listing.Contact.ConsentedAtUtc = null;
        else if (phone != listing.Contact.Phone || listing.Contact.ConsentedAtUtc is null) listing.Contact.ConsentedAtUtc = DateTime.UtcNow;
        listing.Contact.Phone = phone;
    }
    private async Task<bool> Save(Listing listing, CancellationToken ct)
    {
        listing.Version = Guid.NewGuid(); listing.UpdatedAtUtc = DateTime.UtcNow;
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
        catch (DbUpdateException error) when (error.InnerException is Npgsql.PostgresException { SqlState: "23505" }) { return false; }
    }
    private ConflictObjectResult Changed() => Conflict(new { message = "Объявление изменилось. Обновите страницу перед повторным действием." });
}

public class ListingDraftRequest
{
    [MaxLength(200)] public string? Title { get; set; }
    [MaxLength(10000)] public string? Description { get; set; }
    [Range(typeof(decimal), "0", "1000000000")] public decimal? Price { get; set; }
    [Range(1, int.MaxValue)] public int? CategoryId { get; set; }
    [Range(1, int.MaxValue)] public int? CityId { get; set; }
    [MaxLength(20)] public string? Condition { get; set; }
    [MaxLength(30)] public string? Phone { get; set; }
    public bool ContactConsent { get; set; }
}
public sealed class ListingUpdateRequest : ListingDraftRequest { public Guid Version { get; set; } }
public sealed record ListingVersionRequest(Guid Version);

