namespace NeAvito.Models;

public sealed class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public enum ProductStatus { Draft, Published, Unpublished }

public sealed class Product
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    // Store rubles as integer kopecks to avoid rounding and floating point errors.
    public long PriceMinor { get; set; }
    public int Stock { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
    public int CategoryId { get; set; }
    public Category Category { get; set; } = null!;
    public string SellerId { get; set; } = string.Empty;
    public AppUser Seller { get; set; } = null!;
    public ProductStatus Status { get; set; } = ProductStatus.Draft;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<ProductImage> Images { get; set; } = [];
}

public sealed class ProductImage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public string Url { get; set; } = string.Empty;
    public int Position { get; set; }
}

