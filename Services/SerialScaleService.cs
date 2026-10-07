using System.IO;
using System.IO.Ports;
using System.Text.RegularExpressions;
using Abasto.Domain;

namespace Abasto.Services;

public sealed partial class SerialScaleService : IScale
{
    public async Task<decimal> ReadWeightAsync(CancellationToken cancellationToken = default)
    {
        var settings = await PeripheralSettingsService.LoadAsync();
        if (string.IsNullOrWhiteSpace(settings.ScalePort))
            throw new InvalidOperationException("Configura el puerto COM de la báscula.");
        return await Task.Run(() => ReadWeight(settings), cancellationToken);
    }

    private static decimal ReadWeight(PeripheralSettings settings)
    {
        using var port = new SerialPort(settings.ScalePort.Trim(), settings.ScaleBaudRate)
        {
            ReadTimeout = 3000,
            NewLine = "\n"
        };
        port.Open();
        var response = port.ReadLine().Trim();
        var match = WeightPattern().Match(response);
        if (!match.Success || !decimal.TryParse(match.Groups["value"].Value.Replace(',', '.'),
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,
                out var weight))
            throw new InvalidDataException($"La báscula respondió en un formato no reconocido: {response}");
        var suffix = response[(match.Index + match.Length)..].Trim().ToLowerInvariant();
        if (suffix.StartsWith("g", StringComparison.Ordinal))
            weight /= 1000m;
        else if (suffix.StartsWith("lb", StringComparison.Ordinal))
            weight *= 0.45359237m;
        if (weight < 0)
            throw new InvalidDataException("La báscula devolvió un peso negativo.");
        return decimal.Round(weight, 3, MidpointRounding.AwayFromZero);
    }

    [GeneratedRegex(@"[-+]?\d+(?:[\.,]\d+)?")]
    private static partial Regex WeightPattern();
}
