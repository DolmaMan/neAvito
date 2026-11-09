using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NeAvito.Migrations
{
    /// <inheritdoc />
    public partial class PublicListingsCutover : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "LegacyImported",
                table: "Listings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "TransitionNote",
                table: "Listings",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsBlocked",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);
            migrationBuilder.Sql("""
                LOCK TABLE "Products", "ProductImages", "CartItems", "Orders", "OrderItems", "CheckoutBatches" IN ACCESS EXCLUSIVE MODE;
                DO $$ BEGIN
                  IF EXISTS (SELECT 1 FROM "Orders" WHERE "Status" IN ('New','Confirmed')) THEN
                    RAISE EXCEPTION 'Resolve active orders before classified ads cutover';
                  END IF;
                  IF EXISTS (SELECT 1 FROM "Listings" l JOIN "Products" p ON p."Id"=l."Id") THEN
                    RAISE EXCEPTION 'Listing/product ID collision: manual reconciliation required';
                  END IF;
                END $$;
                INSERT INTO "Listings" ("Id","AuthorId","Title","Description","PriceMinor","CategoryId","CityId","Condition","Status","ModerationHidden","Version","CreatedAtUtc","UpdatedAtUtc","LegacyImported","TransitionNote")
                  SELECT "Id","SellerId","Name","Description","PriceMinor","CategoryId",NULL,NULL,
                    CASE WHEN "Status"='Draft' THEN 'Draft' ELSE 'Unpublished' END,false,"Version","CreatedAtUtc",CURRENT_TIMESTAMP,true,
                    'Перенесено из старого каталога. Проверьте данные, выберите город и состояние вещи, добавьте фото и телефон с согласием на показ, затем опубликуйте объявление.'
                  FROM "Products";
                INSERT INTO "ListingContact" ("ListingId","Phone","ConsentedAtUtc")
                  SELECT "Id",'',NULL FROM "Listings" WHERE "LegacyImported";
                INSERT INTO "ListingImages" ("Id","ListingId","Position")
                  SELECT "Id","ProductId","Position" FROM "ProductImages";
                CREATE FUNCTION neavito_legacy_readonly() RETURNS trigger LANGUAGE plpgsql AS $$
                  BEGIN RAISE EXCEPTION 'Legacy commerce is read-only after classified ads cutover'; END;
                $$;
                CREATE TRIGGER legacy_readonly BEFORE INSERT OR UPDATE OR DELETE OR TRUNCATE ON "Products" FOR EACH STATEMENT EXECUTE FUNCTION neavito_legacy_readonly();
                CREATE TRIGGER legacy_readonly BEFORE INSERT OR UPDATE OR DELETE OR TRUNCATE ON "ProductImages" FOR EACH STATEMENT EXECUTE FUNCTION neavito_legacy_readonly();
                CREATE TRIGGER legacy_readonly BEFORE INSERT OR UPDATE OR DELETE OR TRUNCATE ON "CartItems" FOR EACH STATEMENT EXECUTE FUNCTION neavito_legacy_readonly();
                CREATE TRIGGER legacy_readonly BEFORE INSERT OR UPDATE OR DELETE OR TRUNCATE ON "Orders" FOR EACH STATEMENT EXECUTE FUNCTION neavito_legacy_readonly();
                CREATE TRIGGER legacy_readonly BEFORE INSERT OR UPDATE OR DELETE OR TRUNCATE ON "OrderItems" FOR EACH STATEMENT EXECUTE FUNCTION neavito_legacy_readonly();
                CREATE TRIGGER legacy_readonly BEFORE INSERT OR UPDATE OR DELETE OR TRUNCATE ON "CheckoutBatches" FOR EACH STATEMENT EXECUTE FUNCTION neavito_legacy_readonly();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Cutover cannot be reversed automatically. Keep legacy writes disabled; restore a verified backup to a separate database for recovery.");
        }
    }
}
