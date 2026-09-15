using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class A38LocationEventPosition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A38: a position is stored at most once per event. Before creating
            // the unique index, remove existing duplicates keeping the lowest
            // seq per (event_id, lat, lng), so the index builds on dev and prod
            // data as they are.
            migrationBuilder.Sql(@"
delete from location l
using location keep
where l.event_id = keep.event_id
  and l.lat = keep.lat
  and l.lng = keep.lng
  and keep.seq = (
    select min(seq) from location
    where event_id = l.event_id and lat = l.lat and lng = l.lng
  )
  and l.id <> keep.id;");

            migrationBuilder.CreateIndex(
                name: "location_event_position",
                table: "location",
                columns: new[] { "event_id", "lat", "lng" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "location_event_position",
                table: "location");
        }
    }
}
