using Wmsfo.Api.Contracts;

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
