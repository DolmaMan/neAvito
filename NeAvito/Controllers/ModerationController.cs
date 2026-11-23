using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NeAvito.Data;
using NeAvito.Models;

namespace NeAvito.Controllers;

[ApiController, Authorize(Roles = ModerationRoles.Moderator), Route("api/moderation")]
[RequestSizeLimit(16_384)]
public sealed class ModerationController(AppDbContext db) : ControllerBase
{
    private string Actor => User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    private ConflictObjectResult Changed() => Conflict(new { message = "Данные изменились. Обновите обращение перед повторным действием." });
    private static object Summary(ListingReport x) => new { x.Id, x.ListingId, Title = x.Listing.Title,
        Reason = x.Reason.ToString(), Status = x.Status.ToString(), x.CreatedAtUtc, x.Version,
        Decision = x.Decision.ToString(), x.DecisionReason, x.ResolvedAtUtc, x.ModeratorId };

    [HttpGet("reports")]
    public async Task<IActionResult> Reports([FromQuery] ModerationQuery query, CancellationToken ct)
    {
        if (query.Status is not (null or "Pending" or "Resolved")) return BadRequest(new { message = "Неизвестный статус обращения." });
        var reports = db.ListingReports.AsNoTracking().Include(x => x.Listing).AsQueryable();
        if (query.Status is not null) { var status = Enum.Parse<ReportStatus>(query.Status); reports = reports.Where(x => x.Status == status); }
        var total = await reports.CountAsync(ct);
        var rows = await reports.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync(ct);
        return Ok(new { Items = rows.Select(Summary), query.Page, query.PageSize, TotalCount = total, TotalPages = (int)Math.Ceiling(total / (double)query.PageSize) });
    }

    [HttpGet("reports/{id:guid}")]
    public async Task<IActionResult> Detail(Guid id, CancellationToken ct)
    {
        var report = await db.ListingReports.AsNoTracking().Include(x => x.Listing).ThenInclude(x => x.Images).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (report is null) return NotFound();
        var author = await db.Users.AsNoTracking().SingleAsync(x => x.Id == report.Listing.AuthorId, ct);
        return Ok(new { Report = Summary(report), report.Comment,
            Listing = new { report.Listing.Id, report.Listing.Title, report.Listing.Description, report.Listing.Version,
                Status = report.Listing.Status.ToString(), report.Listing.ModerationHidden, report.Listing.ModerationReason,
                Images = report.Listing.Images.OrderBy(x => x.Position).Select(x => new { x.Id, Url = $"/api/moderation/images/{x.Id}" }) },
            Author = new { author.Id, author.DisplayName, author.IsBlocked, author.BlockReason, author.BlockedAtUtc, author.BlockedById, Version = author.ConcurrencyStamp } });
    }

    [HttpGet("reports/{id:guid}/audit")]
    public async Task<IActionResult> Audit(Guid id, [FromQuery] ModerationQuery query, CancellationToken ct)
    {
        var report = await db.ListingReports.AsNoTracking().Include(x => x.Listing).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (report is null) return NotFound();
        var rows = db.ModerationAudits.AsNoTracking().Where(x => x.ListingId == report.ListingId || x.AuthorId == report.Listing.AuthorId &&
            (x.Action == ModerationAction.BlockAuthor || x.Action == ModerationAction.UnblockAuthor));
        var total = await rows.CountAsync(ct);
        var items = await rows.OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.Id).Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .Select(x => new { x.Id, x.ReportId, x.ListingId, x.AuthorId, x.ModeratorId, Action = x.Action.ToString(), x.Reason, x.CreatedAtUtc }).ToListAsync(ct);
        return Ok(new { Items = items, query.Page, query.PageSize, TotalCount = total, TotalPages = (int)Math.Ceiling(total / (double)query.PageSize) });
    }

    [HttpPost("reports/{id:guid}/decision")]
    public async Task<IActionResult> Decide(Guid id, DecisionRequest request, CancellationToken ct)
    {
        if (request.Action is not ("Reject" or "HideListing" or "BlockAuthor") || string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest(new { message = "Выберите решение и укажите его причину." });
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var report = await db.ListingReports.Include(x => x.Listing).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (report is null) return NotFound();
        if (report.Version != request.Version || report.Status != ReportStatus.Pending) return Changed();
        var listing = report.Listing;
        var author = await db.Users.SingleAsync(x => x.Id == listing.AuthorId, ct);
        if (listing.AuthorId == Actor || report.ReporterId == Actor)
            return Conflict(new { message = "Собственное объявление или обращение должен рассмотреть другой модератор." });
        if (listing.Version != request.ListingVersion || author.ConcurrencyStamp != request.AuthorVersion) return Changed();
        var action = Enum.Parse<ModerationAction>(request.Action);
        if (action == ModerationAction.BlockAuthor)
        {
            if (await IsModerator(author.Id, ct)) return Conflict(new { message = "Нельзя блокировать модератора через жалобу. Сначала администратор должен отозвать его роль." });
            if (author.IsBlocked) return Changed();
            author.IsBlocked = true; author.BlockReason = request.Reason.Trim(); author.BlockedById = Actor; author.BlockedAtUtc = DateTime.UtcNow;
            author.SecurityStamp = Guid.NewGuid().ToString();
            await db.AccountSessions.Where(x => x.UserId == author.Id).ExecuteDeleteAsync(ct);
        }
        if (action is ModerationAction.HideListing or ModerationAction.BlockAuthor)
        {
            listing.ModerationHidden = true; listing.ModerationReason = request.Reason.Trim();
        }
        // Touch both aggregates even for rejection: a concurrent editor or moderator must win only once.
        listing.Version = Guid.NewGuid(); listing.UpdatedAtUtc = DateTime.UtcNow;
        author.ConcurrencyStamp = Guid.NewGuid().ToString();
        report.Status = ReportStatus.Resolved; report.Decision = action; report.DecisionReason = request.Reason.Trim();
        report.ModeratorId = Actor; report.ResolvedAtUtc = DateTime.UtcNow; report.Version = Guid.NewGuid();
        AddAudit(report, action, request.Reason);
        try { await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); }
        catch (DbUpdateConcurrencyException) { return Changed(); }
        return Ok(Summary(report));
    }

    [HttpPost("reports/{id:guid}/release")]
    public async Task<IActionResult> Release(Guid id, ReleaseRequest request, CancellationToken ct)
    {
        if (request.Action is not ("RestoreListing" or "UnblockAuthor") || string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest(new { message = "Выберите ограничение и укажите причину его снятия." });
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var report = await db.ListingReports.Include(x => x.Listing).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (report is null) return NotFound();
        var listing = report.Listing;
        var author = await db.Users.SingleAsync(x => x.Id == listing.AuthorId, ct);
        if (listing.AuthorId == Actor || report.ReporterId == Actor) return Conflict(new { message = "Снятие ограничения должен выполнить другой модератор." });
        if (listing.Version != request.ListingVersion || author.ConcurrencyStamp != request.AuthorVersion) return Changed();
        var action = Enum.Parse<ModerationAction>(request.Action);
        if (action == ModerationAction.RestoreListing)
        {
            if (!listing.ModerationHidden || author.IsBlocked) return Conflict(new { message = "Сначала снимите блокировку автора; объявление должно быть скрыто модератором." });
            listing.ModerationHidden = false; listing.ModerationReason = null;
            // Restoring permission does not publish stale content automatically.
            if (listing.Status == ListingStatus.Published) listing.Status = ListingStatus.Unpublished;
        }
        else
        {
            if (!author.IsBlocked) return Changed();
            author.IsBlocked = false; author.BlockReason = null; author.BlockedById = null; author.BlockedAtUtc = null;
            author.SecurityStamp = Guid.NewGuid().ToString();
            await db.AccountSessions.Where(x => x.UserId == author.Id).ExecuteDeleteAsync(ct);
        }
        listing.Version = Guid.NewGuid(); listing.UpdatedAtUtc = DateTime.UtcNow;
        author.ConcurrencyStamp = Guid.NewGuid().ToString();
        AddAudit(report, action, request.Reason);
        try { await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); }
        catch (DbUpdateConcurrencyException) { return Changed(); }
        return Ok(new { message = "Ограничение снято. История решения сохранена." });
    }

    private Task<bool> IsModerator(string id, CancellationToken ct) => db.UserRoles.AnyAsync(x => x.UserId == id && db.Roles.Any(r => r.Id == x.RoleId && r.Name == ModerationRoles.Moderator), ct);
    private void AddAudit(ListingReport report, ModerationAction action, string reason) => db.ModerationAudits.Add(new()
    { ReportId = report.Id, ListingId = report.ListingId, AuthorId = report.Listing.AuthorId, ModeratorId = Actor, Action = action, Reason = reason.Trim() });
}

public sealed class ModerationQuery
{
    [Range(1, 1000000)] public int Page { get; set; } = 1;
    [Range(1, 100)] public int PageSize { get; set; } = 20;
    [MaxLength(20)] public string? Status { get; set; }
}
public class ReleaseRequest
{
    [Required, MaxLength(40)] public string Action { get; set; } = "";
    [Required, MaxLength(1000)] public string Reason { get; set; } = "";
    public Guid ListingVersion { get; set; }
    [Required, MaxLength(100)] public string AuthorVersion { get; set; } = "";
}
public sealed class DecisionRequest : ReleaseRequest { public Guid Version { get; set; } }
