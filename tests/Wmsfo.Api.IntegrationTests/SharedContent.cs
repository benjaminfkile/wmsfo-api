using Wmsfo.Api.Content;

namespace Wmsfo.Api.IntegrationTests;

// JsonSchema.Net registers schemas by $id in a process-global registry, so
// KindRegistry.Load fails on any second call. Every test host loads through
// this shared holder to keep the load count to one per test process.
internal static class SharedContent
{
    public static readonly KindRegistry Registry = KindRegistry.Load(TestPaths.ContractsDir);
    public static readonly SchemaValidator Validator = new(Registry);
}
