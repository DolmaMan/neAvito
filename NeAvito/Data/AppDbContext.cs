using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using NeAvito.Models;
namespace NeAvito.Data;
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<AppUser>(options)
{
    internal DbSet<AccountSession> AccountSessions => Set<AccountSession>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<CheckoutBatch> CheckoutBatches => Set<CheckoutBatch>();
    public DbSet<CartItem> CartItems => Set<CartItem>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<ProductImage> ProductImages => Set<ProductImage>();
    public DbSet<Listing> Listings => Set<Listing>();
    public DbSet<ListingImage> ListingImages => Set<ListingImage>();
    public DbSet<City> Cities => Set<City>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<City>(entity =>
        {
            entity.Property(x => x.Name).HasMaxLength(100).IsRequired();
            entity.HasIndex(x => x.Name).IsUnique();
            entity.HasData(new City { Id = 1, Name = "Иркутск" }, new City { Id = 2, Name = "Ангарск" },
                new City { Id = 3, Name = "Шелехов" }, new City { Id = 4, Name = "Москва" },
                new City { Id = 5, Name = "Санкт-Петербург" }, new City { Id = 6, Name = "Новосибирск" });
        });
        builder.Entity<Listing>(entity =>
        {
            entity.Property(x => x.Title).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(10000).IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.Condition).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne<AppUser>().WithMany().HasForeignKey(x => x.AuthorId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<City>().WithMany().HasForeignKey(x => x.CityId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.AuthorId, x.CreatedAtUtc, x.Id });
            entity.HasIndex(x => new { x.Status, x.CityId, x.CategoryId, x.PriceMinor });
            entity.ToTable(t => t.HasCheckConstraint("CK_Listings_Price", "\"PriceMinor\" IS NULL OR (\"PriceMinor\" >= 0 AND \"PriceMinor\" <= 100000000000)"));
        });
        builder.Entity<ListingContact>(entity =>
        {
            entity.HasKey(x => x.ListingId);
            entity.Property(x => x.Phone).HasMaxLength(30).IsRequired();
            entity.HasOne<Listing>().WithOne(x => x.Contact).HasForeignKey<ListingContact>(x => x.ListingId);
        });
        builder.Entity<ListingImage>(entity =>
        {
            entity.HasOne(x => x.Listing).WithMany(x => x.Images).HasForeignKey(x => x.ListingId);
            entity.HasIndex(x => new { x.ListingId, x.Position }).IsUnique();
        });
        builder.Entity<AppUser>().Property(x => x.DeliveryAddress).HasMaxLength(500);
        builder.Entity<AppUser>().Property(x => x.DisplayName).HasMaxLength(100);
        builder.Entity<AppUser>().HasIndex(x => x.NormalizedEmail).IsUnique();
        builder.Entity<AccountSession>(entity =>
        {
            entity.ToTable("AccountSessions");
            entity.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId);
            entity.HasIndex(x => x.ExpiresAtUtc);
        });
        builder.Entity<CheckoutBatch>(entity =>
        {
            entity.Property(x => x.Fingerprint).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => new { x.BuyerId, x.RequestId }).IsUnique();
            entity.HasOne(x => x.Buyer).WithMany().HasForeignKey(x => x.BuyerId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<Order>(entity =>
        {
            entity.Property(x => x.Number).HasMaxLength(35).IsRequired();
            entity.HasIndex(x => x.Number).IsUnique();
            entity.Property(x => x.RecipientName).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Phone).HasMaxLength(30).IsRequired();
            entity.Property(x => x.DeliveryAddress).HasMaxLength(500).IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne(x => x.Buyer).WithMany().HasForeignKey(x => x.BuyerId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Seller).WithMany().HasForeignKey(x => x.SellerId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.CheckoutBatch).WithMany(x => x.Orders).HasForeignKey(x => x.CheckoutBatchId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.BuyerId, x.CreatedAtUtc, x.Id });
            entity.HasIndex(x => new { x.SellerId, x.CreatedAtUtc, x.Id });
        });
        builder.Entity<OrderItem>(entity =>
        {
            entity.Property(x => x.ProductName).HasMaxLength(200).IsRequired();
            entity.HasOne(x => x.Order).WithMany(x => x.Items).HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(t =>
            {
                t.HasCheckConstraint("CK_OrderItems_Quantity", "\"Quantity\" >= 1 AND \"Quantity\" <= 10000");
                t.HasCheckConstraint("CK_OrderItems_Price", "\"UnitPriceMinor\" > 0 AND \"UnitPriceMinor\" <= 100000000000");
            });
        });
        builder.Entity<CartItem>(entity =>
        {
            entity.HasKey(x => new { x.UserId, x.ProductId });
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(t => t.HasCheckConstraint("CK_CartItems_Quantity", "\"Quantity\" >= 1 AND \"Quantity\" <= 10000"));
        });
        builder.Entity<Category>(entity =>
        {
            entity.Property(x => x.Name).HasMaxLength(100).IsRequired();
            entity.HasIndex(x => x.Name).IsUnique();
            entity.HasData(
                new Category { Id = 1, Name = "Одежда" },
                new Category { Id = 2, Name = "Обувь" },
                new Category { Id = 3, Name = "Электроника" },
                new Category { Id = 4, Name = "Дом и быт" },
                new Category { Id = 5, Name = "Спорт и отдых" },
                new Category { Id = 6, Name = "Другое" });
        });
        builder.Entity<Product>(entity =>
        {
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.Property(x => x.Description).HasMaxLength(10000).IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            entity.HasOne(x => x.Seller).WithMany().HasForeignKey(x => x.SellerId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.Status, x.CreatedAtUtc, x.Id });
            entity.HasIndex(x => new { x.Status, x.CategoryId, x.PriceMinor });
            entity.HasIndex(x => new { x.Status, x.PriceMinor, x.Id });
            entity.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Products_Price", "\"PriceMinor\" > 0 AND \"PriceMinor\" <= 100000000000");
                t.HasCheckConstraint("CK_Products_Stock", "\"Stock\" >= 0");
            });
        });
        builder.Entity<ProductImage>(entity =>
        {
            entity.Property(x => x.Url).HasMaxLength(2048).IsRequired();
            entity.HasOne(x => x.Product).WithMany(x => x.Images).HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.ProductId, x.Position }).IsUnique();
        });
    }
}




