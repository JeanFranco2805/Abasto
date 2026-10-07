using System.IO;
using System.Text.Json;

namespace Abasto.Services;

public static class UnitCatalogService
{
    private static readonly string[] BuiltInUnits = ["UND", "KG", "G", "L", "ML", "PAQ", "CAJA", "DOC"];
    private static readonly SemaphoreSlim FileLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static bool IsBuiltIn(string unit) =>
        BuiltInUnits.Contains(unit, StringComparer.OrdinalIgnoreCase);

    public static async Task<List<string>> GetUnitsAsync()
    {
        await FileLock.WaitAsync();
        try
        {
            return await LoadUnlockedAsync();
        }
        finally
        {
            FileLock.Release();
        }
    }

    public static async Task<string> AddUnitAsync(string value)
    {
        var unit = Normalize(value);
        if (unit.Length is < 1 or > 8 || unit.Any(character => !char.IsAsciiLetterOrDigit(character)))
            throw new InvalidOperationException("Usa un código de 1 a 8 letras o números, por ejemplo BOLSA o M2.");

        await FileLock.WaitAsync();
        try
        {
            var units = await LoadUnlockedAsync();
            if (units.Contains(unit, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("Esa unidad ya está registrada.");

            units.Add(unit);
            await SaveUnlockedAsync(units);
            return unit;
        }
        finally
        {
            FileLock.Release();
        }
    }

    public static async Task RemoveUnitAsync(string value)
    {
        var unit = Normalize(value);
        if (IsBuiltIn(unit))
            throw new InvalidOperationException("Las unidades incluidas no se pueden eliminar.");
        if (await PosService.IsProductUnitInUseAsync(unit))
            throw new InvalidOperationException("No se puede eliminar: hay productos o ventas que usan esta unidad.");

        await FileLock.WaitAsync();
        try
        {
            var units = await LoadUnlockedAsync();
            units.RemoveAll(existing => string.Equals(existing, unit, StringComparison.OrdinalIgnoreCase));
            await SaveUnlockedAsync(units);
        }
        finally
        {
            FileLock.Release();
        }
    }

    private static async Task<List<string>> LoadUnlockedAsync()
    {
        var path = GetFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
            await SaveUnlockedAsync([.. BuiltInUnits]);

        List<string> stored;
        try
        {
            await using var stream = File.OpenRead(path);
            stored = await JsonSerializer.DeserializeAsync<List<string>>(stream) ?? [];
        }
        catch (JsonException)
        {
            stored = [];
        }

        return BuiltInUnits.Concat(stored)
            .Select(Normalize)
            .Where(unit => unit.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task SaveUnlockedAsync(IEnumerable<string> units)
    {
        var path = GetFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var stored = units
            .Select(Normalize)
            .Where(unit => unit.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var temporaryPath = path + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, stored, JsonOptions);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static string GetFilePath()
    {
        if (string.IsNullOrWhiteSpace(App.DatabasePath))
            throw new InvalidOperationException("La base local todavía no está inicializada.");
        return Path.Combine(Path.GetDirectoryName(App.DatabasePath)!, "units.json");
    }

    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
}
