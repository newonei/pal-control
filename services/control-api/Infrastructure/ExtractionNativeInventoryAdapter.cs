using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PalControl.ControlApi.Infrastructure;

public sealed record ExtractionNativeInventorySlotSnapshot(
    [property: JsonPropertyName("slotIndex")] int SlotIndex,
    [property: JsonPropertyName("itemId")] string ItemId,
    [property: JsonPropertyName("quantity")] int Quantity,
    [property: JsonPropertyName("dynamicCreatedWorldId")] string DynamicCreatedWorldId,
    [property: JsonPropertyName("dynamicLocalIdInCreatedWorld")] string DynamicLocalIdInCreatedWorld,
    [property: JsonPropertyName("hasDynamicItemData")] bool HasDynamicItemData,
    [property: JsonPropertyName("corruptionProgress")] double CorruptionProgress,
    [property: JsonPropertyName("corruptionProgressBits")] uint CorruptionProgressBits);

public sealed record ExtractionNativeInventoryContainerSnapshot(
    [property: JsonPropertyName("containerKind")] string ContainerKind,
    [property: JsonPropertyName("containerId")] string ContainerId,
    [property: JsonPropertyName("slots")] IReadOnlyList<ExtractionNativeInventorySlotSnapshot> Slots);

public sealed record ExtractionNativeInventoryQuoteSnapshot(
    int SnapshotVersion,
    string OwnerPlayerUid,
    DateTimeOffset ObservedAt,
    IReadOnlyList<ExtractionNativeInventoryContainerSnapshot> Containers,
    string SnapshotHash);

public sealed record ExtractionNativeConsumeItem(
    [property: JsonPropertyName("itemId")] string ItemId,
    [property: JsonPropertyName("quantity")] int Quantity);

public sealed record ExtractionNativeConsumePayload(
    [property: JsonPropertyName("snapshotVersion")] int SnapshotVersion,
    [property: JsonPropertyName("ownerPlayerId")] string OwnerPlayerId,
    [property: JsonPropertyName("items")] IReadOnlyList<ExtractionNativeConsumeItem> Items,
    [property: JsonPropertyName("expectedContainers")]
    IReadOnlyList<ExtractionNativeInventoryContainerSnapshot> ExpectedContainers);

[JsonConverter(typeof(JsonStringEnumConverter<ExtractionNativeConsumeDisposition>))]
public enum ExtractionNativeConsumeDisposition
{
    Succeeded,
    Failed,
    Uncertain
}

public sealed record ExtractionNativeConsumeItemEvidence(
    string ItemId,
    int RequestedQuantity,
    long? BeforeQuantity,
    long? AfterQuantity,
    long? ActualConsumed);

public sealed record ExtractionNativeConsumeReceipt(
    ExtractionNativeConsumeDisposition Disposition,
    string RequestHash,
    string ResponseHash,
    string NativeState,
    long ObservedRevision,
    bool Applied,
    bool SnapshotMatched,
    bool AggregateVerified,
    bool PersistenceVerified,
    IReadOnlyList<ExtractionNativeConsumeItemEvidence> Items,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset RecordedAt);

public sealed record ExtractionNativeConsumeOutcome(ExtractionNativeConsumeReceipt Receipt);

public interface IExtractionNativeInventoryAdapter
{
    bool StableSettlementAvailable { get; }

    Task<ExtractionNativeInventoryQuoteSnapshot> CaptureQuoteSnapshotAsync(
        string serverId,
        string playerUid,
        CancellationToken cancellationToken);

    ExtractionNativeConsumePayload CreateConsumePayload(
        ExtractionNativeInventoryQuoteSnapshot snapshot,
        IReadOnlyList<ExtractionLootLine> items);

    string ComputeRequestHash(
        Guid runId,
        string serverId,
        ExtractionNativeConsumePayload payload);

    Task<ExtractionNativeConsumeOutcome> ConsumeAsync(
        string serverId,
        ExtractionNativeConsumePayload payload,
        string requestHash,
        string idempotencyKey,
        CancellationToken cancellationToken);
}

public sealed class ExtractionNativeInventoryAdapter : IExtractionNativeInventoryAdapter
{
    public const int CurrentSnapshotVersion = 1;
    public const string StableConsumeCapability = "inventory.consume";
    private static readonly string[] RequiredContainerKinds = ["common", "dropSlot", "food"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly NativeBridgeState _state;
    private readonly INativeBridgeCommandTransport _bridge;

    public ExtractionNativeInventoryAdapter(
        NativeBridgeState state,
        INativeBridgeCommandTransport bridge)
    {
        _state = state;
        _bridge = bridge;
    }

    public bool StableSettlementAvailable
    {
        get
        {
            var snapshot = _state.GetSnapshot();
            return snapshot.Connected &&
                snapshot.RuntimeIdentityVerified &&
                snapshot.WriteEnabled &&
                snapshot.Capabilities.Contains("inventory.probe") &&
                snapshot.Capabilities.Contains(StableConsumeCapability);
        }
    }

    public async Task<ExtractionNativeInventoryQuoteSnapshot> CaptureQuoteSnapshotAsync(
        string serverId,
        string playerUid,
        CancellationToken cancellationToken)
    {
        var normalizedPlayerUid = NormalizeRequiredGuid(playerUid, "playerUid", allowEmpty: false);
        EnsureStableCapabilities();

        NativeBridgeResult result;
        try
        {
            result = await _bridge.SendCommandAsync(
                serverId,
                "inventory.probe",
                new { },
                "capture immutable extraction quote inventory snapshot",
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or OperationCanceledException)
        {
            throw new ExtractionModeException(
                "NATIVE_INVENTORY_PROBE_UNAVAILABLE",
                "Native 背包探针不可用，资源报价已安全拒绝。",
                StatusCodes.Status503ServiceUnavailable);
        }

        if (!string.Equals(result.State, "succeeded", StringComparison.Ordinal) ||
            result.Data is not JsonElement data ||
            data.ValueKind != JsonValueKind.Object)
        {
            throw new ExtractionModeException(
                result.Error?.Code ?? "NATIVE_INVENTORY_PROBE_FAILED",
                result.Error?.Message ?? "Native 背包探针未返回可用快照。",
                StatusCodes.Status503ServiceUnavailable);
        }

        return ParseQuoteSnapshot(data, normalizedPlayerUid);
    }

    public ExtractionNativeConsumePayload CreateConsumePayload(
        ExtractionNativeInventoryQuoteSnapshot snapshot,
        IReadOnlyList<ExtractionLootLine> items)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(items);
        var recalculatedHash = ExtractionNativeInventoryCanonicalizer.Hash(snapshot);
        if (snapshot.SnapshotVersion != CurrentSnapshotVersion ||
            !string.Equals(snapshot.SnapshotHash, recalculatedHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The persisted Native inventory snapshot hash is invalid.");
        }
        if (items.Count is < 1 or > 64 ||
            items.Any(item => !IsValidSlotItem(item.ItemId, item.Quantity) ||
                string.Equals(item.ItemId, "None", StringComparison.Ordinal)) ||
            items.Select(item => item.ItemId).Distinct(StringComparer.Ordinal).Count() != items.Count ||
            items.Sum(item => (long)item.Quantity) > 16_000_000)
        {
            throw new InvalidDataException("The persisted Native consume item list is invalid.");
        }
        return new ExtractionNativeConsumePayload(
            CurrentSnapshotVersion,
            snapshot.OwnerPlayerUid,
            items.Select(item => new ExtractionNativeConsumeItem(item.ItemId, item.Quantity)).ToArray(),
            snapshot.Containers.Select(CloneContainer).ToArray());
    }

    public string ComputeRequestHash(
        Guid runId,
        string serverId,
        ExtractionNativeConsumePayload payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ArgumentNullException.ThrowIfNull(payload);
        var canonical = string.Join('\n',
            "inventory.consume",
            serverId.Trim(),
            runId.ToString("N"),
            JsonSerializer.Serialize(payload, JsonOptions));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public async Task<ExtractionNativeConsumeOutcome> ConsumeAsync(
        string serverId,
        ExtractionNativeConsumePayload payload,
        string requestHash,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!IsSha256(requestHash))
        {
            throw new ArgumentException("requestHash must be a lowercase SHA-256 hash.", nameof(requestHash));
        }

        if (!StableSettlementAvailable)
        {
            return OutcomeWithoutNativeResult(
                ExtractionNativeConsumeDisposition.Failed,
                requestHash,
                "NATIVE_INVENTORY_CONSUME_CAPABILITY_MISSING",
                "Native Bridge 未声明稳定 inventory.consume 能力；experimental 能力不能用于经济入账。");
        }

        NativeBridgeResult result;
        try
        {
            result = await _bridge.SendCommandAsync(
                serverId,
                "inventory.consume",
                payload,
                "atomically consume persisted extraction quote inventory",
                cancellationToken,
                expectedRevision: 0,
                idempotencyKey: idempotencyKey);
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or OperationCanceledException)
        {
            return OutcomeWithoutNativeResult(
                ExtractionNativeConsumeDisposition.Uncertain,
                requestHash,
                "NATIVE_INVENTORY_CONSUME_TRANSPORT_UNCERTAIN",
                "Native 扣物请求的最终结果无法确认，禁止自动重试或入账。");
        }

        return ClassifyResult(result, payload, requestHash);
    }

    private void EnsureStableCapabilities()
    {
        var snapshot = _state.GetSnapshot();
        if (!snapshot.Connected)
        {
            throw new ExtractionModeException(
                "NATIVE_ECONOMY_ADAPTER_NOT_CONNECTED",
                "Native 经济 adapter 未连接。",
                StatusCodes.Status503ServiceUnavailable);
        }
        if (!snapshot.Capabilities.Contains("inventory.probe") ||
            !snapshot.Capabilities.Contains(StableConsumeCapability) ||
            !snapshot.RuntimeIdentityVerified ||
            !snapshot.WriteEnabled)
        {
            throw new ExtractionModeException(
                "NATIVE_INVENTORY_CONSUME_CAPABILITY_MISSING",
                "Native Bridge 必须同时声明 inventory.probe 与稳定 inventory.consume；experimental 能力不被接受。",
                StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static ExtractionNativeInventoryQuoteSnapshot ParseQuoteSnapshot(
        JsonElement data,
        string normalizedPlayerUid)
    {
        if (!TryGetBoolean(data, "mappingReady", out var mappingReady) || !mappingReady ||
            TryGetBoolean(data, "truncated", out var rootTruncated) && rootTruncated ||
            !data.TryGetProperty("inventories", out var inventories) ||
            inventories.ValueKind != JsonValueKind.Array)
        {
            throw InvalidProbe("Native 背包映射未就绪或探针结果被截断。");
        }

        var matches = inventories.EnumerateArray()
            .Where(inventory => inventory.ValueKind == JsonValueKind.Object &&
                TryGetBoolean(inventory, "ownerOnline", out var ownerOnline) &&
                ownerOnline &&
                inventory.TryGetProperty("ownerPlayerUId", out var owner) &&
                owner.ValueKind == JsonValueKind.String &&
                TryNormalizeGuid(owner.GetString(), allowEmpty: false, out var candidate) &&
                string.Equals(candidate, normalizedPlayerUid, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new ExtractionModeException(
                "NATIVE_PLAYER_INVENTORY_NOT_UNIQUE",
                "Native 探针未能唯一解析当前世界 PlayerUID 的已加载背包。",
                StatusCodes.Status409Conflict);
        }

        var inventory = matches[0];
        if (!inventory.TryGetProperty("containers", out var containersElement) ||
            containersElement.ValueKind != JsonValueKind.Array)
        {
            throw InvalidProbe("Native 探针没有返回背包容器数组。");
        }

        Dictionary<string, ExtractionNativeInventoryContainerSnapshot> selected =
            new(StringComparer.Ordinal);
        foreach (var container in containersElement.EnumerateArray())
        {
            if (container.ValueKind != JsonValueKind.Object ||
                !TryGetString(container, "kind", out var kind) ||
                !RequiredContainerKinds.Contains(kind, StringComparer.Ordinal))
            {
                continue;
            }
            if (selected.ContainsKey(kind))
            {
                throw InvalidProbe($"Native 探针重复返回容器 {kind}。");
            }
            selected[kind] = ParseContainer(container, kind);
        }
        if (selected.Count != RequiredContainerKinds.Length ||
            RequiredContainerKinds.Any(kind => !selected.ContainsKey(kind)) ||
            selected.Values.Select(container => container.ContainerId)
                .Distinct(StringComparer.Ordinal).Count() != RequiredContainerKinds.Length)
        {
            throw InvalidProbe("Native 探针必须完整返回 common、dropSlot 与 food 的三个唯一容器。");
        }

        var observedAt = data.TryGetProperty("observedAt", out var observedAtElement) &&
            observedAtElement.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(
                observedAtElement.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var parsedObservedAt)
            ? parsedObservedAt.ToUniversalTime()
            : DateTimeOffset.UtcNow;
        var ordered = RequiredContainerKinds.Select(kind => selected[kind]).ToArray();
        var unhashed = new ExtractionNativeInventoryQuoteSnapshot(
            CurrentSnapshotVersion,
            normalizedPlayerUid,
            observedAt,
            ordered,
            string.Empty);
        return unhashed with { SnapshotHash = ExtractionNativeInventoryCanonicalizer.Hash(unhashed) };
    }

    private static ExtractionNativeInventoryContainerSnapshot ParseContainer(
        JsonElement container,
        string kind)
    {
        if (!TryGetBoolean(container, "resolved", out var resolved) || !resolved ||
            TryGetBoolean(container, "truncated", out var truncated) && truncated ||
            !TryGetString(container, "containerId", out var rawContainerId) ||
            !TryNormalizeGuid(rawContainerId, allowEmpty: false, out var containerId) ||
            !TryGetInt32(container, "slotCount", out var slotCount) ||
            slotCount is < 0 or > 256 ||
            !container.TryGetProperty("slots", out var slotsElement) ||
            slotsElement.ValueKind != JsonValueKind.Array ||
            slotsElement.GetArrayLength() != slotCount)
        {
            throw InvalidProbe($"Native 容器 {kind} 未解析、被截断或槽位元数据不完整。");
        }

        List<ExtractionNativeInventorySlotSnapshot> slots = new(slotCount);
        HashSet<int> slotIndexes = [];
        foreach (var slot in slotsElement.EnumerateArray())
        {
            if (slot.ValueKind != JsonValueKind.Object ||
                !TryGetInt32(slot, "slotIndex", out var slotIndex) || slotIndex < 0 ||
                !slotIndexes.Add(slotIndex) ||
                !(TryGetString(slot, "staticItemId", out var itemId) ||
                  TryGetString(slot, "itemId", out itemId)) ||
                !(TryGetInt32(slot, "stackCount", out var quantity) ||
                  TryGetInt32(slot, "quantity", out quantity)) ||
                !IsValidSlotItem(itemId, quantity) ||
                !TryGetString(slot, "dynamicCreatedWorldId", out var rawCreatedWorldId) ||
                !TryNormalizeGuid(rawCreatedWorldId, allowEmpty: true, out var createdWorldId) ||
                !TryGetString(slot, "dynamicLocalIdInCreatedWorld", out var rawLocalId) ||
                !TryNormalizeGuid(rawLocalId, allowEmpty: true, out var localId) ||
                !TryGetBoolean(slot, "hasDynamicItemData", out var hasDynamicItemData) ||
                !TryGetDouble(slot, "corruptionProgress", out var corruptionProgress) ||
                !double.IsFinite(corruptionProgress) ||
                !float.IsFinite((float)corruptionProgress) ||
                !TryGetUInt32(slot, "corruptionProgressBits", out var corruptionProgressBits) ||
                BitConverter.SingleToUInt32Bits((float)corruptionProgress) !=
                    corruptionProgressBits)
            {
                throw InvalidProbe($"Native 容器 {kind} 包含空槽引用或不完整的精确槽位元数据。");
            }
            slots.Add(new ExtractionNativeInventorySlotSnapshot(
                slotIndex,
                itemId,
                quantity,
                createdWorldId,
                localId,
                hasDynamicItemData,
                corruptionProgress,
                corruptionProgressBits));
        }
        return new ExtractionNativeInventoryContainerSnapshot(kind, containerId, slots.ToArray());
    }

    private static ExtractionNativeConsumeOutcome ClassifyResult(
        NativeBridgeResult result,
        ExtractionNativeConsumePayload payload,
        string requestHash)
    {
        var data = result.Data is JsonElement value && value.ValueKind == JsonValueKind.Object
            ? value
            : (JsonElement?)null;
        var applied = data is JsonElement objectData &&
            TryGetBoolean(objectData, "applied", out var appliedValue) && appliedValue;
        var snapshotMatched = data is JsonElement snapshotData &&
            TryGetBoolean(snapshotData, "snapshotMatched", out var snapshotValue) && snapshotValue;
        var persistenceVerified = data is JsonElement persistenceData &&
            TryGetBoolean(persistenceData, "persistenceVerified", out var persistenceValue) && persistenceValue;
        var aggregateVerified = data is JsonElement aggregateData &&
            (TryGetBoolean(aggregateData, "aggregateVerified", out var aggregateValue) && aggregateValue ||
             aggregateData.TryGetProperty("settlement", out var settlement) &&
             settlement.ValueKind == JsonValueKind.Object &&
             TryGetBoolean(settlement, "liveAggregateVerified", out var liveAggregate) && liveAggregate);
        var evidence = data is JsonElement itemsData
            ? ParseEvidence(itemsData)
            : Array.Empty<ExtractionNativeConsumeItemEvidence>();

        var exactEvidence = EvidenceMatches(payload.Items, evidence);
        var disposition = result.State switch
        {
            "succeeded" when applied && snapshotMatched && aggregateVerified &&
                persistenceVerified && exactEvidence => ExtractionNativeConsumeDisposition.Succeeded,
            "failed" when !applied && RollbackIsSafe(data) => ExtractionNativeConsumeDisposition.Failed,
            _ => ExtractionNativeConsumeDisposition.Uncertain
        };
        var errorCode = result.Error?.Code;
        var errorMessage = result.Error?.Message;
        if (string.Equals(result.State, "succeeded", StringComparison.Ordinal) &&
            disposition == ExtractionNativeConsumeDisposition.Uncertain)
        {
            errorCode = "NATIVE_INVENTORY_CONSUME_EVIDENCE_INVALID";
            errorMessage = "Native 返回成功，但持久化、完整快照或逐行扣除证据不满足入账条件。";
        }
        var responseHash = Convert.ToHexStringLower(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions)));
        var receipt = new ExtractionNativeConsumeReceipt(
            disposition,
            requestHash,
            responseHash,
            result.State,
            result.ObservedRevision,
            applied,
            snapshotMatched,
            aggregateVerified,
            persistenceVerified,
            evidence,
            errorCode,
            errorMessage,
            DateTimeOffset.UtcNow);
        return new ExtractionNativeConsumeOutcome(receipt);
    }

    private static IReadOnlyList<ExtractionNativeConsumeItemEvidence> ParseEvidence(JsonElement data)
    {
        if (!data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        List<ExtractionNativeConsumeItemEvidence> evidence = [];
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !TryGetString(item, "itemId", out var itemId) ||
                !TryGetInt32(item, "requestedQuantity", out var requestedQuantity))
            {
                return [];
            }
            evidence.Add(new ExtractionNativeConsumeItemEvidence(
                itemId,
                requestedQuantity,
                TryGetInt64(item, "beforeQuantity", out var before) ? before : null,
                TryGetInt64(item, "afterQuantity", out var after) ? after : null,
                TryGetInt64(item, "actualConsumed", out var actual) ? actual : null));
        }
        return evidence;
    }

    private static bool EvidenceMatches(
        IReadOnlyList<ExtractionNativeConsumeItem> requested,
        IReadOnlyList<ExtractionNativeConsumeItemEvidence> evidence)
    {
        if (evidence.Count != requested.Count ||
            evidence.Select(item => item.ItemId).Distinct(StringComparer.Ordinal).Count() != evidence.Count)
        {
            return false;
        }
        var byId = evidence.ToDictionary(item => item.ItemId, StringComparer.Ordinal);
        return requested.All(item =>
            byId.TryGetValue(item.ItemId, out var actual) &&
            actual.RequestedQuantity == item.Quantity &&
            actual.ActualConsumed == item.Quantity &&
            actual.BeforeQuantity is long before &&
            actual.AfterQuantity is long after &&
            before >= item.Quantity &&
            after >= 0 &&
            before - after == item.Quantity);
    }

    private static bool RollbackIsSafe(JsonElement? data)
    {
        if (data is not JsonElement value)
        {
            return true;
        }
        if (!value.TryGetProperty("rollback", out var rollback) ||
            rollback.ValueKind != JsonValueKind.Object)
        {
            return true;
        }
        return !TryGetBoolean(rollback, "attempted", out var attempted) || !attempted ||
            TryGetBoolean(rollback, "verified", out var verified) && verified;
    }

    private static ExtractionNativeConsumeOutcome OutcomeWithoutNativeResult(
        ExtractionNativeConsumeDisposition disposition,
        string requestHash,
        string errorCode,
        string errorMessage)
    {
        var canonical = $"{disposition}|{requestHash}|{errorCode}";
        return new ExtractionNativeConsumeOutcome(new ExtractionNativeConsumeReceipt(
            disposition,
            requestHash,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))),
            disposition == ExtractionNativeConsumeDisposition.Failed ? "failed" : "uncertain",
            0,
            Applied: false,
            SnapshotMatched: false,
            AggregateVerified: false,
            PersistenceVerified: false,
            Items: [],
            errorCode,
            errorMessage,
            DateTimeOffset.UtcNow));
    }

    private static ExtractionNativeInventoryContainerSnapshot CloneContainer(
        ExtractionNativeInventoryContainerSnapshot container) =>
        container with { Slots = container.Slots.ToArray() };

    private static ExtractionModeException InvalidProbe(string message) => new(
        "NATIVE_INVENTORY_PROBE_INVALID",
        message,
        StatusCodes.Status503ServiceUnavailable);

    private static string NormalizeRequiredGuid(string value, string name, bool allowEmpty)
    {
        if (!TryNormalizeGuid(value, allowEmpty, out var normalized))
        {
            throw new ArgumentException($"{name} must be a complete GUID.", name);
        }
        return normalized;
    }

    private static bool TryNormalizeGuid(string? value, bool allowEmpty, out string normalized)
    {
        if (Guid.TryParse(value, out var parsed) && (allowEmpty || parsed != Guid.Empty))
        {
            normalized = parsed.ToString("N").ToLowerInvariant();
            return true;
        }
        normalized = string.Empty;
        return false;
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsValidSlotItem(string itemId, int quantity) =>
        string.Equals(itemId, "None", StringComparison.Ordinal)
            ? quantity == 0
            : quantity is >= 1 and <= 999_999 &&
              itemId.Length is >= 1 and <= 128 &&
              itemId.All(character =>
                  char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static bool TryGetString(JsonElement value, string name, out string result)
    {
        if (value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
        {
            result = property.GetString() ?? string.Empty;
            return true;
        }
        result = string.Empty;
        return false;
    }

    private static bool TryGetBoolean(JsonElement value, string name, out bool result)
    {
        if (value.TryGetProperty(name, out var property) &&
            property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            result = property.GetBoolean();
            return true;
        }
        result = false;
        return false;
    }

    private static bool TryGetInt32(JsonElement value, string name, out int result)
    {
        if (value.TryGetProperty(name, out var property) && property.TryGetInt32(out result))
        {
            return true;
        }
        result = 0;
        return false;
    }

    private static bool TryGetInt64(JsonElement value, string name, out long result)
    {
        if (value.TryGetProperty(name, out var property) && property.TryGetInt64(out result))
        {
            return true;
        }
        result = 0;
        return false;
    }

    private static bool TryGetUInt32(JsonElement value, string name, out uint result)
    {
        if (value.TryGetProperty(name, out var property) && property.TryGetUInt32(out result))
        {
            return true;
        }
        result = 0;
        return false;
    }

    private static bool TryGetDouble(JsonElement value, string name, out double result)
    {
        if (value.TryGetProperty(name, out var property) && property.TryGetDouble(out result))
        {
            return true;
        }
        result = 0;
        return false;
    }
}

public static class ExtractionNativeInventoryCanonicalizer
{
    private static readonly string[] ContainerOrder = ["common", "dropSlot", "food"];

    public static string Hash(ExtractionNativeInventoryQuoteSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        StringBuilder canonical = new();
        canonical.Append("snapshotVersion=").Append(snapshot.SnapshotVersion).Append('\n');
        canonical.Append("ownerPlayerUid=").Append(snapshot.OwnerPlayerUid).Append('\n');
        foreach (var kind in ContainerOrder)
        {
            var matches = snapshot.Containers
                .Where(container => string.Equals(container.ContainerKind, kind, StringComparison.Ordinal))
                .ToArray();
            if (matches.Length != 1)
            {
                throw new InvalidDataException($"Native snapshot must contain exactly one '{kind}' container.");
            }
            var container = matches[0];
            canonical.Append("container=").Append(kind).Append('|')
                .Append(container.ContainerId).Append('\n');
            foreach (var slot in container.Slots.OrderBy(slot => slot.SlotIndex))
            {
                canonical.Append("slot=").Append(slot.SlotIndex).Append('|')
                    .Append(slot.ItemId).Append('|').Append(slot.Quantity).Append('|')
                    .Append(slot.DynamicCreatedWorldId).Append('|')
                    .Append(slot.DynamicLocalIdInCreatedWorld).Append('|')
                    .Append(slot.HasDynamicItemData ? '1' : '0').Append('|')
                    .Append(slot.CorruptionProgress.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                    .Append(slot.CorruptionProgressBits).Append('\n');
            }
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    public static IReadOnlyDictionary<string, long> AggregateTotals(
        ExtractionNativeInventoryQuoteSnapshot snapshot)
    {
        _ = Hash(snapshot);
        Dictionary<string, long> totals = new(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in snapshot.Containers.SelectMany(container => container.Slots))
        {
            if (slot.Quantity <= 0 || string.Equals(slot.ItemId, "None", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            totals.TryGetValue(slot.ItemId, out var current);
            totals[slot.ItemId] = checked(current + slot.Quantity);
        }
        return totals;
    }
}
