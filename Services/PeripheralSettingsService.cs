using System.IO;
using System.Text.Json;

namespace Abasto.Services;

public sealed record PeripheralSettings(string PrinterName, string ScalePort, int ScaleBaudRate)
{
    public string StoreName { get; init; } = "";
    public static PeripheralSettings Empty { get; } = new("", "", 9600);
}

public static class PeripheralSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SupermercadoPOS", "peripherals.json");

    public static async Task<PeripheralSettings> LoadAsync()
    {
        if (!File.Exists(SettingsPath))
            return PeripheralSettings.Empty;
        await using var input = File.OpenRead(SettingsPath);
        return await JsonSerializer.DeserializeAsync<PeripheralSettings>(input, JsonOptions) ?? PeripheralSettings.Empty;
    }

    public static async Task SaveAsync(PeripheralSettings settings)
    {
        if (settings.ScaleBaudRate is < 300 or > 115200)
            throw new InvalidOperationException("La velocidad de la báscula debe estar entre 300 y 115200 baudios.");
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temporaryPath = SettingsPath + ".tmp";
        await using (var output = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(output, settings, JsonOptions);
        File.Move(temporaryPath, SettingsPath, true);
    }
}
