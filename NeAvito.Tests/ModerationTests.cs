using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NeAvito.Data;
using NeAvito.Models;
using SkiaSharp;

namespace NeAvito.Tests;

public sealed class ModerationTests
{
    internal static async Task<HttpClient> Moderator(WebApplicationFactory<Program> app, string name = "moderator")
    {
        var client = await OrderTests.Account(app, name);
        using var scope = app.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        if (!await roles.RoleExistsAsync(ModerationRoles.Moderator)) Assert.True((await roles.CreateAsync(new IdentityRole(ModerationRoles.Moderator))).Succeeded);
        Assert.True((await users.AddToRoleAsync((await users.FindByEmailAsync(name + "@example.com"))!, ModerationRoles.Moderator)).Succeeded);
        return client;
    }
    internal static async Task<JsonElement> Json(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    { Assert.Equal(expected, response.StatusCode); return await response.Content.ReadFromJsonAsync<JsonElement>(); }
    internal static async Task<JsonElement> Publish(HttpClient owner)
    {
        var listing = await Json(await owner.PostAsJsonAsync("/api/me/listings", new { title = "Проверяемая вещь", description = "Подробное описание", price = 1, categoryId = 1, cityId = 1, condition = "Used", phone = "+79991234567", contactConsent = true }), HttpStatusCode.Created);
        using var bitmap = new SKBitmap(2, 2); bitmap.Erase(SKColors.Green);
        using var image = SKImage.FromBitmap(bitmap); using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        using var form = new MultipartFormDataContent(); form.Add(new StringContent(listing.GetProperty("version").GetString()!), "version");
        form.Add(new ByteArrayContent(encoded.ToArray()), "file", "photo.png");
        var id = listing.GetProperty("id").GetGuid();
        listing = await Json(await owner.PostAsync($"/api/me/listings/{id}/images", form));
        return await Json(await owner.PostAsJsonAsync($"/api/me/listings/{id}/publish", new { version = listing.GetProperty("version").GetGuid() }));
    }
    internal static async Task<Guid> Report(HttpClient reporter, Guid listingId) =>
        (await Json(await reporter.PostAsJsonAsync($"/api/listings/{listingId}/reports", new { reason = "SuspectedFraud", comment = "Проверить описание" }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
    internal static object Decision(JsonElement detail, string action = "HideListing", string reason = "Нарушение правил") => new
    { action, reason, version = detail.GetProperty("report").GetProperty("version").GetGuid(),
        listingVersion = detail.GetProperty("listing").GetProperty("version").GetGuid(), authorVersion = detail.GetProperty("author").GetProperty("version").GetString() };
    internal static Task<JsonElement> Detail(HttpClient moderator, Guid id) => moderator.GetFromJsonAsync<JsonElement>($"/api/moderation/reports/{id}");

    [Fact]
    public async Task ReportsRequireLoginValidateReasonAndDeduplicateOpenRequests()
    {
        using var app = new AuthFactory(enableLegacy: false); using var guest = app.Client(); using var owner = await OrderTests.Account(app, "owner");
        using var reporter = await OrderTests.Account(app, "reporter"); var listing = await Publish(owner); var id = listing.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.PostAsJsonAsync($"/api/listings/{id}/reports", new { reason = "Other", comment = "test" })).StatusCode);
        foreach (var reason in new[] { "Unknown", "Other", "0" }) Assert.Equal(HttpStatusCode.BadRequest, (await reporter.PostAsJsonAsync($"/api/listings/{id}/reports", new { reason })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/api/listings/{id}/reports", new { reason = "ProhibitedItem" })).StatusCode);
        var first = await Report(reporter, id);
        var duplicate = await Json(await reporter.PostAsJsonAsync($"/api/listings/{id}/reports", new { reason = "InaccurateDescription" }));
        Assert.Equal(first, duplicate.GetProperty("id").GetGuid());
        reporter.DefaultRequestHeaders.Remove("X-NeAvito-Request");
        Assert.Equal(HttpStatusCode.Forbidden, (await reporter.PostAsJsonAsync($"/api/listings/{id}/reports", new { reason = "ProhibitedItem" })).StatusCode);
    }

    [Fact]
    public async Task ModerationRequiresRoleForEveryReadAndWriteAndRoleRevocationIsImmediate()
    {
        using var app = new AuthFactory(enableLegacy: false); using var guest = app.Client(); using var normal = await OrderTests.Account(app, "normal");
        using var mod = await Moderator(app); var id = Guid.NewGuid();
        Assert.False((await normal.GetFromJsonAsync<JsonElement>("/api/profile")).GetProperty("canModerate").GetBoolean());
        Assert.True((await mod.GetFromJsonAsync<JsonElement>("/api/profile")).GetProperty("canModerate").GetBoolean());
        foreach (var url in new[] { "/api/moderation/reports", $"/api/moderation/reports/{id}", $"/api/moderation/reports/{id}/audit", $"/api/moderation/images/{id}" })
        { Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync(url)).StatusCode); Assert.Equal(HttpStatusCode.Forbidden, (await normal.GetAsync(url)).StatusCode); }
        foreach (var suffix in new[] { "decision", "release" })
        { Assert.Equal(HttpStatusCode.Unauthorized, (await guest.PostAsJsonAsync($"/api/moderation/reports/{id}/{suffix}", new { })).StatusCode); Assert.Equal(HttpStatusCode.Forbidden, (await normal.PostAsJsonAsync($"/api/moderation/reports/{id}/{suffix}", new { })).StatusCode); }
        using var scope = app.Services.CreateScope(); var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        await users.RemoveFromRoleAsync((await users.FindByEmailAsync("moderator@example.com"))!, ModerationRoles.Moderator);
        Assert.Equal(HttpStatusCode.Forbidden, (await mod.GetAsync("/api/moderation/reports")).StatusCode);
    }

    [Fact]
    public async Task HideIsAtomicAuditedPrivateAndRestorationRequiresRepublishing()
    {
        using var app = new AuthFactory(enableLegacy: false); using var guest = app.Client(); using var owner = await OrderTests.Account(app, "owner");
        using var reporter = await OrderTests.Account(app, "reporter"); using var mod = await Moderator(app);
        var listing = await Publish(owner); var id = listing.GetProperty("id").GetGuid(); var reportId = await Report(reporter, id);
        var detail = await Detail(mod, reportId); var photo = listing.GetProperty("images")[0].GetProperty("url").GetString();
        Assert.DoesNotContain("+7999", detail.ToString()); Assert.DoesNotContain("@example", detail.ToString());
        Assert.Equal(HttpStatusCode.OK, (await mod.PostAsJsonAsync($"/api/moderation/reports/{reportId}/decision", Decision(detail))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await mod.PostAsJsonAsync($"/api/moderation/reports/{reportId}/decision", Decision(detail))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/api/listings/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync(photo)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reporter.GetAsync($"/api/listings/{id}/contact")).StatusCode);
        Assert.Equal(0, (await guest.GetFromJsonAsync<JsonElement>("/api/listings")).GetProperty("totalCount").GetInt32());
        var own = await owner.GetFromJsonAsync<JsonElement>($"/api/me/listings/{id}"); Assert.Equal("Нарушение правил", own.GetProperty("moderationReason").GetString());
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/me/listings/{id}/unpublish", new { version = own.GetProperty("version").GetGuid() })).StatusCode);
        own = await owner.GetFromJsonAsync<JsonElement>($"/api/me/listings/{id}");
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/api/me/listings/{id}/publish", new { version = own.GetProperty("version").GetGuid() })).StatusCode);
        detail = await Detail(mod, reportId);
        Assert.Equal(HttpStatusCode.OK, (await mod.GetAsync(detail.GetProperty("listing").GetProperty("images")[0].GetProperty("url").GetString())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await mod.PostAsJsonAsync($"/api/moderation/reports/{reportId}/release", Decision(detail, "RestoreListing", "Исправлено"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await mod.PostAsJsonAsync($"/api/moderation/reports/{reportId}/release", Decision(detail, "RestoreListing", "Исправлено"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/api/listings/{id}")).StatusCode);
        own = await owner.GetFromJsonAsync<JsonElement>($"/api/me/listings/{id}");
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/me/listings/{id}/publish", new { version = own.GetProperty("version").GetGuid() })).StatusCode);
        var audit = await mod.GetFromJsonAsync<JsonElement>($"/api/moderation/reports/{reportId}/audit"); Assert.Equal(2, audit.GetProperty("totalCount").GetInt32());
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(ReportStatus.Resolved, db.ListingReports.Single().Status); Assert.Equal(2, db.ModerationAudits.Count());
    }

    [Fact]
    public async Task BlockingRevokesAllSessionsAndUnblockingDoesNotResurrectThem()
    {
        using var app = new AuthFactory(enableLegacy: false); using var guest = app.Client(); using var owner = await OrderTests.Account(app, "owner");
        using var otherSession = app.Client(); await otherSession.PostAsJsonAsync("/api/auth/login", new { email = "owner@example.com", password = "TestPassword123!" });
        using var reporter = await OrderTests.Account(app, "reporter"); using var mod = await Moderator(app);
        var listing = await Publish(owner); var second = await Publish(owner); var id = listing.GetProperty("id").GetGuid(); var reportId = await Report(reporter, id);
        var detail = await Detail(mod, reportId);
        Assert.Equal(HttpStatusCode.OK, (await mod.PostAsJsonAsync($"/api/moderation/reports/{reportId}/decision", Decision(detail, "BlockAuthor"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.PostAsJsonAsync("/api/me/listings", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync("/api/listings/" + second.GetProperty("id").GetString())).StatusCode);
        detail = await Detail(mod, reportId);
        Assert.Equal(HttpStatusCode.Conflict, (await mod.PostAsJsonAsync($"/api/moderation/reports/{reportId}/release", Decision(detail, "RestoreListing"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await mod.PostAsJsonAsync($"/api/moderation/reports/{reportId}/release", Decision(detail, "UnblockAuthor", "Проверка завершена"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await otherSession.GetAsync("/api/profile")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync("/api/auth/login", new { email = "owner@example.com", password = "TestPassword123!" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/api/listings/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync("/api/listings/" + second.GetProperty("id").GetString())).StatusCode);
    }

    [Fact]
    public async Task StaleOrInvalidDecisionsHaveNoSideEffectsAndResolvedReportAllowsNewComplaint()
    {
        using var app = new AuthFactory(enableLegacy: false); using var guest = app.Client(); using var owner = await OrderTests.Account(app, "owner");
        using var reporter = await OrderTests.Account(app, "reporter"); using var mod = await Moderator(app);
        var listing = await Publish(owner); var id = listing.GetProperty("id").GetGuid(); var reportId = await Report(reporter, id); var detail = await Detail(mod, reportId);
        Assert.Equal(HttpStatusCode.BadRequest, (await mod.PostAsJsonAsync($"/api/moderation/reports/{reportId}/decision", Decision(detail, reason: "  "))).StatusCode);
        await owner.PostAsJsonAsync($"/api/me/listings/{id}/unpublish", new { version = listing.GetProperty("version").GetGuid() });
        Assert.Equal(HttpStatusCode.Conflict, (await mod.PostAsJsonAsync($"/api/moderation/reports/{reportId}/decision", Decision(detail))).StatusCode);
        using (var scope = app.Services.CreateScope()) { var db = scope.ServiceProvider.GetRequiredService<AppDbContext>(); Assert.Empty(db.ModerationAudits); Assert.False(db.Listings.Single().ModerationHidden); Assert.Equal(ReportStatus.Pending, db.ListingReports.Single().Status); }
        detail = await Detail(mod, reportId);
        Assert.Equal(HttpStatusCode.OK, (await mod.PostAsJsonAsync($"/api/moderation/reports/{reportId}/decision", Decision(detail, "Reject"))).StatusCode);
        var own = await owner.GetFromJsonAsync<JsonElement>($"/api/me/listings/{id}");
        await owner.PostAsJsonAsync($"/api/me/listings/{id}/publish", new { version = own.GetProperty("version").GetGuid() });
        Assert.NotEqual(reportId, await Report(reporter, id));
        Assert.Equal(1, (await mod.GetFromJsonAsync<JsonElement>("/api/moderation/reports?status=Pending")).GetProperty("totalCount").GetInt32());
        Assert.Equal(1, (await mod.GetFromJsonAsync<JsonElement>("/api/moderation/reports?status=Resolved")).GetProperty("totalCount").GetInt32());
        var first = await mod.GetStringAsync("/api/moderation/reports?pageSize=1"); Assert.Equal(first, await mod.GetStringAsync("/api/moderation/reports?pageSize=1"));
        Assert.Equal(HttpStatusCode.BadRequest, (await mod.GetAsync("/api/moderation/reports?page=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await mod.GetAsync("/api/moderation/reports?status=Other")).StatusCode);
    }

    [Fact]
    public async Task ReporterCannotModerateTheirOwnComplaintAndReportRateIsLimited()
    {
        using var app = new AuthFactory(enableLegacy: false); using var guest = app.Client(); using var owner = await OrderTests.Account(app, "owner");
        using var mod = await Moderator(app); var id = (await Publish(owner)).GetProperty("id").GetGuid(); var reportId = await Report(mod, id); var detail = await Detail(mod, reportId);
        Assert.Equal(HttpStatusCode.Conflict, (await mod.PostAsJsonAsync($"/api/moderation/reports/{reportId}/decision", Decision(detail))).StatusCode);
        for (var i = 1; i < 10; i++) Assert.Equal(HttpStatusCode.OK, (await mod.PostAsJsonAsync($"/api/listings/{id}/reports", new { reason = "ProhibitedItem" })).StatusCode);
        var response = await mod.PostAsJsonAsync($"/api/listings/{id}/reports", new { reason = "ProhibitedItem" });
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode); Assert.NotNull(response.Headers.RetryAfter);
    }

    [PostgreSqlFact]
    public async Task ConcurrentModeratorsProduceOneDecisionAndOneAudit()
    {
        using var app = new PostgreSqlFactory(); app.Initialize(); using var owner = await OrderTests.Account(app, "owner");
        using var reporter = await OrderTests.Account(app, "reporter"); using var mod = await Moderator(app); using var mod2 = await Moderator(app, "moderator2");
        var id = (await Publish(owner)).GetProperty("id").GetGuid();
        var reports = await Task.WhenAll(reporter.PostAsJsonAsync($"/api/listings/{id}/reports", new { reason = "Other", comment = "First" }), reporter.PostAsJsonAsync($"/api/listings/{id}/reports", new { reason = "Other", comment = "Second" }));
        Assert.Single(reports, r => r.StatusCode == HttpStatusCode.Created); Assert.Single(reports, r => r.StatusCode == HttpStatusCode.OK);
        var reportId = (await reports[0].Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var detail = await Detail(mod, reportId);
        var results = await Task.WhenAll(mod.PostAsJsonAsync($"/api/moderation/reports/{reportId}/decision", Decision(detail)), mod2.PostAsJsonAsync($"/api/moderation/reports/{reportId}/decision", Decision(detail, "Reject")));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK); Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Single(db.ListingReports); Assert.Single(db.ModerationAudits);
        Assert.Equal(db.ListingReports.Single().Decision == ModerationAction.HideListing, db.Listings.Single().ModerationHidden);
    }

    [Fact]
    public async Task ModeratorCannotReviewOwnListingOrBlockAnotherModeratorAndMutationsRequireHeader()
    {
        using var app = new AuthFactory(enableLegacy: false); using var guest = app.Client();
        using var owner = await Moderator(app, "owner"); using var mod = await Moderator(app); using var reporter = await OrderTests.Account(app, "reporter");
        var id = (await Publish(owner)).GetProperty("id").GetGuid(); var reportId = await Report(reporter, id); var detail = await Detail(mod, reportId);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync($"/api/moderation/reports/{reportId}/decision", Decision(detail, "Reject"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await mod.PostAsJsonAsync($"/api/moderation/reports/{reportId}/decision", Decision(detail, "BlockAuthor"))).StatusCode);
        mod.DefaultRequestHeaders.Remove("X-NeAvito-Request");
        foreach (var suffix in new[] { "decision", "release" })
            Assert.Equal(HttpStatusCode.Forbidden, (await mod.PostAsJsonAsync($"/api/moderation/reports/{reportId}/{suffix}", Decision(detail))).StatusCode);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(db.ModerationAudits); Assert.False(db.Listings.Single().ModerationHidden); Assert.False(db.Users.Single(x => x.UserName == "owner@example.com").IsBlocked);
    }

    [PostgreSqlFact]
    public async Task LosingConcurrentBlockRollsBackSessionRevocationWithItsDecision()
    {
        using var app = new PostgreSqlFactory(); app.Initialize(); using var owner = await OrderTests.Account(app, "owner");
        using var reporter = await OrderTests.Account(app, "reporter"); using var mod = await Moderator(app); using var mod2 = await Moderator(app, "moderator2");
        var id = (await Publish(owner)).GetProperty("id").GetGuid(); var reportId = await Report(reporter, id); var detail = await Detail(mod, reportId);
        var results = await Task.WhenAll(mod.PostAsJsonAsync($"/api/moderation/reports/{reportId}/decision", Decision(detail, "BlockAuthor")),
            mod2.PostAsJsonAsync($"/api/moderation/reports/{reportId}/decision", Decision(detail, "Reject")));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK); Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blocked = db.ListingReports.Single().Decision == ModerationAction.BlockAuthor;
        Assert.Equal(blocked, db.Users.Single(x => x.UserName == "owner@example.com").IsBlocked); Assert.Single(db.ModerationAudits);
        Assert.Equal(blocked ? HttpStatusCode.Unauthorized : HttpStatusCode.OK, (await owner.GetAsync("/api/profile")).StatusCode);
    }
}
