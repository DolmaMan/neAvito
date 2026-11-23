namespace NeAvito.Models;

public static class ModerationRoles { public const string Moderator = "Moderator"; }
public enum ReportReason { InaccurateDescription, ProhibitedItem, SuspectedFraud, Other }
public enum ReportStatus { Pending, Resolved }
public enum ModerationAction { Reject, HideListing, BlockAuthor, RestoreListing, UnblockAuthor }

public sealed class ListingReport
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ListingId { get; set; }
    public Listing Listing { get; set; } = null!;
    public string ReporterId { get; set; } = "";
    public ReportReason Reason { get; set; }
    public string Comment { get; set; } = "";
    public ReportStatus Status { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAtUtc { get; set; }
    public ModerationAction? Decision { get; set; }
    public string? DecisionReason { get; set; }
    public string? ModeratorId { get; set; }
}

public sealed class ModerationAudit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ReportId { get; set; }
    public Guid ListingId { get; set; }
    public string AuthorId { get; set; } = "";
    public string ModeratorId { get; set; } = "";
    public ModerationAction Action { get; set; }
    public string Reason { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
