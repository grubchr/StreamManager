using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StreamManager.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddKsqlStreamName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "KsqlStreamName",
                table: "StreamDefinitions",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KsqlStreamName",
                table: "StreamDefinitions");
        }
    }
}
