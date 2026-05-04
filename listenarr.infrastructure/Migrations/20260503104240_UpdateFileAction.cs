using Listenarr.Application.Models.Enumerations;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Test : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "CompletedFileAction",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: FileAction.Copy,
                oldClrType: typeof(string),
                oldType: "TEXT");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "CompletedFileAction",
                table: "ApplicationSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "Copy",
                oldClrType: typeof(int),
                oldType: "INTEGER");
        }
    }
}
