using Wmsfo.Api.Contracts;
using Wmsfo.Api.Objects;

if (args.Length > 0)
{
    switch (args[0])
    {
        case "export-openapi":
            var openapiPath = args.Length > 1 ? args[1] : "contracts/openapi.json";
            await OpenApiExport.WriteAsync(openapiPath);
            return;
        case "export-contracts":
            var root = args.Length > 1 ? args[1] : "contracts";
            Directory.CreateDirectory(root);
            SchemaExport.WriteAll(Path.Combine(root, "schema"));
            FixtureExport.WriteAll(Path.Combine(root, "fixtures"));
            // Starter content mirrors the content-document fixture byte-for-byte; the seed loader reads this file.
            await File.WriteAllBytesAsync(
                Path.Combine(root, "starter-content.json"),
                CanonicalJson.SerializeToUtf8Bytes(FixtureData.BuildContentDocument()));
            await File.WriteAllTextAsync(Path.Combine(root, "admin-thresholds.json"), AdminThresholds.ToJson());
            await OpenApiExport.WriteAsync(Path.Combine(root, "openapi.json"));
            return;
    }
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();
var app = builder.Build();
EndpointStubs.MapAll(app);
app.MapOpenApi();
app.Run();
