using Microsoft.EntityFrameworkCore;
using Wmsfo.Api.Data;
using Wmsfo.Api.Icons;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.IntegrationTests;

// A6: EnsureWrittenAsync uses icon_library_state to decide whether to PUT the
// library and reports back whether it wrote. First call on a fresh migration
// writes and updates the row; a second call is a no-op.
public sealed class IconLibraryWriteTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public IconLibraryWriteTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private DbContextOptions<WmsfoDbContext> Options() =>
        new DbContextOptionsBuilder<WmsfoDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

    [Fact]
    public async Task First_run_writes_and_second_run_is_a_no_op()
    {
        // Migrate a fresh database and load the compiled icon library.
        await using (var db = new WmsfoDbContext(Options()))
        {
            await db.Database.MigrateAsync();
        }
        var library = IconLibrary.Load(TestPaths.IconsDir, "https://cdn.example");

        var storeDir = Path.Combine(Path.GetTempPath(), "wmsfo-icon-store-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            var store = new LocalObjectStore(storeDir, "http://localhost:5000");

            bool firstWrote;
            await using (var db = new WmsfoDbContext(Options()))
            {
                firstWrote = await library.EnsureWrittenAsync(store, db);
            }
            Assert.True(firstWrote);

            // The store now holds every icon under icons/.
            foreach (var file in library.Files)
            {
                var head = await store.HeadObjectAsync(file.Key);
                Assert.NotNull(head);
                Assert.Equal(IconLibrary.SvgContentType, head!.ContentType);
                Assert.Equal(IconLibrary.ImmutableCacheControl, head.CacheControl);
                var body = await store.GetObjectAsync(file.Key);
                Assert.NotNull(body);
                Assert.Equal(file.Bytes, body!.Bytes);
            }

            // Row updated with the library hash.
            await using (var db = new WmsfoDbContext(Options()))
            {
                var state = await db.IconLibraryState.SingleAsync(x => x.Id == 1);
                Assert.Equal(library.LibraryHash, state.LibrarySha256?.Trim());
                Assert.NotNull(state.WrittenAt);
            }

            // Second call short-circuits.
            bool secondWrote;
            await using (var db = new WmsfoDbContext(Options()))
            {
                secondWrote = await library.EnsureWrittenAsync(store, db);
            }
            Assert.False(secondWrote);
        }
        finally
        {
            if (Directory.Exists(storeDir)) Directory.Delete(storeDir, recursive: true);
        }
    }
}
