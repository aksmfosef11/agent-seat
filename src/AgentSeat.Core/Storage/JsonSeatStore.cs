using System.Text.Json;
using AgentSeat.Core.Abstractions;
using AgentSeat.Core.Models;

namespace AgentSeat.Core.Storage;

public sealed class JsonSeatStore : ISeatStore, IDisposable
{
    private const int CurrentSchemaVersion = 2;
    private const int FirstAutomaticSunshinePort = 47989;
    private const int SunshinePortStep = 100;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonSeatStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
    }

    public async Task<IReadOnlyList<SeatDefinition>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await ReadDocumentAsync(cancellationToken).ConfigureAwait(false)).Seats.ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SeatDefinition?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        var seats = await GetAllAsync(cancellationToken).ConfigureAwait(false);
        return seats.SingleOrDefault(seat => string.Equals(seat.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public async Task UpsertAsync(SeatDefinition seat, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await ReadDocumentAsync(cancellationToken).ConfigureAwait(false);
            var seats = document.Seats
                .Where(existing => !string.Equals(existing.Id, seat.Id, StringComparison.OrdinalIgnoreCase))
                .Append(seat)
                .OrderBy(existing => existing.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            await WriteDocumentAsync(new SeatStoreDocument(CurrentSchemaVersion, seats), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await ReadDocumentAsync(cancellationToken).ConfigureAwait(false);
            var removed = document.Seats.RemoveAll(
                seat => string.Equals(seat.Id, id, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
            {
                return false;
            }

            await WriteDocumentAsync(document, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<SeatStoreDocument> ReadDocumentAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return new SeatStoreDocument(CurrentSchemaVersion, []);
        }

        await using var stream = new FileStream(
            _filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var document = await JsonSerializer.DeserializeAsync<SeatStoreDocument>(
            stream,
            SerializerOptions,
            cancellationToken).ConfigureAwait(false);

        if (document?.Seats is null)
        {
            throw new InvalidDataException($"Seat store '{_filePath}' is empty or malformed.");
        }

        if (document.SchemaVersion == 1)
        {
            return MigrateVersionOne(document);
        }

        if (document.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported seat store schema {document.SchemaVersion}; expected {CurrentSchemaVersion}.");
        }

        return document;
    }

    private static SeatStoreDocument MigrateVersionOne(SeatStoreDocument document)
    {
        var occupied = document.Seats.Select(seat => seat.RdpPort).ToHashSet();
        var migrated = new List<SeatDefinition>(document.Seats.Count);
        foreach (var seat in document.Seats.OrderBy(seat => seat.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (!seat.StreamingEnabled || seat.SunshineBasePort != 0)
            {
                migrated.Add(seat);
                if (seat.StreamingEnabled && seat.SunshineBasePort >= SunshinePorts.MinimumBasePort)
                {
                    occupied.UnionWith(SunshinePorts.FromBasePort(seat.SunshineBasePort).AllPorts);
                }

                continue;
            }

            var assigned = false;
            for (var basePort = FirstAutomaticSunshinePort;
                 basePort <= SunshinePorts.MaximumBasePort;
                 basePort += SunshinePortStep)
            {
                var ports = SunshinePorts.FromBasePort(basePort).AllPorts;
                if (ports.Any(occupied.Contains))
                {
                    continue;
                }

                migrated.Add(seat with { SunshineBasePort = basePort });
                occupied.UnionWith(ports);
                assigned = true;
                break;
            }

            if (!assigned)
            {
                throw new InvalidDataException(
                    $"Could not migrate seat '{seat.Id}': no collision-free Sunshine port family is available.");
            }
        }

        return new SeatStoreDocument(CurrentSchemaVersion, migrated);
    }

    private async Task WriteDocumentAsync(SeatStoreDocument document, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("Seat store path has no parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, document, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed record SeatStoreDocument(int SchemaVersion, List<SeatDefinition> Seats);
}
