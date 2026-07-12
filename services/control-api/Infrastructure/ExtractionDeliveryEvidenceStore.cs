using System.Text.Json;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

public sealed record ExtractionDeliveryEvidence(
    Guid DeliveryId,
    DateTimeOffset CapturedAt,
    IReadOnlyDictionary<string, long> BaselineItemTotals,
    Guid? CommandId = null,
    DateTimeOffset? VerifiedAt = null,
    IReadOnlyDictionary<string, long>? VerifiedItemTotals = null);

public sealed class ExtractionDeliveryEvidenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, ExtractionDeliveryEvidence> _items = [];
    private readonly string _path;

    public ExtractionDeliveryEvidenceStore(
        IOptions<ExtractionPersistenceOptions> options,
        IWebHostEnvironment environment)
    {
        var configured = options.Value.DataDirectory;
        var directory = Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(environment.ContentRootPath, configured));
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "delivery-inventory-evidence.json");
        Load();
    }

    public async Task<ExtractionDeliveryEvidence?> GetAsync(
        Guid deliveryId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _items.TryGetValue(deliveryId, out var evidence)
                ? Clone(evidence)
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ExtractionDeliveryEvidence> ReplaceBaselineAsync(
        Guid deliveryId,
        IReadOnlyDictionary<string, long> baselineItemTotals,
        CancellationToken cancellationToken)
    {
        if (deliveryId == Guid.Empty)
        {
            throw new ArgumentException("Delivery id cannot be empty.", nameof(deliveryId));
        }
        ArgumentNullException.ThrowIfNull(baselineItemTotals);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            _items.TryGetValue(deliveryId, out var existing);
            if (existing?.CommandId is not null)
            {
                throw new InvalidOperationException(
                    "A delivery baseline cannot be replaced after a command has been attached.");
            }
            var evidence = new ExtractionDeliveryEvidence(
                deliveryId,
                DateTimeOffset.UtcNow,
                new Dictionary<string, long>(baselineItemTotals, StringComparer.OrdinalIgnoreCase));
            _items[deliveryId] = evidence;
            try
            {
                await PersistAsync(cancellationToken);
            }
            catch
            {
                if (existing is null)
                {
                    _items.Remove(deliveryId);
                }
                else
                {
                    _items[deliveryId] = existing;
                }
                throw;
            }
            return Clone(evidence);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ExtractionDeliveryEvidence> AttachCommandAsync(
        Guid deliveryId,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (deliveryId == Guid.Empty)
        {
            throw new ArgumentException("Delivery id cannot be empty.", nameof(deliveryId));
        }
        if (commandId == Guid.Empty)
        {
            throw new ArgumentException("Command id cannot be empty.", nameof(commandId));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_items.TryGetValue(deliveryId, out var existing))
            {
                throw new InvalidOperationException(
                    "A delivery command cannot be attached without a persisted inventory baseline.");
            }
            if (existing.CommandId == commandId)
            {
                return Clone(existing);
            }
            if (existing.CommandId is not null)
            {
                throw new InvalidOperationException(
                    "The delivery evidence is already attached to another command.");
            }

            var updated = existing with { CommandId = commandId };
            _items[deliveryId] = updated;
            try
            {
                await PersistAsync(cancellationToken);
            }
            catch
            {
                _items[deliveryId] = existing;
                throw;
            }
            return Clone(updated);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ExtractionDeliveryEvidence> SaveVerificationAsync(
        Guid deliveryId,
        Guid commandId,
        IReadOnlyDictionary<string, long> verifiedItemTotals,
        CancellationToken cancellationToken)
    {
        if (deliveryId == Guid.Empty)
        {
            throw new ArgumentException("Delivery id cannot be empty.", nameof(deliveryId));
        }
        if (commandId == Guid.Empty)
        {
            throw new ArgumentException("Command id cannot be empty.", nameof(commandId));
        }
        ArgumentNullException.ThrowIfNull(verifiedItemTotals);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_items.TryGetValue(deliveryId, out var existing) ||
                existing.CommandId != commandId)
            {
                throw new InvalidOperationException(
                    "A delivery readback can only be saved for its attached command.");
            }
            var updated = existing with
            {
                VerifiedAt = DateTimeOffset.UtcNow,
                VerifiedItemTotals = new Dictionary<string, long>(
                    verifiedItemTotals,
                    StringComparer.OrdinalIgnoreCase)
            };
            _items[deliveryId] = updated;
            try
            {
                await PersistAsync(cancellationToken);
            }
            catch
            {
                _items[deliveryId] = existing;
                throw;
            }
            return Clone(updated);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }
        var bytes = File.ReadAllBytes(_path);
        var items = JsonSerializer.Deserialize<ExtractionDeliveryEvidence[]>(bytes, JsonOptions)
            ?? throw new InvalidDataException("The extraction delivery evidence store is invalid.");
        foreach (var item in items)
        {
            if (item.DeliveryId == Guid.Empty || !_items.TryAdd(item.DeliveryId, Clone(item)))
            {
                throw new InvalidDataException("The extraction delivery evidence store contains duplicate or invalid ids.");
            }
        }
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        var tempPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    _items.Values.OrderBy(item => item.CapturedAt).ToArray(),
                    JsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static ExtractionDeliveryEvidence Clone(ExtractionDeliveryEvidence evidence) =>
        evidence with
        {
            BaselineItemTotals = new Dictionary<string, long>(
                evidence.BaselineItemTotals,
                StringComparer.OrdinalIgnoreCase),
            VerifiedItemTotals = evidence.VerifiedItemTotals is null
                ? null
                : new Dictionary<string, long>(
                    evidence.VerifiedItemTotals,
                    StringComparer.OrdinalIgnoreCase)
        };
}
