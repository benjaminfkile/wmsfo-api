namespace Wmsfo.Api.Contracts;

// Sets `maxItems` on the exported JSON Schema of a list property (SchemaExport).
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class SchemaMaxItemsAttribute : Attribute
{
    public SchemaMaxItemsAttribute(int maxItems) => MaxItems = maxItems;

    public int MaxItems { get; }
}
