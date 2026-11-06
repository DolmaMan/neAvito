namespace NeAvito.Models;

public enum OrderStatus { New, Confirmed, Completed, Cancelled }

public sealed class CheckoutBatch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string BuyerId { get; set; } = string.Empty;
    public AppUser Buyer { get; set; } = null!;
    public Guid RequestId { get; set; }
    public string Fingerprint { get; set; } = string.Empty;
    public List<Order> Orders { get; set; } = [];
}

public sealed class Order
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Number { get; set; } = "NA-" + Guid.NewGuid().ToString("N").ToUpperInvariant();
    public Guid CheckoutBatchId { get; set; }
    public CheckoutBatch CheckoutBatch { get; set; } = null!;
    public string BuyerId { get; set; } = string.Empty;
    public AppUser Buyer { get; set; } = null!;
    public string SellerId { get; set; } = string.Empty;
    public AppUser Seller { get; set; } = null!;
    public string RecipientName { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string DeliveryAddress { get; set; } = string.Empty;
    public OrderStatus Status { get; set; } = OrderStatus.New;
    public Guid Version { get; set; } = Guid.NewGuid();
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime StatusUpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<OrderItem> Items { get; set; } = [];
}

public sealed class OrderItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public Order Order { get; set; } = null!;
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public string ProductName { get; set; } = string.Empty;
    public long UnitPriceMinor { get; set; }
    public int Quantity { get; set; }
}
