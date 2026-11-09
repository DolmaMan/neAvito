using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NeAvito.Data;
using SkiaSharp;

namespace NeAvito.Tests;

public sealed class ListingTests
{
    private const string Root = "/api/me/listings";
    private sealed record Draft(string Title = "Велосипед", string Description = "Исправен", decimal? Price = 0,
        int? CategoryId = 5, int? CityId = 1, string? Condition = "Used", string Phone = "+7 (999) 123-45-67",
        bool ContactConsent = true, Guid Version = default);
    private static async Task<JsonElement> Json(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
    {
        Assert.Equal(status, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();
    }
    private static Guid Id(JsonElement listing) => listing.GetProperty("id").GetGuid();
    private static Guid Version(JsonElement listing) => listing.GetProperty("version").GetGuid();
    private static Task<HttpResponseMessage> Transition(HttpClient client, JsonElement listing, string command) =>
        client.PostAsJsonAsync($"{Root}/{Id(listing)}/{command}", new { version = Version(listing) });
    private static async Task<JsonElement> Photo(HttpClient client, JsonElement listing)
    {
        using var bitmap = new SKBitmap(2, 2); bitmap.Erase(SKColors.Green);
        using var image = SKImage.FromBitmap(bitmap); using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(Version(listing).ToString()), "version");
        form.Add(new ByteArrayContent(encoded.ToArray()), "file", "photo.png");
        return await Json(await client.PostAsync($"{Root}/{Id(listing)}/images", form));
    }

    [Fact]
    public async Task PartialDraftAndFreeListingFollowLifecycle()
    {
        using var app = new AuthFactory(); using var guest = app.Client(); using var owner = await OrderTests.Account(app, "owner");
        var listing = await Json(await owner.PostAsJsonAsync(Root, new { }), HttpStatusCode.Created);
        Assert.Equal("Draft", listing.GetProperty("status").GetString());
        Assert.True(listing.GetProperty("publicationErrors").GetArrayLength() >= 7);
        Assert.Equal(HttpStatusCode.BadRequest, (await Transition(owner, listing, "publish")).StatusCode);
        listing = await Json(await owner.PutAsJsonAsync($"{Root}/{Id(listing)}", new Draft(Version: Version(listing))));
        Assert.Equal(0, listing.GetProperty("price").GetDecimal());
        Assert.Equal("+79991234567", listing.GetProperty("phone").GetString());
        listing = await Photo(owner, listing);
        var photoUrl = listing.GetProperty("images")[0].GetProperty("url").GetString();
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync(photoUrl)).StatusCode);
        listing = await Json(await Transition(owner, listing, "publish"));
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync(photoUrl)).StatusCode);
        Assert.Equal(Version(listing), Version(await Json(await Transition(owner, listing, "publish"))));
        listing = await Json(await Transition(owner, listing, "sold"));
        Assert.Equal("Sold", listing.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync(photoUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Transition(owner, listing, "publish")).StatusCode);
        listing = await Json(await Transition(owner, listing, "reopen"));
        listing = await Json(await Transition(owner, listing, "publish"));
        listing = await Json(await Transition(owner, listing, "unpublish"));
        listing = await Json(await Transition(owner, listing, "archive"));
        Assert.Equal("Archived", listing.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync($"{Root}/{Id(listing)}", new Draft(Version: Version(listing)))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(photoUrl)).StatusCode);
    }

    [Fact]
    public async Task OwnershipAndStaleVersionsProtectEveryMutation()
    {
        using var app = new AuthFactory(); using var guest = app.Client(); using var owner = await OrderTests.Account(app, "owner");
        using var other = await OrderTests.Account(app, "other");
        var listing = await Json(await owner.PostAsJsonAsync(Root, new Draft()), HttpStatusCode.Created);
        var stale = listing;
        listing = await Photo(owner, listing);
        var image = listing.GetProperty("images")[0];
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync(Root)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{Root}/{Id(listing)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"{Root}/{Id(listing)}", new Draft(Version: Version(listing)))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Transition(other, listing, "publish")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"{Root}/{Id(listing)}/images/{image.GetProperty("id")}?version={Version(listing)}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync($"{Root}/{Id(listing)}", new Draft(Version: Version(stale)))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Transition(owner, stale, "publish")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.DeleteAsync($"{Root}/{Id(listing)}/images/{image.GetProperty("id")}?version={Version(stale)}")).StatusCode);
        using var file = new MultipartFormDataContent(); file.Add(new StringContent(Version(stale).ToString()), "version");
        file.Add(new ByteArrayContent("bad"u8.ToArray()), "file", "bad.png");
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsync($"{Root}/{Id(listing)}/images", file)).StatusCode);
    }

    [Fact]
    public async Task RevokingConsentOrRemovingLastPhotoUnpublishes()
    {
        using var app = new AuthFactory(); using var guest = app.Client(); using var owner = await OrderTests.Account(app, "owner");
        var listing = await Json(await owner.PostAsJsonAsync(Root, new Draft()), HttpStatusCode.Created);
        listing = await Photo(owner, listing);
        listing = await Json(await Transition(owner, listing, "publish"));
        var image = listing.GetProperty("images")[0];
        listing = await Json(await owner.PutAsJsonAsync($"{Root}/{Id(listing)}", new Draft(ContactConsent: false, Version: Version(listing))));
        Assert.Equal("Unpublished", listing.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, listing.GetProperty("consentedAtUtc").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync(image.GetProperty("url").GetString())).StatusCode);
        listing = await Json(await owner.PutAsJsonAsync($"{Root}/{Id(listing)}", new Draft(Version: Version(listing))));
        listing = await Json(await Transition(owner, listing, "publish"));
        listing = await Json(await owner.DeleteAsync($"{Root}/{Id(listing)}/images/{image.GetProperty("id")}?version={Version(listing)}"));
        Assert.Equal("Unpublished", listing.GetProperty("status").GetString());
        Assert.Equal(0, listing.GetProperty("images").GetArrayLength());
    }

    [Fact]
    public async Task ModerationCannotBeBypassedAndContactIsNotInPublicCatalog()
    {
        using var app = new AuthFactory(); using var guest = app.Client(); using var owner = await OrderTests.Account(app, "owner");
        var listing = await Json(await owner.PostAsJsonAsync(Root, new Draft()), HttpStatusCode.Created);
        listing = await Photo(owner, listing);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Listings.Single(x => x.Id == Id(listing)).ModerationHidden = true; await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await Transition(owner, listing, "publish")).StatusCode);
        Assert.DoesNotContain("79991234567", await guest.GetStringAsync("/api/catalog/products"));
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync(listing.GetProperty("images")[0].GetProperty("url").GetString())).StatusCode);
    }

    [Fact]
    public async Task InvalidValuesAndImageLimitsAreRejected()
    {
        using var app = new AuthFactory(); using var guest = app.Client(); using var owner = await OrderTests.Account(app, "owner");
        foreach (var draft in new[] { new Draft(Price: -1), new Draft(Price: 1.001m), new Draft(CityId: 9999),
            new Draft(CategoryId: 9999), new Draft(Condition: "Other"), new Draft(Phone: "invalid"), new Draft(Phone: "") })
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(Root, draft)).StatusCode);
        var listing = await Json(await owner.PostAsJsonAsync(Root, new Draft()), HttpStatusCode.Created);
        using var bad = new MultipartFormDataContent(); bad.Add(new StringContent(Version(listing).ToString()), "version");
        bad.Add(new ByteArrayContent("not an image"u8.ToArray()), "file", "bad.png");
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync($"{Root}/{Id(listing)}/images", bad)).StatusCode);
        for (var i=0; i<10; i++) listing=await Photo(owner, listing);
        using var extra = new MultipartFormDataContent(); extra.Add(new StringContent(Version(listing).ToString()), "version");
        extra.Add(new ByteArrayContent("unused"u8.ToArray()), "file", "photo.png");
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync($"{Root}/{Id(listing)}/images", extra)).StatusCode);
        Assert.Equal(10, (await Json(await owner.GetAsync($"{Root}/{Id(listing)}"))).GetProperty("images").GetArrayLength());
    }
}
