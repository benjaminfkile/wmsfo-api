using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Config;
using Wmsfo.Api.Contracts.Dtos;
using Wmsfo.Api.Endpoints;
using Wmsfo.Api.Http;
using Wmsfo.Api.Icons;
using Wmsfo.Api.Node;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Content;

// api.md 11a.3 / sql.md 8.19: the publish transaction. Reads the working set in
// document order, validates at the publish level, resolves references, hashes
// the canonical document bytes, inserts content_version, prunes to newest 50,
// builds the snapshot inside the same transaction, PUTs the snapshot object,
// and updates the snapshot row. Fires the live-object write after commit.
//
// Errors surface as ApiException:
//   422 content_invalid + details.problems (ProblemRef[])
//   409 content_unchanged when the working set hashes to the newest version
//   502 snapshot_write_failed when the snapshot PUT fails (frame rolls back)
public sealed class Publisher
{
    public const int MaxRetainedVersions = 50;

    private readonly WmsfoConnectionStrings _connections;
    private readonly DocumentBuilder _documentBuilder;
    private readonly SchemaValidator _validator;
    private readonly ReferenceChecker _referenceChecker;
    private readonly SnapshotBuilder _snapshotBuilder;
    private readonly LiveObjectWriter? _liveObjectWriter;
    private readonly NodeStateService? _nodeState;
    private readonly ILogger<Publisher> _logger;

    public Publisher(
        WmsfoConnectionStrings connections,
        DocumentBuilder documentBuilder,
        SchemaValidator validator,
        IconLibrary? iconLibrary,
        SnapshotBuilder snapshotBuilder,
        LiveObjectWriter? liveObjectWriter,
        NodeStateService? nodeState,
        ILogger<Publisher> logger)
    {
        _connections = connections;
        _documentBuilder = documentBuilder;
        _validator = validator;
        _referenceChecker = new ReferenceChecker(iconLibrary);
        _snapshotBuilder = snapshotBuilder;
        _liveObjectWriter = liveObjectWriter;
        _nodeState = nodeState;
        _logger = logger;
    }

    // POST /admin/content/publish. Runs the full publish transaction and returns
    // the persisted content_version row info + the new snapshot version.
    public async Task<PublishResult> PublishAsync(string publishedBy, string? label, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connections.App);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return await RunPublishAsync(conn, publishedBy, label, buildSnapshot: true, ct).ConfigureAwait(false);
    }

    // sql.md 8.16 step 3: first boot publish. When content_version has no rows
    // yet, publish the current working set (starter content) with
    // `published_by = 'seed'` and `label = 'Starter content'`. Does NOT build the
    // snapshot — SnapshotBootstrap does that in step 4. Uses the migrate
    // connection so it runs under the same role as the migrator (advisory lock
    // held by DatabaseMigrator).
    public async Task<PublishResult?> EnsureVersionOneAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        long existingId;
        await using (var check = new NpgsqlCommand(
            "select coalesce(max(id), 0) from content_version;", conn))
        {
            existingId = Convert.ToInt64(await check.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
        }
        if (existingId > 0) return null;

        return await RunPublishAsync(conn, publishedBy: "seed", label: "Starter content",
            buildSnapshot: false, ct).ConfigureAwait(false);
    }

    // Publish transaction body. sql.md 8.19 minus the snapshot rebuild when
    // buildSnapshot is false (first-boot step 3). The snapshot row lock is only
    // taken when we intend to rebuild.
    private async Task<PublishResult> RunPublishAsync(NpgsqlConnection conn,
        string publishedBy, string? label, bool buildSnapshot, CancellationToken ct)
    {
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        if (buildSnapshot)
        {
            await using var lockRow = new NpgsqlCommand(
                "select id from snapshot where id = 1 for update;", conn, tx);
            await lockRow.ExecuteScalarAsync(ct).ConfigureAwait(false);
        }

        var load = await _documentBuilder.LoadAsync(conn, tx, includeHidden: false, ct).ConfigureAwait(false);

        // Publish-level structural + semantic validation.
        var problems = new List<ProblemRefDto>();
        // Site settings.
        foreach (var p in _validator.ValidateSiteSettings(load.SettingsData, ValidationLevel.Publish))
        {
            problems.Add(new ProblemRefDto { Path = "/settings" + p.Path, Message = p.Message });
        }
        foreach (var page in load.WorkingSet.Pages)
        {
            foreach (var section in page.Sections)
            {
                foreach (var p in _validator.ValidateSectionData(section.Kind, section.Data, ValidationLevel.Publish))
                {
                    problems.Add(new ProblemRefDto
                    {
                        Path = "/data" + p.Path,
                        Message = p.Message,
                        PageId = page.Id,
                        SectionId = section.Id,
                    });
                }
                foreach (var p in _validator.ValidatePresentation(section.Presentation, ValidationLevel.Publish))
                {
                    problems.Add(new ProblemRefDto
                    {
                        Path = "/presentation" + p.Path,
                        Message = p.Message,
                        PageId = page.Id,
                        SectionId = section.Id,
                    });
                }
                foreach (var item in section.Items)
                {
                    foreach (var p in _validator.ValidateItemData(section.Kind, item.Data, ValidationLevel.Publish))
                    {
                        problems.Add(new ProblemRefDto
                        {
                            Path = "/data" + p.Path,
                            Message = p.Message,
                            PageId = page.Id,
                            SectionId = section.Id,
                            ItemId = item.Id,
                        });
                    }
                }
            }
        }
        // Semantic (reference) checks.
        var refProblems = await _referenceChecker.CheckAsync(load.WorkingSet, conn, tx, ct).ConfigureAwait(false);
        problems.AddRange(refProblems);

        if (problems.Count > 0)
        {
            throw new ApiException(StatusCodes.Status422UnprocessableEntity,
                ApiErrorCodes.ContentInvalid,
                "the working set has publish-level problems",
                new ContentInvalidDetails(problems));
        }

        // Hash the canonical bytes of the document.
        var documentBytes = CanonicalJson.SerializeToUtf8Bytes(load.Document);
        var documentJson = System.Text.Encoding.UTF8.GetString(documentBytes);
        var sha = CanonicalJson.Sha256Hex(documentBytes);

        // 409 content_unchanged when the newest row hashes to the same bytes.
        await using (var check = new NpgsqlCommand(
            "select sha256 from content_version order by id desc limit 1;", conn, tx))
        {
            var r = await check.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (r is string existing && string.Equals(existing.Trim(), sha, StringComparison.Ordinal))
            {
                throw new ApiException(StatusCodes.Status409Conflict,
                    ApiErrorCodes.ContentUnchanged,
                    "the working set is identical to the newest published version");
            }
        }

        // Collect every media id the document references (MediaRef, media icons,
        // background media, inline media icons, site settings icons).
        var mediaIds = DocumentBuilder.CollectReferencedMediaIds(load.WorkingSet);

        long newVersionId;
        DateTimeOffset publishedAt;
        await using (var insert = new NpgsqlCommand(@"
insert into content_version (document, sha256, media_ids, label, published_by)
values ($1::jsonb, $2, $3, $4, $5)
returning id, published_at;", conn, tx))
        {
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = documentJson });
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Char, Value = sha });
            insert.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid,
                Value = mediaIds,
            });
            insert.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlDbType.Text,
                Value = string.IsNullOrEmpty(label) ? DBNull.Value : (object)label,
            });
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = publishedBy });
            await using var reader = await insert.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await reader.ReadAsync(ct).ConfigureAwait(false);
            newVersionId = reader.GetInt64(0);
            publishedAt = reader.GetFieldValue<DateTimeOffset>(1);
        }

        // Prune older rows outside the newest 50.
        await using (var prune = new NpgsqlCommand(@"
delete from content_version
where id not in (select id from content_version order by id desc limit $1);", conn, tx))
        {
            prune.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = MaxRetainedVersions });
            await prune.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        long? snapshotVersion = null;
        string? snapshotKey = null;
        string? snapshotUrl = null;
        if (buildSnapshot)
        {
            var built = await _snapshotBuilder.BuildAndPutAsync(conn, tx, ct).ConfigureAwait(false);
            snapshotVersion = await SnapshotBuilder.ApplySnapshotRowUpdateAsync(conn, tx, built, ct).ConfigureAwait(false);
            snapshotKey = built.Key;
            snapshotUrl = built.Url;
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);

        if (snapshotVersion is long v)
        {
            _nodeState?.RecordWroteVersion(v);
            if (_liveObjectWriter is not null)
            {
                // Fire-and-forget: the response does not wait for the live-object write.
                _ = _liveObjectWriter.WriteFromStateAsync("admin", CancellationToken.None);
            }
        }

        // Count pages/sections from the built document.
        var (pageCount, sectionCount) = CountPagesAndSections(load.Document);

        var info = new ContentVersionInfoDto
        {
            Id = newVersionId,
            Sha256 = sha,
            Label = string.IsNullOrEmpty(label) ? null : label,
            PublishedBy = publishedBy,
            PublishedAt = publishedAt,
            PageCount = pageCount,
            SectionCount = sectionCount,
        };
        return new PublishResult(info, snapshotVersion, snapshotKey, snapshotUrl);
    }

    private static (int Pages, int Sections) CountPagesAndSections(ContentDocument doc)
    {
        var pages = doc.Pages?.Count ?? 0;
        var sections = 0;
        if (doc.Pages is not null)
        {
            foreach (var p in doc.Pages) sections += p.Sections?.Count ?? 0;
        }
        return (pages, sections);
    }
}

// Publish result carried out to the endpoint handler. The snapshot version is
// null on the first-boot publish (SnapshotBootstrap does step 4 next).
public sealed record PublishResult(
    ContentVersionInfoDto Version,
    long? SnapshotVersion,
    string? SnapshotKey,
    string? SnapshotUrl);

// api.md 11a.3: `content_invalid` carries a `problems` array of ProblemRef entries
// so the panel can link each problem to its page/section/item.
public sealed record ContentInvalidDetails(IReadOnlyList<ProblemRefDto> Problems);
