using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Wmsfo.Api.Data;

namespace Wmsfo.Api.Tests;

// A128: person.seen_up_to_id (sql.md 3.12) is in the model and in the
// migrations' model snapshot as a nullable bigint with its comment, unindexed.
public class PersonSeenUpToModelTests
{
    [Fact]
    public void Model_snapshot_carries_the_column()
    {
        using var db = NewContext();
        var snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot;
        Assert.NotNull(snapshot);
        var person = snapshot!.Model.FindEntityType("Wmsfo.Api.Data.Person");
        Assert.NotNull(person);
        AssertColumn(person!);
    }

    [Fact]
    public void Context_model_carries_the_column()
    {
        using var db = NewContext();
        var person = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(Person));
        Assert.NotNull(person);
        AssertColumn(person!);
    }

    private static WmsfoDbContext NewContext() =>
        new(new DbContextOptionsBuilder<WmsfoDbContext>()
            .UseNpgsql("Host=localhost;Database=unused")
            .UseSnakeCaseNamingConvention()
            .Options);

    private static void AssertColumn(IReadOnlyEntityType person)
    {
        var column = person.FindProperty("SeenUpToId");
        Assert.NotNull(column);
        Assert.Equal("seen_up_to_id", column!.GetColumnName());
        Assert.Equal("bigint", column.GetColumnType());
        Assert.True(column.IsNullable);
        Assert.Contains("snapshot.event.alerts[].id", column.GetComment() ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain(person.GetIndexes(), i => i.Properties.Contains(column));
    }
}
