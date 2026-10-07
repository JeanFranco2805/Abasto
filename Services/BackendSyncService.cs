using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SupermercadoPOS.Domain;

namespace SupermercadoPOS.Services;

public sealed record BackendSyncResult(bool IsConfigured, bool IsConnected, int SentCount, int PendingCount, string Message);

public static class BackendSyncService
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly SemaphoreSlim SyncLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<BackendSyncResult> SynchronizePendingAsync(CancellationToken cancellationToken = default)
    {
        if (!await SyncLock.WaitAsync(0, cancellationToken))
            return new BackendSyncResult(true, false, 0, await PosService.GetPendingSyncCountAsync(), "Sincronización en curso.");

        try
        {
            var settings = await BackendSettingsService.LoadAsync();
            var pendingCount = await PosService.GetPendingSyncCountAsync();
            if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var baseUri)
                || baseUri.Scheme is not ("http" or "https")
                || string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                return new BackendSyncResult(false, false, 0, pendingCount, "Configura la URL y la clave de la API central.");
            }

            using var requestClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15), BaseAddress = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/") };
            requestClient.DefaultRequestHeaders.Add("X-Api-Key", settings.ApiKey);

            using var summaryResponse = await requestClient.GetAsync("api/sync/summary", cancellationToken);
            if (!summaryResponse.IsSuccessStatusCode)
            {
                var message = summaryResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? "La API rechazó la clave configurada."
                    : $"La API respondió {(int)summaryResponse.StatusCode} ({summaryResponse.ReasonPhrase}).";
                return new BackendSyncResult(true, false, 0, pendingCount, message);
            }

            if (pendingCount == 0)
                return new BackendSyncResult(true, true, 0, 0, "Conectado; no hay eventos pendientes.");

            var settingsClientId = settings.ClientId;
            var sentCount = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                List<SyncQueueItem> batch;
                await using (var db = App.CreateDbContext())
                {
                    batch = await db.SyncQueue.Where(item => item.Status == "Pendiente")
                        .OrderBy(item => item.Id).Take(100).ToListAsync(cancellationToken);
                }

                if (batch.Count == 0)
                    break;

                var body = new SyncBatchRequest(settingsClientId, batch.Select(item => new SyncEventRequest(
                    item.Id, item.CreatedAtUtc, item.EntityType, item.EntityId, item.Operation, item.Payload)).ToList());
                using var response = await requestClient.PostAsJsonAsync("api/sync/events", body, JsonOptions, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new HttpRequestException($"La API respondió {(int)response.StatusCode}: {TrimResponse(responseText)}");
                }

                var acknowledgment = await response.Content.ReadFromJsonAsync<SyncBatchResponse>(JsonOptions, cancellationToken)
                    ?? throw new InvalidDataException("La API respondió sin confirmación de sincronización.");
                if (acknowledgment.Accepted + acknowledgment.Duplicates != batch.Count)
                    throw new InvalidDataException("La API no confirmó todos los eventos del lote.");

                await using var updateDb = App.CreateDbContext();
                var ids = batch.Select(item => item.Id).ToArray();
                var storedItems = await updateDb.SyncQueue.Where(item => ids.Contains(item.Id)).ToListAsync(cancellationToken);
                foreach (var item in storedItems)
                    item.Status = "Enviado";
                await updateDb.SaveChangesAsync(cancellationToken);
                sentCount += storedItems.Count;
            }

            var remaining = await PosService.GetPendingSyncCountAsync();
            return new BackendSyncResult(true, true, sentCount, remaining,
                sentCount == 0 ? "Conectado; no hay eventos pendientes." : $"Conectado; se enviaron {sentCount:N0} evento(s).");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await IncrementPendingAttemptsAsync(cancellationToken);
            var pending = await PosService.GetPendingSyncCountAsync();
            return new BackendSyncResult(true, false, 0, pending, $"Sin conexión con la central: {TrimResponse(exception.Message)}");
        }
        finally
        {
            SyncLock.Release();
        }
    }

    private static async Task IncrementPendingAttemptsAsync(CancellationToken cancellationToken)
    {
        await using var db = App.CreateDbContext();
        var batch = await db.SyncQueue.Where(item => item.Status == "Pendiente")
            .OrderBy(item => item.Id).Take(100).ToListAsync(cancellationToken);
        foreach (var item in batch)
            item.Attempts++;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string TrimResponse(string value) =>
        value.Length <= 240 ? value : value[..240];

    private sealed record SyncBatchRequest(string ClientId, List<SyncEventRequest> Events);
    private sealed record SyncEventRequest(long LocalEventId, DateTime CreatedAtUtc, string EntityType, string EntityId, string Operation, string Payload);
    private sealed record SyncBatchResponse(int Accepted, int Duplicates);
}
