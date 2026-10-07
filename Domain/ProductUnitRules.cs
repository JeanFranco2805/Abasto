namespace SupermercadoPOS.Domain;

public static class ProductUnitRules
{
    private static readonly HashSet<string> FractionalUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        "KG", "G", "L", "ML"
    };

    public static bool AllowsFractionalQuantity(string? unit) =>
        unit is not null && FractionalUnits.Contains(unit.Trim());
}
