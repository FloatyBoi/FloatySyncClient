using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FloatySyncClient.Migrations
{
    /// <inheritdoc />
    public partial class UpdatedGroupTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "IdOnServer",
                table: "Groups",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSyncUtc",
                table: "Groups",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "LocalFolder",
                table: "Groups",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IdOnServer",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "LastSyncUtc",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "LocalFolder",
                table: "Groups");
        }
    }
}
