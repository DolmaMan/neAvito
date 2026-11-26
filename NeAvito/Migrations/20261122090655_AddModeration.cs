using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NeAvito.Migrations
{
    /// <inheritdoc />
    public partial class AddModeration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ModerationReason",
                table: "Listings",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "BlockReason",
                table: "AspNetUsers",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true
            );

            migrationBuilder.AddColumn<DateTime>(
                name: "BlockedAtUtc",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "BlockedById",
                table: "AspNetUsers",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true
            );

            migrationBuilder.CreateTable(
                name: "ListingReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ListingId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReporterId = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(
                        type: "character varying(40)",
                        maxLength: 40,
                        nullable: false
                    ),
                    Comment = table.Column<string>(
                        type: "character varying(2000)",
                        maxLength: 2000,
                        nullable: false
                    ),
                    Status = table.Column<string>(
                        type: "character varying(20)",
                        maxLength: 20,
                        nullable: false
                    ),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Decision = table.Column<string>(
                        type: "character varying(40)",
                        maxLength: 40,
                        nullable: true
                    ),
                    DecisionReason = table.Column<string>(
                        type: "character varying(1000)",
                        maxLength: 1000,
                        nullable: true
                    ),
                    ModeratorId = table.Column<string>(type: "text", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ListingReports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ListingReports_AspNetUsers_ModeratorId",
                        column: x => x.ModeratorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "FK_ListingReports_AspNetUsers_ReporterId",
                        column: x => x.ReporterId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "FK_ListingReports_Listings_ListingId",
                        column: x => x.ListingId,
                        principalTable: "Listings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "ModerationAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: true),
                    ListingId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorId = table.Column<string>(type: "text", nullable: false),
                    ModeratorId = table.Column<string>(type: "text", nullable: false),
                    Action = table.Column<string>(
                        type: "character varying(40)",
                        maxLength: 40,
                        nullable: false
                    ),
                    Reason = table.Column<string>(
                        type: "character varying(1000)",
                        maxLength: 1000,
                        nullable: false
                    ),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModerationAudits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ModerationAudits_AspNetUsers_AuthorId",
                        column: x => x.AuthorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "FK_ModerationAudits_AspNetUsers_ModeratorId",
                        column: x => x.ModeratorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "FK_ModerationAudits_ListingReports_ReportId",
                        column: x => x.ReportId,
                        principalTable: "ListingReports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "FK_ModerationAudits_Listings_ListingId",
                        column: x => x.ListingId,
                        principalTable: "Listings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_ListingReports_ListingId_ReporterId",
                table: "ListingReports",
                columns: new[] { "ListingId", "ReporterId" },
                unique: true,
                filter: "\"Status\" = 'Pending'"
            );

            migrationBuilder.CreateIndex(
                name: "IX_ListingReports_ModeratorId",
                table: "ListingReports",
                column: "ModeratorId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_ListingReports_ReporterId",
                table: "ListingReports",
                column: "ReporterId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_ListingReports_Status_CreatedAtUtc_Id",
                table: "ListingReports",
                columns: new[] { "Status", "CreatedAtUtc", "Id" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_ModerationAudits_AuthorId_CreatedAtUtc_Id",
                table: "ModerationAudits",
                columns: new[] { "AuthorId", "CreatedAtUtc", "Id" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_ModerationAudits_ListingId_CreatedAtUtc_Id",
                table: "ModerationAudits",
                columns: new[] { "ListingId", "CreatedAtUtc", "Id" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_ModerationAudits_ModeratorId",
                table: "ModerationAudits",
                column: "ModeratorId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_ModerationAudits_ReportId",
                table: "ModerationAudits",
                column: "ReportId"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException(
                "Moderation history must be preserved. Roll forward or restore a verified backup to a separate database."
            );
        }
    }
}
