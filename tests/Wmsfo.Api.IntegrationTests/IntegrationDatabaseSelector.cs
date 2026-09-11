namespace Wmsfo.Api.IntegrationTests;

public enum IntegrationDatabaseSource
{
    SuppliedConnectionString,
    LibpqEnvironment,
    Testcontainers,
}

public readonly record struct IntegrationDatabaseChoice(IntegrationDatabaseSource Source, string? ConnectionString);

public static class IntegrationDatabaseSelector
{
    // api.md 21: `WMSFO_TEST_DB_CONNECTION` when set wins; otherwise, when
    // libpq's `PGHOST` is set, build a connection string from `PGHOST` /
    // `PGPORT` / `PGUSER` / `PGPASSWORD` / `PGDATABASE` (defaults `5432`,
    // `postgres`, empty, `postgres`); otherwise Testcontainers.
    public static IntegrationDatabaseChoice Choose(Func<string, string?> readEnv)
    {
        var supplied = readEnv("WMSFO_TEST_DB_CONNECTION");
        if (!string.IsNullOrEmpty(supplied))
        {
            return new IntegrationDatabaseChoice(IntegrationDatabaseSource.SuppliedConnectionString, supplied);
        }

        var host = readEnv("PGHOST");
        if (!string.IsNullOrEmpty(host))
        {
            var port = Nonempty(readEnv("PGPORT"), "5432");
            var user = Nonempty(readEnv("PGUSER"), "postgres");
            var password = readEnv("PGPASSWORD") ?? "";
            var database = Nonempty(readEnv("PGDATABASE"), "postgres");
            var connectionString = $"Host={host};Port={port};Username={user};Password={password};Database={database}";
            return new IntegrationDatabaseChoice(IntegrationDatabaseSource.LibpqEnvironment, connectionString);
        }

        return new IntegrationDatabaseChoice(IntegrationDatabaseSource.Testcontainers, null);
    }

    private static string Nonempty(string? value, string fallback) =>
        string.IsNullOrEmpty(value) ? fallback : value;
}
