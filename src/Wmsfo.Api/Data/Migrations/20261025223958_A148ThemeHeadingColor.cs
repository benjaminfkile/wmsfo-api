using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    // overlay.headingColor (contracts 1.3, 4.0): every tracker_theme row
    // whose overlay lacks the key gains it, by key for the seeded themes and
    // #ffb300 for any other row; the column comment names the eight keys.
    // Down removes the key.
    public partial class A148ThemeHeadingColor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<JsonDocument>(
                name: "overlay",
                table: "tracker_theme",
                type: "jsonb",
                nullable: false,
                comment: "{ routeColor, routeOpacity, arrowColor, timeLabelBg, timeLabelFg, timeLabelOpacity, userColor, headingColor }. Every key required; headingColor paints Santa's heading beam on the tracker, apart from the route.",
                oldClrType: typeof(JsonDocument),
                oldType: "jsonb",
                oldComment: "{ routeColor, routeOpacity, arrowColor, timeLabelBg, timeLabelFg, timeLabelOpacity, userColor }.");

            migrationBuilder.Sql(@"
update tracker_theme
set overlay = overlay || jsonb_build_object('headingColor',
      case key
        when 'light' then '#ffb300'
        when 'dark' then '#ffb300'
        when 'standard' then '#ffb300'
        when 'expedition' then '#ffb300'
        when 'blizzard' then '#ffb300'
        when 'charcoal' then '#69f0ae'
        when 'night' then '#ffb300'
        when 'nebula' then '#69f0ae'
        else '#ffb300'
      end)
where not overlay ? 'headingColor';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
update tracker_theme
set overlay = overlay - 'headingColor'
where overlay ? 'headingColor';");

            migrationBuilder.AlterColumn<JsonDocument>(
                name: "overlay",
                table: "tracker_theme",
                type: "jsonb",
                nullable: false,
                comment: "{ routeColor, routeOpacity, arrowColor, timeLabelBg, timeLabelFg, timeLabelOpacity, userColor }.",
                oldClrType: typeof(JsonDocument),
                oldType: "jsonb",
                oldComment: "{ routeColor, routeOpacity, arrowColor, timeLabelBg, timeLabelFg, timeLabelOpacity, userColor, headingColor }. Every key required; headingColor paints Santa's heading beam on the tracker, apart from the route.");
        }
    }
}
