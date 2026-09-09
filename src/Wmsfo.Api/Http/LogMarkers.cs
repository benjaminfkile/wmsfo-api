namespace Wmsfo.Api.Http;

// api.md 16 / platform.md 10: every marker constant is a property named
// `marker` on the log line. CloudWatch metric filters key on the string, so
// these are the exact tokens the docs list. Central to keep the marker table
// and the code in sync; ChangedFileRules test also asserts this file is the
// only definer.
public static class LogMarkers
{
    public const string LivePutFailed = "wmsfo_live_put_failed";
    public const string PublishFailed = "wmsfo_publish_failed";
    public const string LeaderGained = "wmsfo_leader_gained";
    public const string LeaderLost = "wmsfo_leader_lost";
    public const string SnapshotWriteFailed = "wmsfo_snapshot_write_failed";
    public const string OutboxExhausted = "wmsfo_outbox_exhausted";
    public const string AlertExhausted = "wmsfo_alert_exhausted";
    public const string HealthUnavailable = "wmsfo_health_unavailable";
    public const string MediaWriteFailed = "wmsfo_media_write_failed";
    public const string ContentPublished = "wmsfo_content_published";
    public const string IconLibraryWritten = "wmsfo_icon_library_written";

    // The list every marker test iterates. Order matches api.md 16.
    public static readonly string[] All =
    {
        LivePutFailed,
        PublishFailed,
        LeaderGained,
        LeaderLost,
        SnapshotWriteFailed,
        OutboxExhausted,
        AlertExhausted,
        HealthUnavailable,
        MediaWriteFailed,
        ContentPublished,
        IconLibraryWritten,
    };
}
