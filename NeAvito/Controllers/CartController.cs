using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NeAvito.Data;
using NeAvito.Models;
using Npgsql;

namespace NeAvito.Controllers;

[Authorize]
[ApiController]
[Route("api/cart")]
public sealed class CartController(AppDbContext db) : ControllerBase
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    private IQueryable<CartItem> OwnItems => db.CartItems.Where(x => x.UserId == UserId);

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var rows = await OwnItems.AsNoTracking().OrderBy(x => x.AddedAtUtc).ThenBy(x => x.ProductId)
            .Select(x => new
            {
                x.ProductId, x.Quantity, x.PriceAtAdditionMinor, x.Version,
                x.Product.Name, x.Product.PriceMinor, x.Product.Stock, x.Product.Status,
                SellerId = x.Product.SellerId, SellerName = x.Product.Seller.DisplayName,
                ImageUrl = x.Product.Status == ProductStatus.Published
                    ? x.Product.Images.OrderBy(i => i.Position).Select(i => i.Url).FirstOrDefault() : null
            }).ToListAsync(cancellationToken);
        var items = rows.Select(x => new
        {
            x.ProductId, x.Name, x.Quantity, Price = x.PriceMinor / 100m,
            PriceAtAddition = x.PriceAtAdditionMinor / 100m, PriceChanged = x.PriceMinor != x.PriceAtAdditionMinor,
            LineTotal = x.PriceMinor / 100m * x.Quantity, x.Stock,
            IsAvailable = x.Status == ProductStatus.Published && x.Stock >= x.Quantity,
            UnavailableReason = x.Status != ProductStatus.Published ? "Товар снят с публикации."
                : x.Stock == 0 ? "Товар закончился." : x.Stock < x.Quantity ? "Недостаточно товара в наличии." : null,
            Seller = new { Id = x.SellerId, DisplayName = x.SellerName }, x.ImageUrl, x.Version
        }).ToList();
        return Ok(new
        {
            Currency = "RUB", Items = items, ItemCount = items.Count,
            TotalQuantity = items.Sum(x => (long)x.Quantity), Total = items.Sum(x => x.LineTotal),
            AvailableTotal = items.Where(x => x.IsAvailable).Sum(x => x.LineTotal),
            HasPriceChanges = items.Any(x => x.PriceChanged),
            CanCheckout = items.Count > 0 && items.All(x => x.IsAvailable)
        });
    }

    // PUT sets the desired quantity rather than incrementing it: retries do not double the cart.
    [HttpPut("items/{productId:guid}")]
    public async Task<IActionResult> SetQuantity(Guid productId, QuantityRequest request, CancellationToken cancellationToken)
    {
        var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.Id == productId &&
            x.Status == ProductStatus.Published, cancellationToken);
        if (product is null) return NotFound(new { message = "Товар не найден или снят с публикации." });
        if (product.Stock < request.Quantity)
            return Conflict(new { message = "Недостаточно товара в наличии.", availableQuantity = product.Stock });
        var item = await OwnItems.SingleOrDefaultAsync(x => x.ProductId == productId, cancellationToken);
        if (item is null)
        {
            item = new CartItem { UserId = UserId, ProductId = productId, Quantity = request.Quantity,
                PriceAtAdditionMinor = product.PriceMinor };
            db.CartItems.Add(item);
        }
        else
        {
            item.Quantity = request.Quantity;
            item.Version = Guid.NewGuid();
        }
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Changed(); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: "23505" })
        { return Changed(); }
        return await Get(cancellationToken);
    }

    [HttpDelete("items/{productId:guid}")]
    public async Task<IActionResult> Remove(Guid productId, CancellationToken cancellationToken)
    {
        await OwnItems.Where(x => x.ProductId == productId).ExecuteDeleteAsync(cancellationToken);
        return NoContent();
    }

    [HttpDelete]
    public async Task<IActionResult> Clear(CancellationToken cancellationToken)
    {
        await OwnItems.ExecuteDeleteAsync(cancellationToken);
        return NoContent();
    }

    private ConflictObjectResult Changed() => Conflict(new { message = "Корзина изменилась. Обновите ее и повторите действие." });
}

public sealed class QuantityRequest
{
    [Range(1, 10000)] public int Quantity { get; set; }
}
