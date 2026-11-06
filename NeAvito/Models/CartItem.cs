namespace NeAvito.Models;

public sealed class CartItem
{
    public string UserId { get; set; } = string.Empty;
    public AppUser User { get; set; } = null!;
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public int Quantity { get; set; }
    public long PriceAtAdditionMinor { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
    public DateTime AddedAtUtc { get; set; } = DateTime.UtcNow;
}
