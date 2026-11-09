namespace NeAvito.Models;

public enum ListingStatus { Draft, Published, Unpublished, Sold, Archived }
public enum ItemCondition { New, Used }

public sealed class City
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class Listing
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string AuthorId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public long? PriceMinor { get; set; }
    public int? CategoryId { get; set; }
    public int? CityId { get; set; }
    public ItemCondition? Condition { get; set; }
    public ListingStatus Status { get; set; }
    public bool ModerationHidden { get; set; }
    public bool LegacyImported { get; set; }
    public string? TransitionNote { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? PublishedAtUtc { get; set; }
    public DateTime? SoldAtUtc { get; set; }
    public DateTime? ArchivedAtUtc { get; set; }
    public ListingContact Contact { get; set; } = new();
    public List<ListingImage> Images { get; set; } = [];
}

public sealed class ListingContact
{
    public Guid ListingId { get; set; }
    public string Phone { get; set; } = "";
    public DateTime? ConsentedAtUtc { get; set; }
}

public sealed class ListingImage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ListingId { get; set; }
    public Listing Listing { get; set; } = null!;
    public int Position { get; set; }
}
