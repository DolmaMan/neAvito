using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using NeAvito.Data;
using Npgsql;

namespace NeAvito.Tests;

public sealed class CutoverMigrationTests
{
    [PostgreSqlFact]
    public async Task CutoverPreservesDataRejectsActiveOrdersAndFreezesLegacyWriters()
    {
        var schema = "neavito_cutover_" + Guid.NewGuid().ToString("N");
        var connectionString = new ConfigurationBuilder().AddUserSecrets<Program>().Build().GetConnectionString("Postgres")!;
        await using var admin = new NpgsqlConnection(connectionString); await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema }.ConnectionString).Options;
            await using var db = new AppDbContext(options); var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20261109161926_AddListings");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "AspNetUsers" ("Id","UserName","EmailConfirmed","PhoneNumberConfirmed","TwoFactorEnabled","LockoutEnabled","AccessFailedCount","DeliveryAddress","DisplayName","PhoneNumber")
                VALUES ('old','old',true,false,false,true,0,'private address','Old seller','+79999999999');
                INSERT INTO "Products" ("Id","Name","Description","PriceMinor","Stock","Version","CategoryId","SellerId","Status","CreatedAtUtc")
                VALUES ('10000000-0000-0000-0000-000000000001','Original','Description',12345,0,'10000000-0000-0000-0000-000000000002',1,'old','Published',CURRENT_TIMESTAMP),
                ('10000000-0000-0000-0000-000000000003','Draft','Description',100,10,'10000000-0000-0000-0000-000000000004',1,'old','Draft',CURRENT_TIMESTAMP);
                INSERT INTO "ProductImages" ("Id","ProductId","Url","Position") VALUES ('10000000-0000-0000-0000-000000000005','10000000-0000-0000-0000-000000000001','/api/product-images/10000000-0000-0000-0000-000000000005',0);
                INSERT INTO "CheckoutBatches" ("Id","BuyerId","RequestId","Fingerprint") VALUES ('20000000-0000-0000-0000-000000000001','old','20000000-0000-0000-0000-000000000002','fixture');
                INSERT INTO "Orders" ("Id","Number","CheckoutBatchId","BuyerId","SellerId","RecipientName","Phone","DeliveryAddress","Status","Version","CreatedAtUtc","StatusUpdatedAtUtc") VALUES
                ('30000000-0000-0000-0000-000000000001','fixture','20000000-0000-0000-0000-000000000001','old','old','Private recipient','+78888888888','Private order address','New','30000000-0000-0000-0000-000000000002',CURRENT_TIMESTAMP,CURRENT_TIMESTAMP);
                """);
            var failure = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync());
            Assert.Contains("Resolve active orders", failure.MessageText);
            Assert.DoesNotContain("20261109172437_PublicListingsCutover", await db.Database.GetAppliedMigrationsAsync());
            await db.Database.ExecuteSqlRawAsync("UPDATE \"Orders\" SET \"Status\"='Completed'");
            await migrator.MigrateAsync(); await migrator.MigrateAsync();
            var listings = await db.Listings.Include(x => x.Contact).Include(x => x.Images).OrderBy(x => x.Title).ToListAsync();
            Assert.Equal(2, listings.Count); Assert.Equal(Models.ListingStatus.Draft, listings[0].Status);
            var imported = listings[1]; Assert.Equal(Models.ListingStatus.Unpublished, imported.Status); Assert.Equal(12345, imported.PriceMinor);
            Assert.Equal(Guid.Parse("10000000-0000-0000-0000-000000000001"), imported.Id);
            Assert.Equal("old", imported.AuthorId); Assert.True(imported.LegacyImported); Assert.NotEmpty(imported.TransitionNote!);
            Assert.Equal("", imported.Contact.Phone); Assert.Null(imported.Contact.ConsentedAtUtc); Assert.Null(imported.CityId);
            Assert.Equal(Guid.Parse("10000000-0000-0000-0000-000000000005"), Assert.Single(imported.Images).Id);
            Assert.Equal(2, await db.Products.CountAsync()); Assert.Equal("+78888888888", (await db.Orders.SingleAsync()).Phone);
            // Table identifiers below are a fixed test-owned allowlist.
#pragma warning disable EF1002
            foreach (var table in new[] { "Products", "ProductImages", "CartItems", "Orders", "OrderItems", "CheckoutBatches" })
                Assert.Contains("read-only", (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync($"DELETE FROM \"{table}\" WHERE false"))).MessageText);
#pragma warning restore EF1002
            await Assert.ThrowsAsync<NotSupportedException>(() => migrator.MigrateAsync("20261109161926_AddListings"));
            Assert.Equal(2, await db.Listings.CountAsync());
        }
        finally
        {
            // Only the unique schema created by this test can be removed.
            await using var drop = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", admin); await drop.ExecuteNonQueryAsync();
        }
    }
}
