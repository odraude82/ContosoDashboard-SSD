using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContosoDashboard.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentShares : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DocumentShares",
                columns: table => new
                {
                    DocumentShareId = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DocumentId = table.Column<int>(type: "INTEGER", nullable: false),
                    SharedByUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    RecipientUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    RecipientDepartment = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    SharedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentShares", x => x.DocumentShareId);
                    table.CheckConstraint("CK_DocumentShares_RecipientTarget", "(\"RecipientUserId\" IS NOT NULL AND \"RecipientDepartment\" IS NULL) OR (\"RecipientUserId\" IS NULL AND \"RecipientDepartment\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_DocumentShares_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "DocumentId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DocumentShares_Users_RecipientUserId",
                        column: x => x.RecipientUserId,
                        principalTable: "Users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentShares_Users_SharedByUserId",
                        column: x => x.SharedByUserId,
                        principalTable: "Users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentShares_DocumentId_RevokedAtUtc",
                table: "DocumentShares",
                columns: new[] { "DocumentId", "RevokedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentShares_RecipientDepartment_RevokedAtUtc",
                table: "DocumentShares",
                columns: new[] { "RecipientDepartment", "RevokedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentShares_RecipientUserId_RevokedAtUtc",
                table: "DocumentShares",
                columns: new[] { "RecipientUserId", "RevokedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentShares_SharedByUserId",
                table: "DocumentShares",
                column: "SharedByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocumentShares");
        }
    }
}
