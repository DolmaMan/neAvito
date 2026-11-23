using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using NeAvito.Data;
using NeAvito.Models;

namespace NeAvito.Controllers;

[ApiController, Authorize, Route("api/listings/{id:guid}/reports")]
[RequestSizeLimit(16_384)]
public sealed class ReportsController(AppDbContext db) : ControllerBase
{
    [HttpPost, EnableRateLimiting("reports-account")]
    public async Task<IActionResult> Create(Guid id, ReportRequest request, CancellationToken ct)
    {
        if (!Enum.GetNames<ReportReason>().Contains(request.Reason) ||
            request.Reason == "Other" && string.IsNullOrWhiteSpace(request.Comment))
            return BadRequest(new { message = "Выберите причину; для «Другое» добавьте пояснение." });
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var previous = await db.ListingReports.AsNoTracking().SingleOrDefaultAsync(x => x.ListingId == id && x.ReporterId == userId && x.Status == ReportStatus.Pending, ct);
        if (previous is not null) return Ok(Receipt(previous));
        var listing = await db.Listings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.Status == ListingStatus.Published && !x.ModerationHidden &&
            db.Users.Any(u => u.Id == x.AuthorId && u.EmailConfirmed && !u.IsBlocked), ct);
        if (listing is null) return NotFound(new { message = "Объявление недоступно." });
        if (listing.AuthorId == userId) return BadRequest(new { message = "Нельзя пожаловаться на собственное объявление." });
        var report = new ListingReport { ListingId = id, ReporterId = userId, Reason = Enum.Parse<ReportReason>(request.Reason), Comment = request.Comment?.Trim() ?? "" };
        db.ListingReports.Add(report);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            // A partial unique index also protects simultaneous duplicate submissions.
            db.ChangeTracker.Clear();
            previous = await db.ListingReports.AsNoTracking().SingleOrDefaultAsync(x => x.ListingId == id && x.ReporterId == userId && x.Status == ReportStatus.Pending, ct);
            if (previous is null) throw;
            return Ok(Receipt(previous));
        }
        return StatusCode(StatusCodes.Status201Created, Receipt(report));
    }
    private static object Receipt(ListingReport x) => new { x.Id, x.ListingId, Status = x.Status.ToString(), x.CreatedAtUtc };
}
public sealed class ReportRequest
{
    [Required, MaxLength(40)] public string Reason { get; set; } = "";
    [MaxLength(2000)] public string? Comment { get; set; }
}
