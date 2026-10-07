using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Abasto.Backend.Services;

public sealed record FiscalProviderResult(
    string Provider,
    string DocumentNumber,
    string ProviderDocumentId,
    string Status,
    string ResponsePayload);

public sealed class ElectronicInvoicingProvider(IConfiguration configuration)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public string ProviderName => configuration["ElectronicInvoicing:ProviderName"]?.Trim() ?? "";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ProviderName)
        && Uri.TryCreate(configuration["ElectronicInvoicing:BaseUrl"], UriKind.Absolute, out var baseUri)
        && baseUri.Scheme == Uri.UriSchemeHttps
        && !string.IsNullOrWhiteSpace(configuration["ElectronicInvoicing:AccessToken"]);

    public Task<FiscalProviderResult> IssueInvoiceAsync(
        string clientId,
        long saleId,
        string salePayload,
        CancellationToken cancellationToken) => SendAsync(
            configuration["ElectronicInvoicing:InvoicePath"] ?? "invoices",
            $"{clientId}-sale-{saleId}-invoice",
            new
            {
                documentType = "invoice",
                clientId,
                saleId,
                sale = JsonDocument.Parse(salePayload).RootElement
            },
            cancellationToken);

    public Task<FiscalProviderResult> IssueCreditNoteAsync(
        string clientId,
        long saleId,
        long returnId,
        string salePayload,
        string returnPayload,
        CancellationToken cancellationToken) => SendAsync(
            configuration["ElectronicInvoicing:CreditNotePath"] ?? "credit-notes",
            $"{clientId}-return-{returnId}-credit-note",
            new
            {
                documentType = "credit-note",
                clientId,
                saleId,
                returnId,
                sale = JsonDocument.Parse(salePayload).RootElement,
                saleReturn = JsonDocument.Parse(returnPayload).RootElement
            },
            cancellationToken);

    private async Task<FiscalProviderResult> SendAsync(
        string path,
        string idempotencyKey,
        object payload,
        CancellationToken cancellationToken)
    {
        var provider = ProviderName;
        var baseUrl = configuration["ElectronicInvoicing:BaseUrl"]?.Trim() ?? "";
        var token = configuration["ElectronicInvoicing:AccessToken"]?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(provider)
            || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("El backend no tiene un proveedor de facturación HTTPS configurado.");

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, path.TrimStart('/')))
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        using var response = await client.SendAsync(request, cancellationToken);
        var responsePayload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"El proveedor {provider} respondió {(int)response.StatusCode}: {Trim(responsePayload)}");

        using var document = JsonDocument.Parse(responsePayload);
        var root = document.RootElement;
        var documentNumber = ReadString(root, "documentNumber", "invoiceNumber", "number", "folio", "uuid");
        if (string.IsNullOrWhiteSpace(documentNumber))
            throw new InvalidDataException($"El proveedor {provider} no devolvió el número o UUID del documento: {Trim(responsePayload)}");
        var providerDocumentId = ReadString(root, "providerDocumentId", "documentId", "id") ?? documentNumber;
        return new FiscalProviderResult(provider, documentNumber, providerDocumentId, "Emitida", responsePayload);
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var property in element.EnumerateObject())
        {
            if (names.Any(name => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                    return property.Value.GetString();
                if (property.Value.ValueKind == JsonValueKind.Number)
                    return property.Value.ToString();
            }
        }
        return null;
    }

    private static string Trim(string value) => value.Length <= 500 ? value : value[..500];
}
