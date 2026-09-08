using Npgsql;

namespace Wmsfo.Api.IntegrationTests;

// The six role pages (contracts 4.5 Pages preamble) are seeded on first boot in
// A14. A13's tests need them to exist so we seed only the page rows here; the
// starter content stack is A14's concern.
internal static class RolePageSeed
{
    private static readonly (string Slug, string Role, string Title)[] RolePages =
    {
        ("no-event",  "no_event",  "Off season"),
        ("planned",   "planned",   "Planned"),
        ("scheduled", "scheduled", "Scheduled"),
        ("live",      "live",      "Live"),
        ("ended",     "ended",     "Ended"),
        ("cancelled", "cancelled", "Cancelled"),
    };

    public static async Task EnsureAsync(NpgsqlConnection conn)
    {
        await using var check = new NpgsqlCommand("select count(*) from page;", conn);
        var n = Convert.ToInt64(await check.ExecuteScalarAsync());
        if (n > 0) return;
        foreach (var (slug, role, title) in RolePages)
        {
            await using var ins = new NpgsqlCommand(@"
insert into page (slug, title, nav_label, nav_position, is_hidden, role, created_by, updated_by)
values ($1, $2, null, 0, false, $3, 'seed', 'seed');", conn);
            ins.Parameters.Add(new NpgsqlParameter { Value = slug });
            ins.Parameters.Add(new NpgsqlParameter { Value = title });
            ins.Parameters.Add(new NpgsqlParameter { Value = role });
            await ins.ExecuteNonQueryAsync();
        }
    }
}
