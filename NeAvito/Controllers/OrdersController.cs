using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NeAvito.Data;
using NeAvito.Models;
using Npgsql;

namespace NeAvito.Controllers;

[Authorize]
[ApiController]
[Route("api/orders")]
public sealed class OrdersController(AppDbContext db, ILogger<OrdersController> logger) : ControllerBase
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    internal static object View(Order order) => new
    {
        order.Id, order.Number, Status = order.Status.ToString(), order.Version,
        order.BuyerId, order.SellerId, order.RecipientName, order.Phone, order.DeliveryAddress,
        order.CreatedAtUtc, order.StatusUpdatedAtUtc, Currency = "RUB",
        Total = order.Items.Sum(x => x.UnitPriceMinor / 100m * x.Quantity),
        Items = order.Items.OrderBy(x => x.ProductId).Select(x => new
        { x.ProductId, x.ProductName, UnitPrice = x.UnitPriceMinor / 100m, x.Quantity, LineTotal = x.UnitPriceMinor / 100m * x.Quantity })
    };

    [HttpPost("checkout")]
    public async Task<IActionResult> Checkout(CheckoutRequest request, CancellationToken cancellationToken)
    {
        if (request.RequestId == Guid.Empty || request.Items.Any(x => x is null || x.ProductId == Guid.Empty || x.Version == Guid.Empty)
            || request.Items.Select(x => x.ProductId).Distinct().Count() != request.Items.Count
            || string.IsNullOrWhiteSpace(request.RecipientName) || string.IsNullOrWhiteSpace(request.DeliveryAddress)
            || request.Items.Any(x => decimal.Round(x.UnitPrice, 2) != x.UnitPrice))
            return BadRequest(new { message = "Проверьте идентификаторы, состав заказа, цены и контактные данные." });
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Name = request.RecipientName.Trim(), Phone = request.Phone.Trim(), Address = request.DeliveryAddress.Trim(),
            Items = request.Items.OrderBy(x => x.ProductId).Select(x => new { x.ProductId, x.Quantity, UnitPriceMinor = (long)(x.UnitPrice * 100), x.Version })
        })));
        var previous = await db.CheckoutBatches.AsNoTracking().Include(x => x.Orders).ThenInclude(x => x.Items)
            .SingleOrDefaultAsync(x => x.BuyerId == UserId && x.RequestId == request.RequestId, cancellationToken);
        if (previous is not null) return previous.Fingerprint == fingerprint
            ? Ok(new { request.RequestId, Orders = previous.Orders.OrderBy(x => x.SellerId).Select(View) })
            : Conflict(new { message = "Этот requestId уже использован для другого запроса." });

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var cart = await db.CartItems.Include(x => x.Product).Where(x => x.UserId == UserId)
                .OrderBy(x => x.ProductId).ToListAsync(cancellationToken);
            var expected = request.Items.ToDictionary(x => x.ProductId);
            if (cart.Count == 0 || cart.Count != expected.Count || cart.Any(x => !expected.ContainsKey(x.ProductId)))
                return Conflict(new { message = "Состав корзины изменился или корзина пуста. Проверьте ее заново." });
            var issues = cart.Where(x => x.Version != expected[x.ProductId].Version ||
                x.Quantity != expected[x.ProductId].Quantity || x.Product.PriceMinor != expected[x.ProductId].UnitPrice * 100
                || x.Product.Status != ProductStatus.Published || x.Product.Stock < x.Quantity)
                .Select(x => new { x.ProductId, CurrentPrice = x.Product.PriceMinor / 100m, AvailableQuantity = x.Product.Stock,
                    message = "Цена, количество или доступность товара изменились. Обновите корзину." }).ToList();
            if (issues.Count > 0) return Conflict(new { message = "Заказ не оформлен. Проверьте изменения.", issues });

            var batch = new CheckoutBatch { BuyerId = UserId, RequestId = request.RequestId, Fingerprint = fingerprint };
            foreach (var group in cart.GroupBy(x => x.Product.SellerId))
            {
                var order = new Order
                {
                    BuyerId = UserId, SellerId = group.Key, CheckoutBatch = batch,
                    RecipientName = request.RecipientName.Trim(), Phone = request.Phone.Trim(), DeliveryAddress = request.DeliveryAddress.Trim()
                };
                foreach (var item in group)
                {
                    order.Items.Add(new OrderItem { ProductId = item.ProductId, ProductName = item.Product.Name,
                        UnitPriceMinor = item.Product.PriceMinor, Quantity = item.Quantity });
                    item.Product.Stock -= item.Quantity;
                    item.Product.Version = Guid.NewGuid();
                }
                batch.Orders.Add(order);
            }
            db.CheckoutBatches.Add(batch);
            db.CartItems.RemoveRange(cart);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("Создано {OrderCount} заказов для оформления {CheckoutId}", batch.Orders.Count, batch.Id);
            return StatusCode(201, new { request.RequestId, Orders = batch.Orders.OrderBy(x => x.SellerId).Select(View) });
        }
        catch (Exception exception) when (IsConflict(exception))
        { return Conflict(new { message = "Данные изменились при оформлении. Обновите корзину; для повтора используйте прежний requestId." }); }
    }

    [HttpGet]
    public Task<IActionResult> List([FromQuery, Range(1, 1000000)] int page = 1,
        [FromQuery, Range(1, 100)] int pageSize = 20, CancellationToken cancellationToken = default) =>
        ReadList(db.Orders.Where(x => x.BuyerId == UserId), page, pageSize, cancellationToken);

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var order = await db.Orders.AsNoTracking().Include(x => x.Items)
            .SingleOrDefaultAsync(x => x.Id == id && x.BuyerId == UserId, cancellationToken);
        return order is null ? NotFound() : Ok(View(order));
    }

    [HttpPost("{id:guid}/cancel")]
    public Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken) => ChangeStatus(id, "Cancelled", false, cancellationToken);

    [HttpGet("/api/seller/orders")]
    public Task<IActionResult> SellerList([FromQuery, Range(1, 1000000)] int page = 1,
        [FromQuery, Range(1, 100)] int pageSize = 20, CancellationToken cancellationToken = default) =>
        ReadList(db.Orders.Where(x => x.SellerId == UserId), page, pageSize, cancellationToken);

    [HttpGet("/api/seller/orders/{id:guid}")]
    public async Task<IActionResult> SellerGet(Guid id, CancellationToken cancellationToken)
    {
        var order = await db.Orders.AsNoTracking().Include(x => x.Items)
            .SingleOrDefaultAsync(x => x.Id == id && x.SellerId == UserId, cancellationToken);
        return order is null ? NotFound() : Ok(View(order));
    }

    [HttpPost("/api/seller/orders/{id:guid}/status")]
    public Task<IActionResult> SellerStatus(Guid id, StatusRequest request, CancellationToken cancellationToken) =>
        ChangeStatus(id, request.Status, true, cancellationToken);

    private async Task<IActionResult> ReadList(IQueryable<Order> query, int page, int pageSize, CancellationToken cancellationToken)
    {
        var total = await query.CountAsync(cancellationToken);
        var orders = await query.AsNoTracking().Include(x => x.Items).OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return Ok(new { page, pageSize, totalCount = total, totalPages = (int)Math.Ceiling((double)total / pageSize), items = orders.Select(View) });
    }

    private async Task<IActionResult> ChangeStatus(Guid id, string status, bool seller, CancellationToken cancellationToken)
    {
        if (status is not ("Confirmed" or "Completed" or "Cancelled"))
            return BadRequest(new { message = "Допустимые статусы: Confirmed, Completed, Cancelled." });
        var target = Enum.Parse<OrderStatus>(status);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var order = await db.Orders.Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == id &&
                (seller ? x.SellerId == UserId : x.BuyerId == UserId), cancellationToken);
            if (order is null) return NotFound();
            if (order.Status == target) return Ok(View(order));
            var allowed = (order.Status == OrderStatus.New && target == OrderStatus.Confirmed && seller)
                || (order.Status == OrderStatus.Confirmed && target == OrderStatus.Completed && seller)
                || (order.Status is OrderStatus.New or OrderStatus.Confirmed && target == OrderStatus.Cancelled);
            if (!allowed) return Conflict(new { message = "Этот переход статуса недопустим." });
            if (target == OrderStatus.Cancelled)
            {
                var ids = order.Items.Select(x => x.ProductId).ToList();
                var products = await db.Products.Where(x => ids.Contains(x.Id)).OrderBy(x => x.Id).ToDictionaryAsync(x => x.Id, cancellationToken);
                foreach (var item in order.Items)
                {
                    var product = products[item.ProductId];
                    if ((long)product.Stock + item.Quantity > int.MaxValue)
                        return Conflict(new { message = "Остаток товара слишком велик для возврата. Продавцу необходимо скорректировать остаток." });
                    product.Stock += item.Quantity;
                    product.Version = Guid.NewGuid();
                }
            }
            order.Status = target; order.Version = Guid.NewGuid(); order.StatusUpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("Статус заказа {OrderId} изменен на {Status}", order.Id, target);
            return Ok(View(order));
        }
        catch (Exception exception) when (IsConflict(exception))
        { return Conflict(new { message = "Заказ или остатки изменились. Обновите данные и повторите действие." }); }
    }

    private static bool IsConflict(Exception exception)
    {
        // The provider may wrap serialization failures in an execution-strategy exception.
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is DbUpdateConcurrencyException || current is PostgresException { SqlState: "40001" or "40P01" or "23505" })
                return true;
        return false;
    }
}

public sealed class CheckoutRequest
{
    public Guid RequestId { get; set; }
    [Required, MaxLength(100)] public string RecipientName { get; set; } = string.Empty;
    [Required, Phone, MaxLength(30)] public string Phone { get; set; } = string.Empty;
    [Required, MaxLength(500)] public string DeliveryAddress { get; set; } = string.Empty;
    [Required, MinLength(1), MaxLength(1000)] public List<CheckoutItemRequest> Items { get; set; } = [];
}
public sealed class CheckoutItemRequest
{
    public Guid ProductId { get; set; }
    public Guid Version { get; set; }
    [Range(1, 10000)] public int Quantity { get; set; }
    [Range(typeof(decimal), "0.01", "1000000000", ParseLimitsInInvariantCulture = true)] public decimal UnitPrice { get; set; }
}
public sealed class StatusRequest
{
    [Required, MaxLength(20)] public string Status { get; set; } = string.Empty;
}


