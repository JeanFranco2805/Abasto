using System.IO;
using System.Text.Json;

namespace SupermercadoPOS.Services;

public sealed record BackendSettings(string BaseUrl, string ApiKey, string ClientId);

public static class BackendSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SupermercadoPOS", "backend.json");

    public static async Task<BackendSettings> LoadAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        if (File.Exists(SettingsPath))
        {
            await using var input = File.OpenRead(SettingsPath);
            var stored = await JsonSerializer.DeserializeAsync<BackendSettings>(input, JsonOptions);
            if (stored is not null && !string.IsNullOrWhiteSpace(stored.ClientId))
                return stored;
        }

        var initial = new BackendSettings("", "", Guid.NewGuid().ToString("N"));
        await SaveAsync(initial);
        return initial;
    }

    public static async Task<BackendSettings> SaveAsync(string baseUrl, string apiKey)
    {
        var previous = await LoadAsync();
        return await SaveAsync(previous with
        {
            BaseUrl = baseUrl.Trim().TrimEnd('/'),
            ApiKey = apiKey.Trim()
        });
    }

    private static async Task<BackendSettings> SaveAsync(BackendSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temporaryPath = SettingsPath + ".tmp";
        await using (var output = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(output, settings, JsonOptions);
        File.Move(temporaryPath, SettingsPath, true);
        return settings;
    }
}
