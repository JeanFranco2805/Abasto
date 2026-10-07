using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Abasto.Domain;

namespace Abasto.Services;

public static class FiscalInvoicingClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<FiscalProviderStatus> GetProviderStatusAsync(CancellationToken cancellationToken = default)
    {
        var settings = await BackendSettingsService.LoadAsync();
        using var client = CreateClient(settings);
        using var response = await client.GetAsync("api/fiscal/status", cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"El backend respondió {(int)response.StatusCode}: {ReadError(text)}");
        return JsonSerializer.Deserialize<FiscalProviderStatus>(text, JsonOptions)
            ?? throw new InvalidDataException("El backend respondió sin el estado de facturación.");
    }

    public static Task<FiscalProviderResult> IssueInvoiceAsync(long saleId, CancellationToken cancellationToken = default) =>
        SendAsync("api/fiscal/invoices", saleId, null, cancellationToken);

    public static Task<FiscalProviderResult> IssueCreditNoteAsync(long saleId, long returnId, CancellationToken cancellationToken = default) =>
        SendAsync("api/fiscal/credit-notes", saleId, returnId, cancellationToken);

    private static async Task<FiscalProviderResult> SendAsync(string path, long saleId, long? returnId, CancellationToken cancellationToken)
    {
        var syncResult = await BackendSyncService.SynchronizePendingAsync(cancellationToken);
        if (!syncResult.IsConnected)
            throw new InvalidOperationException($"La facturación electrónica requiere el backend conectado. {syncResult.Message}");

        var settings = await BackendSettingsService.LoadAsync();
        using var client = CreateClient(settings);
        using var response = await client.PostAsJsonAsync(path,
            new FiscalRequest(settings.ClientId, saleId, returnId),
            JsonOptions, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"El backend respondió {(int)response.StatusCode}: {ReadError(responseText)}");
        return JsonSerializer.Deserialize<FiscalProviderResult>(responseText, JsonOptions)
            ?? throw new InvalidDataException("El backend respondió sin datos del documento fiscal.");
    }

    private static string ReadError(string responseText)
    {
        try
        {
            using var document = JsonDocument.Parse(responseText);
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.Name.Equals("detail", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("error", StringComparison.OrdinalIgnoreCase))
                    return property.Value.ToString();
        }
        catch (JsonException)
        {
        }
        return responseText.Length <= 500 ? responseText : responseText[..500];
    }

    private static HttpClient CreateClient(BackendSettings settings)
    {
        if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(settings.ApiKey))
            throw new InvalidOperationException("Configura la URL y la clave de la API central antes de facturar.");
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(50),
            BaseAddress = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/")
        };
        client.DefaultRequestHeaders.Add("X-Api-Key", settings.ApiKey);
        return client;
    }

    private sealed record FiscalRequest(string ClientId, long SaleId, long? ReturnId);
}
