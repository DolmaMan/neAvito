using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using NeAvito.Data;
using NeAvito.Models;
using NeAvito.Services;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
if (builder.Configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
{
    builder.Services.AddDataProtection().SetApplicationName("NeAvito")
        .PersistKeysToFileSystem(new DirectoryInfo(Path.GetFullPath(keysPath, builder.Environment.ContentRootPath)));
}
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddSingleton<NeAvito.Services.ProductImageStorage>();
builder.Services.AddOptions<AccountEmailOptions>().BindConfiguration("AccountEmail")
    .Validate(options => options.IsValid(builder.Environment.IsDevelopment()),
        "Настройте AccountEmail:PublicOrigin, From и SMTP. PickupDirectory доступен в Development.")
    .ValidateOnStart();
builder.Services.AddTransient<IAccountEmail, AccountEmail>();
builder.Services.AddRateLimiter(options =>
{
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        context.Request.Path.StartsWithSegments("/api/listings") && context.Request.Path.Value!.EndsWith("/contact", StringComparison.OrdinalIgnoreCase)
            ? RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(15) })
            : RateLimitPartition.GetNoLimiter("other"));
    options.AddPolicy("contact-account", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(15) }));
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (context, cancellation) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            context.HttpContext.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new ValueTask(context.HttpContext.Response.WriteAsJsonAsync(
            new { message = "Слишком много запросов. Повторите попытку позже." }, cancellation));
    };
    foreach (var (policy, limit) in new[] { ("auth-login", 30), ("auth-mail", 10), ("auth-token", 20) })
        options.AddPolicy(policy, context => RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromMinutes(15) }));
});
builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(
    builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Настройте ConnectionStrings:Postgres через User Secrets или окружение.")));
builder.Services.AddIdentity<AppUser, IdentityRole>(options =>
{
    options.User.RequireUniqueEmail = true;
    options.SignIn.RequireConfirmedEmail = true;
    options.Password.RequiredLength = 8;
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
}).AddEntityFrameworkStores<AppDbContext>().AddDefaultTokenProviders();
builder.Services.Configure<DataProtectionTokenProviderOptions>(options => options.TokenLifespan = TimeSpan.FromHours(1));
builder.Services.AddScoped<AccountCookieEvents>();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "NeAvito.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = false;
    options.EventsType = typeof(AccountCookieEvents);
});
var app = builder.Build();
app.UseExceptionHandler();
if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.UseHttpsRedirection();
app.Use(async (context, next) =>
{
    // A custom header forces browser cross-origin mutations to preflight. CORS is not enabled.
    if (context.Request.Path.StartsWithSegments("/api") &&
        context.Request.Method is "POST" or "PUT" or "PATCH" or "DELETE" &&
        context.Request.Headers["X-NeAvito-Request"] != "1")
    {
        context.Response.StatusCode = 403;
        await context.Response.WriteAsJsonAsync(new { message = "Добавьте заголовок X-NeAvito-Request: 1." });
        return;
    }
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' blob:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    if (context.Request.Path.StartsWithSegments("/api")) context.Response.Headers.CacheControl = "no-store";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // Revalidate the shell and scripts so an API upgrade cannot leave a cached UI behind.
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache"
});
app.UseRouting();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
// Legacy regression fixtures may exercise the former API only in the Testing environment.
if (!(app.Environment.IsEnvironment("Testing") && app.Configuration.GetValue<bool>("LegacyCommerce:Enabled")))
    app.Use(async (context, next) =>
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments("/api/cart") || path.StartsWithSegments("/api/seller/products") ||
            context.Request.Method is not ("GET" or "HEAD") &&
            (path.StartsWithSegments("/api/orders") || path.StartsWithSegments("/api/seller/orders")))
        {
            context.Response.StatusCode = StatusCodes.Status410Gone;
            await context.Response.WriteAsJsonAsync(new { message = "Покупки через корзину закрыты. Свяжитесь с автором объявления. История заказов доступна для чтения." });
            return;
        }
        string? redirect = null;
        if (path.StartsWithSegments("/api/catalog/products", out var rest)) redirect = "/api/listings" + rest;
        if (path == "/api/catalog/categories") redirect = "/api/listings/categories";
        if (path.StartsWithSegments("/api/product-images", out rest)) redirect = "/api/listing-images" + rest;
        if (redirect is not null) { context.Response.Redirect(redirect + context.Request.QueryString); return; }
        await next();
    });
app.MapControllers();
app.Run();
public partial class Program { }


