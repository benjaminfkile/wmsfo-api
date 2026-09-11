namespace Wmsfo.Api.IntegrationTests;

// api.md 21: the three-way choice used by PostgresFixture. Each branch is
// exercised by stubbing the environment reader.
public class IntegrationDatabaseSelectorTests
{
    private static Func<string, string?> Env(params (string Name, string? Value)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Name, p => p.Value);
        return name => map.TryGetValue(name, out var value) ? value : null;
    }

    [Fact]
    public void Supplied_connection_string_wins()
    {
        var env = Env(
            ("WMSFO_TEST_DB_CONNECTION", "Host=db.example;Port=5432;Username=u;Password=p;Database=d"),
            ("PGHOST", "127.0.0.1"),
            ("PGPORT", "6543"),
            ("PGUSER", "runner"),
            ("PGPASSWORD", "runnerpw"),
            ("PGDATABASE", "runnerdb"));

        var choice = IntegrationDatabaseSelector.Choose(env);

        Assert.Equal(IntegrationDatabaseSource.SuppliedConnectionString, choice.Source);
        Assert.Equal("Host=db.example;Port=5432;Username=u;Password=p;Database=d", choice.ConnectionString);
    }

    [Fact]
    public void Libpq_environment_is_used_when_PGHOST_is_set()
    {
        var env = Env(
            ("PGHOST", "127.0.0.1"),
            ("PGPORT", "6543"),
            ("PGUSER", "runner"),
            ("PGPASSWORD", "runnerpw"),
            ("PGDATABASE", "runnerdb"));

        var choice = IntegrationDatabaseSelector.Choose(env);

        Assert.Equal(IntegrationDatabaseSource.LibpqEnvironment, choice.Source);
        Assert.Equal("Host=127.0.0.1;Port=6543;Username=runner;Password=runnerpw;Database=runnerdb", choice.ConnectionString);
    }

    [Fact]
    public void Libpq_defaults_fill_in_missing_variables()
    {
        var env = Env(("PGHOST", "127.0.0.1"));

        var choice = IntegrationDatabaseSelector.Choose(env);

        Assert.Equal(IntegrationDatabaseSource.LibpqEnvironment, choice.Source);
        Assert.Equal("Host=127.0.0.1;Port=5432;Username=postgres;Password=;Database=postgres", choice.ConnectionString);
    }

    [Fact]
    public void Testcontainers_when_no_environment_is_set()
    {
        var choice = IntegrationDatabaseSelector.Choose(_ => null);

        Assert.Equal(IntegrationDatabaseSource.Testcontainers, choice.Source);
        Assert.Null(choice.ConnectionString);
    }

    [Fact]
    public void Empty_supplied_connection_falls_through_to_libpq()
    {
        var env = Env(
            ("WMSFO_TEST_DB_CONNECTION", ""),
            ("PGHOST", "127.0.0.1"));

        var choice = IntegrationDatabaseSelector.Choose(env);

        Assert.Equal(IntegrationDatabaseSource.LibpqEnvironment, choice.Source);
    }

    [Fact]
    public void Empty_PGHOST_falls_through_to_Testcontainers()
    {
        var env = Env(("PGHOST", ""));

        var choice = IntegrationDatabaseSelector.Choose(env);

        Assert.Equal(IntegrationDatabaseSource.Testcontainers, choice.Source);
    }
}
