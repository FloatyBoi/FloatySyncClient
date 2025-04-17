using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FloatySyncClient.Migrations
{
    /// <inheritdoc />
    public partial class AddedIsDirectory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "isDirectory",
                table: "Files",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "isDirectory",
                table: "Files");
        }
    }
}
