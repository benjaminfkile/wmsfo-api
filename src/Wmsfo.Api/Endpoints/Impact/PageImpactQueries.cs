using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Contracts.Dtos;

namespace Wmsfo.Api.Endpoints.Impact;

// api.md 5b: pages cascade their sections and section_items (FK Cascade).
// A role page's delete needs `roleTo` (400 role_needs_page when missing); the
// preview surfaces the warning sentence and the caller passes `roleTo` on the
// delete. Places and qr_codes that pointed to the page get their opens
// unlinked (FK SetNull), so those groups appear as `unlinks`.
public static class PageImpactQueries
{
    public static async Task<(DeleteImpactDto Impact, string? Role)> PreviewAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, long id, CancellationToken ct)
    {
        var impact = new DeleteImpactDto();
        string? role = null;
        bool exists = false;
        await using (var read = new NpgsqlCommand(
            "select role from page where id = $1;", conn, tx))
        {
            read.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
            var r = await read.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (r is not null && r is not DBNull)
            {
                exists = true;
                role = (string)r;
            }
        }
        if (!exists) return (impact, null);

        if (!string.Equals(role, "none", System.StringComparison.Ordinal))
        {
            impact.Warnings.Add(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                ImpactHelpers.PageRoleWarning, role));
        }

        var parameters = ImpactHelpers.LongId(id);

        var sections = await ImpactHelpers.CountAndNamesAsync(
            conn, tx, "section", "section", "kind", "page_id = $1",
            parameters, ct).ConfigureAwait(false);
        if (sections is not null) impact.Deletes.Add(sections);

        int itemCount = 0;
        await using (var cmd = new NpgsqlCommand(@"
select count(*)::int from section_item i
join section s on s.id = i.section_id
where s.page_id = $1;", conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
            var r = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            itemCount = r is int i ? i : System.Convert.ToInt32(r);
        }
        if (itemCount > 0)
        {
            impact.Deletes.Add(new ImpactGroupDto
            {
                Entity = "section_item", Count = itemCount, Names = new List<string>(),
            });
        }

        var places = await ImpactHelpers.CountAndNamesAsync(
            conn, tx, "place", "place", "name", "opens_page_id = $1",
            parameters, ct).ConfigureAwait(false);
        if (places is not null) impact.Unlinks.Add(places);

        var qrCodes = await ImpactHelpers.CountAndNamesAsync(
            conn, tx, "qr_code", "qr_code", "tag", "opens_page_id = $1",
            parameters, ct).ConfigureAwait(false);
        if (qrCodes is not null) impact.Unlinks.Add(qrCodes);

        return (impact, role);
    }

    // ApplyAsync moves the role to `roleTo` (must be a `none` page) before the
    // FK cascade removes the sections. The caller re-checks after the update
    // to guard against a race that clears roleTo between preview and apply.
    public static async Task ApplyAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long id, string? role, long? roleTo, CancellationToken ct)
    {
        if (string.Equals(role, "none", System.StringComparison.Ordinal) || role is null) return;
        if (roleTo is null) return;
        // The endpoint validates roleTo and the page's role before calling.
        // Clear the old role first, then set it on roleTo, so the partial
        // unique index page_one_per_role is satisfied.
        await using (var clear = new NpgsqlCommand(
            "update page set role = 'none', updated_at = now() where id = $1;", conn, tx))
        {
            clear.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
            await clear.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await using (var set = new NpgsqlCommand(
            "update page set role = $1, updated_at = now() where id = $2;", conn, tx))
        {
            set.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = role });
            set.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = roleTo.Value });
            await set.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }
}
