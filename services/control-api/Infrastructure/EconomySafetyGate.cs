using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Domain;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

public enum EconomyWriteFeature
{
    Purchase,
    ResourceExchange
}

public sealed class EconomySafetyOptions
{
    public int DeliveryBacklogCapacity { get; init; } = 128;
    public long MinimumFreeSpaceBytes { get; init; } = 536_870_912;
    public string ApprovedGameVersion { get; init; } = "1.0.0.100427";
    public string ApprovedPalDefenderVersion { get; init; } = "1.8.1.3933";
    public bool PalDefenderGrantReceiptSemanticsVerified { get; init; }
    public bool RequireNativeForPurchase { get; init; }
    public bool RequireNativeForResourceExchange { get; init; }
    public string ApprovedNativeProtocolVersion { get; init; } = "1.0";
    public string ApprovedNativeGameBuild { get; init; } = string.Empty;
    public string ApprovedNativeModVersion { get; init; } = string.Empty;
    public IReadOnlyList<string> PurchaseNativeCapabilities { get; init; } = [];
    public IReadOnlyList<string> ResourceExchangeNativeCapabilities { get; init; } = [];

    public bool IsValid(out string? error)
    {
        if (DeliveryBacklogCapacity is < 1 or > 10_000)
        {
            error = "ExtractionMode:Safety:DeliveryBacklogCapacity must be between 1 and 10000.";
            return false;
        }
        if (MinimumFreeSpaceBytes is < 16_777_216 or > 1_125_899_906_842_624)
        {
            error = "ExtractionMode:Safety:MinimumFreeSpaceBytes must be between 16 MiB and 1 PiB.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(ApprovedGameVersion) ||
            string.IsNullOrWhiteSpace(ApprovedPalDefenderVersion) ||
            ApprovedGameVersion.Length > 128 ||
            ApprovedPalDefenderVersion.Length > 128)
        {
            error = "ExtractionMode:Safety requires explicit approved game and PalDefender versions.";
            return false;
        }
        if ((RequireNativeForPurchase || RequireNativeForResourceExchange) &&
            (string.IsNullOrWhiteSpace(ApprovedNativeProtocolVersion) ||
             string.IsNullOrWhiteSpace(ApprovedNativeGameBuild) ||
             string.IsNullOrWhiteSpace(ApprovedNativeModVersion)))
        {
            error = "An enabled Native economy adapter requires approved protocol, game-build, and mod versions.";
            return false;
        }
        if ((RequireNativeForPurchase && PurchaseNativeCapabilities.Count == 0) ||
            (RequireNativeForResourceExchange && ResourceExchangeNativeCapabilities.Count == 0) ||
            PurchaseNativeCapabilities.Concat(ResourceExchangeNativeCapabilities).Any(capability =>
                string.IsNullOrWhiteSpace(capability) || capability.Length > 128))
        {
            error = "An enabled Native economy adapter requires non-empty, bounded capability names.";
            return false;
        }
        error = null;
        return true;
    }
}

public sealed record EconomySafetyContext(
    Guid? AccountId,
    Guid? SeasonId,
    string? PlatformSubject,
    string? PlayerIdentifier,
    PlayerIdentityBinding? IdentityBinding,
    bool RequireIdentity)
{
    public static EconomySafetyContext FromAccount(ExtractionAccountContext context) =>
        new(
            context.Account.AccountId,
            context.Season.SeasonId,
            context.Account.ExternalUserId,
            context.Account.ExternalUserId,
            context.IdentityBinding,
            RequireIdentity: true);

    public static EconomySafetyContext ForDelivery(string playerIdentifier) =>
        new(null, null, null, playerIdentifier, null, RequireIdentity: true);
}

public sealed record EconomyQueueSnapshot(
    bool Ready,
    int Pending,
    int Capacity,
    int? Backlog = null,
    int? BacklogCapacity = null);

public sealed record EconomyCircuitState(
    bool WritesEnabled,
    string Reason,
    string Actor,
    DateTimeOffset UpdatedAt);

public sealed record EconomySafetyGateState(
    EconomyCircuitState Purchase,
    EconomyCircuitState ResourceExchange);

public sealed record EconomySafetyDecision(
    EconomyWriteFeature Feature,
    bool Enabled,
    IReadOnlyList<ApiError> Blockers,
    EconomyCircuitState Circuit,
    DateTimeOffset EvaluatedAt);

public interface IEconomySafetyDependencyProbe
{
    Task<IReadOnlyList<ApiError>> ProbeAsync(
        EconomyWriteFeature feature,
        EconomySafetyContext? context,
        CancellationToken cancellationToken);
}

public sealed class EconomySafetyDependencyProbe : IEconomySafetyDependencyProbe
{
    private readonly ExtractionCommerceService _commerce;
    private readonly SaveManagementService _saves;
    private readonly PalDefenderRestClient _palDefender;
    private readonly IExtractionRconAdapter _rcon;
    private readonly NativeBridgeState _native;
    private readonly ExtractionModeOptions _modeOptions;
    private readonly ExtractionRconOptions _rconOptions;
    private readonly EconomySafetyOptions _safetyOptions;
    private readonly string _dataDirectory;
    private readonly bool _useDevelopmentRconSettlement;

    public EconomySafetyDependencyProbe(
        ExtractionCommerceService commerce,
        SaveManagementService saves,
        PalDefenderRestClient palDefender,
        IExtractionRconAdapter rcon,
        NativeBridgeState native,
        IOptions<ExtractionModeOptions> modeOptions,
        IOptions<ExtractionRconOptions> rconOptions,
        IOptions<EconomySafetyOptions> safetyOptions,
        IOptions<ExtractionPersistenceOptions> persistenceOptions,
        IWebHostEnvironment environment,
        IConfiguration configuration)
    {
        _commerce = commerce;
        _saves = saves;
        _palDefender = palDefender;
        _rcon = rcon;
        _native = native;
        _modeOptions = modeOptions.Value;
        _rconOptions = rconOptions.Value;
        _safetyOptions = safetyOptions.Value;
        _useDevelopmentRconSettlement = DevelopmentRconSettlementPolicy.IsAllowed(
            environment,
            configuration,
            _rconOptions,
            _safetyOptions);
        var configured = persistenceOptions.Value.DataDirectory;
        _dataDirectory = Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(environment.ContentRootPath, configured));
    }

    public async Task<IReadOnlyList<ApiError>> ProbeAsync(
        EconomyWriteFeature feature,
        EconomySafetyContext? context,
        CancellationToken cancellationToken)
    {
        List<ApiError> blockers = [];
        ProbeDiskCapacity(blockers);
        if (!_commerce.IsReady)
        {
            blockers.Add(Blocker(
                "ECONOMY_STORE_NOT_READY",
                "经济存储尚未就绪，经济写入保持关闭。"));
        }
        else
        {
            try
            {
                await _commerce.ProbeWriteAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                blockers.Add(Blocker(
                    "ECONOMY_STORE_NOT_WRITABLE",
                    "经济数据库当前不可写；只读查询仍可继续。"));
            }
        }

        ExtractionSeason? activeSeason = null;
        try
        {
            activeSeason = await _commerce.GetActiveSeasonAsync(
                _modeOptions.ServerId,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            blockers.Add(Blocker(
                "ACTIVE_SEASON_READ_FAILED",
                "无法读取当前活动经济赛季，经济写入保持关闭。"));
        }

        if (activeSeason is null)
        {
            blockers.Add(Blocker(
                "ACTIVE_SEASON_UNAVAILABLE",
                "当前没有活动经济赛季，经济写入保持关闭。"));
        }
        else
        {
            ValidateSeasonWindow(activeSeason, blockers);
            await ValidateWorldAsync(activeSeason, blockers, cancellationToken);
            if (context?.RequireIdentity == true)
            {
                await ValidateIdentityAsync(activeSeason, context, blockers, cancellationToken);
            }
        }

        if (feature == EconomyWriteFeature.Purchase)
        {
            await ProbePurchaseAdapterAsync(blockers, cancellationToken);
            ValidateNativeAdapter(
                _safetyOptions.RequireNativeForPurchase,
                _safetyOptions.PurchaseNativeCapabilities,
                blockers);
        }
        else
        {
            var requireNativeSettlement = !_useDevelopmentRconSettlement;
            if (!requireNativeSettlement)
            {
                await ProbeResourceExchangeAdapterAsync(blockers, cancellationToken);
            }
            ValidateNativeAdapter(
                requireNativeSettlement,
                requireNativeSettlement
                    ? _safetyOptions.ResourceExchangeNativeCapabilities
                        .Append("inventory.probe")
                        .Append(ExtractionNativeInventoryAdapter.StableConsumeCapability)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray()
                    : _safetyOptions.ResourceExchangeNativeCapabilities,
                blockers);
        }
        return blockers;
    }

    private void ProbeDiskCapacity(ICollection<ApiError> blockers)
    {
        try
        {
            var root = Path.GetPathRoot(_dataDirectory);
            if (string.IsNullOrWhiteSpace(root))
            {
                throw new IOException("The economy data directory has no filesystem root.");
            }
            var drive = new DriveInfo(root);
            if (!drive.IsReady || drive.AvailableFreeSpace < _safetyOptions.MinimumFreeSpaceBytes)
            {
                blockers.Add(Blocker(
                    "ECONOMY_DISK_SPACE_LOW",
                    "经济数据盘剩余空间低于安全阈值；只读查询仍可继续。"));
            }
        }
        catch
        {
            blockers.Add(Blocker(
                "ECONOMY_DISK_SPACE_PROBE_FAILED",
                "无法验证经济数据盘剩余空间，经济写入保持关闭。"));
        }
    }

    private static void ValidateSeasonWindow(
        ExtractionSeason season,
        ICollection<ApiError> blockers)
    {
        var now = DateTimeOffset.UtcNow;
        if (now < season.StartsAt || now >= season.EndsAt)
        {
            blockers.Add(Blocker(
                "SEASON_ROLLOVER_REQUIRED",
                "当前经济周档不在有效时间窗口内，必须先完成受控换档。"));
        }
        if (season.WorldId is null)
        {
            blockers.Add(Blocker(
                "SEASON_WORLD_UNBOUND",
                "当前经济赛季尚未绑定 Palworld 世界。"));
        }
    }

    private async Task ValidateWorldAsync(
        ExtractionSeason season,
        ICollection<ApiError> blockers,
        CancellationToken cancellationToken)
    {
        if (season.WorldId is null)
        {
            return;
        }
        try
        {
            var activeWorld = await _saves.ResolveActiveWorldAsync(
                _modeOptions.ServerId,
                cancellationToken);
            if (!string.Equals(
                    NormalizeWorldId(season.WorldId),
                    NormalizeWorldId(activeWorld.WorldGuid),
                    StringComparison.Ordinal))
            {
                blockers.Add(Blocker(
                    "SEASON_WORLD_MISMATCH",
                    "当前 Palworld 世界与活动经济赛季不一致。"));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            blockers.Add(Blocker(
                "ACTIVE_WORLD_VERIFICATION_FAILED",
                "无法证明当前 Palworld 世界身份，经济写入保持关闭。"));
        }
    }

    private async Task ValidateIdentityAsync(
        ExtractionSeason activeSeason,
        EconomySafetyContext context,
        ICollection<ApiError> blockers,
        CancellationToken cancellationToken)
    {
        if (context.SeasonId is Guid requestedSeasonId &&
            requestedSeasonId != activeSeason.SeasonId)
        {
            blockers.Add(Blocker(
                "ECONOMY_SEASON_CONTEXT_MISMATCH",
                "请求账户不属于当前活动经济赛季。"));
            return;
        }

        var binding = context.IdentityBinding;
        if (binding is null && !string.IsNullOrWhiteSpace(context.PlayerIdentifier))
        {
            try
            {
                binding = await _commerce.FindActivePlayerIdentityBindingAsync(
                    _modeOptions.ServerId,
                    context.PlayerIdentifier,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                blockers.Add(Blocker(
                    "PLAYER_IDENTITY_BINDING_READ_FAILED",
                    "无法读取玩家的当前周世界身份绑定。"));
                return;
            }
        }
        if (binding is null)
        {
            blockers.Add(Blocker(
                "PLAYER_IDENTITY_BINDING_REQUIRED",
                "玩家尚未建立当前周世界的稳定身份绑定。"));
            return;
        }

        var bindingMatches = binding.SeasonId == activeSeason.SeasonId &&
            activeSeason.WorldId is not null &&
            string.Equals(
                NormalizeWorldId(binding.WorldId),
                NormalizeWorldId(activeSeason.WorldId),
                StringComparison.Ordinal) &&
            (context.AccountId is not Guid accountId || binding.AccountId == accountId) &&
            (string.IsNullOrWhiteSpace(context.PlatformSubject) ||
             string.Equals(
                 binding.PlatformSubject,
                 context.PlatformSubject,
                 StringComparison.OrdinalIgnoreCase));
        if (!bindingMatches)
        {
            blockers.Add(Blocker(
                "PLAYER_IDENTITY_BINDING_MISMATCH",
                "玩家身份绑定与账户、赛季或当前世界不一致。"));
        }
    }

    private async Task ProbePurchaseAdapterAsync(
        ICollection<ApiError> blockers,
        CancellationToken cancellationToken)
    {
        if (!_palDefender.Enabled)
        {
            blockers.Add(Blocker(
                "PALDEFENDER_DISABLED",
                "商城发货所需的 PalDefender REST adapter 未启用。"));
            return;
        }
        var permissionProbe = await _palDefender.ProbeConfiguredPermissionsAsync(
            ["REST.Version.Read", "REST.Players.Read", "REST.Items.Read", "REST.Items.Give"],
            cancellationToken);
        if (!permissionProbe.Success)
        {
            blockers.Add(Blocker(
                permissionProbe.ErrorCode ?? "PALDEFENDER_CAPABILITY_NOT_APPROVED",
                permissionProbe.ErrorMessage ??
                "商城发货凭据未证明所需的 PalDefender REST capabilities。"));
            return;
        }
        var response = await _palDefender.GetAsync("version", cancellationToken);
        if (!response.IsSuccess || response.Json is not JsonObject document)
        {
            blockers.Add(Blocker(
                response.ErrorCode ?? "PALDEFENDER_VERSION_PROBE_FAILED",
                "无法验证商城发货 adapter 的 PalDefender 与游戏版本。"));
            return;
        }
        var gameVersion = GetString(document, "game_version");
        var palDefender = GetProperty(document, "paldefender") as JsonObject;
        var palDefenderVersion = palDefender is null ? null : GetString(palDefender, "full");
        if (!string.Equals(
                gameVersion,
                _safetyOptions.ApprovedGameVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                palDefenderVersion,
                _safetyOptions.ApprovedPalDefenderVersion,
                StringComparison.Ordinal))
        {
            blockers.Add(Blocker(
                "PALDEFENDER_VERSION_NOT_APPROVED",
                "商城发货 adapter 报告的游戏或 PalDefender 版本不在批准列表中。"));
        }
    }

    private async Task ProbeResourceExchangeAdapterAsync(
        ICollection<ApiError> blockers,
        CancellationToken cancellationToken)
    {
        if (!_rconOptions.Enabled)
        {
            blockers.Add(Blocker(
                "RCON_DISABLED",
                "资源兑换 RCON adapter 未启用。"));
            return;
        }
        var commands = await _rcon.GetCommandsAsync(cancellationToken);
        if (!commands.Success ||
            !RconCapabilityCatalog.ContainsExact(commands.Response, "delitems:2"))
        {
            blockers.Add(Blocker(
                commands.ErrorCode?.ToUpperInvariant() ?? "RCON_DELITEMS_CAPABILITY_MISSING",
                "资源兑换 adapter 未证明 delitems:2 能力。"));
            return;
        }
        var version = await _rcon.GetVersionAsync(cancellationToken);
        if (!version.Success)
        {
            blockers.Add(Blocker(
                version.ErrorCode?.ToUpperInvariant() ?? "RCON_VERSION_PROBE_FAILED",
                "无法验证资源兑换 adapter 的游戏与 PalDefender 版本。"));
            return;
        }
        try
        {
            var document = JsonNode.Parse(version.Response ?? string.Empty) as JsonObject;
            var gameVersion = document is null ? null : GetString(document, "game_version");
            var palDefender = document is null
                ? null
                : GetProperty(document, "paldefender") as JsonObject;
            var palDefenderVersion = palDefender is null ? null : GetString(palDefender, "full");
            if (!string.Equals(gameVersion, _rconOptions.ApprovedGameVersion, StringComparison.Ordinal) ||
                !string.Equals(
                    palDefenderVersion,
                    _rconOptions.ApprovedPalDefenderVersion,
                    StringComparison.Ordinal))
            {
                blockers.Add(Blocker(
                    "RCON_VERSION_NOT_APPROVED",
                    "资源兑换 adapter 报告的游戏或 PalDefender 版本不在批准列表中。"));
            }
        }
        catch (JsonException)
        {
            blockers.Add(Blocker(
                "RCON_VERSION_RESPONSE_INVALID",
                "资源兑换 adapter 返回了无效的版本文档。"));
        }
    }

    private void ValidateNativeAdapter(
        bool required,
        IReadOnlyCollection<string> requiredCapabilities,
        ICollection<ApiError> blockers)
    {
        if (!required)
        {
            return;
        }
        var snapshot = _native.GetSnapshot();
        if (!snapshot.Connected)
        {
            blockers.Add(Blocker(
                "NATIVE_ECONOMY_ADAPTER_NOT_CONNECTED",
                "该经济写入要求的 Native adapter 未连接。"));
            return;
        }
        if (!string.Equals(
                snapshot.ProtocolVersion,
                _safetyOptions.ApprovedNativeProtocolVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                snapshot.GameBuild,
                _safetyOptions.ApprovedNativeGameBuild,
                StringComparison.Ordinal) ||
            !string.Equals(
                snapshot.ModVersion,
                _safetyOptions.ApprovedNativeModVersion,
                StringComparison.Ordinal))
        {
            blockers.Add(Blocker(
                "NATIVE_ECONOMY_ADAPTER_VERSION_NOT_APPROVED",
                "Native economy adapter 的协议、游戏或 MOD 版本不在批准列表中。"));
        }
        var missing = requiredCapabilities
            .Where(capability => !snapshot.Capabilities.Contains(capability))
            .OrderBy(capability => capability, StringComparer.Ordinal)
            .ToArray();
        if (missing.Length > 0)
        {
            blockers.Add(Blocker(
                "NATIVE_ECONOMY_CAPABILITY_MISSING",
                $"Native economy adapter 缺少能力：{string.Join(", ", missing)}。"));
        }
    }

    private static string NormalizeWorldId(string worldId) =>
        worldId.Trim().ToUpperInvariant();

    private static JsonNode? GetProperty(JsonObject value, string name) =>
        value.FirstOrDefault(property =>
            string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static string? GetString(JsonObject value, string name) =>
        GetProperty(value, name) is JsonValue jsonValue &&
        jsonValue.TryGetValue<string>(out var text)
            ? text
            : null;

    private static ApiError Blocker(string code, string message) =>
        new(code.ToUpperInvariant(), message);
}

public sealed class EconomySafetyGate
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly object _sync = new();
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly SemaphoreSlim _purchaseAdmission = new(1, 1);
    private readonly IEconomySafetyDependencyProbe _dependencies;
    private readonly ExtractionOperationGate _operationGate;
    private readonly ExtractionModeOptions _modeOptions;
    private readonly EconomySafetyOptions _safetyOptions;
    private readonly string _legacyPath;
    private readonly string _connectionString;
    private EconomySafetyGateState _state;
    private int _activePurchase;
    private int _activeResourceExchange;
    private TaskCompletionSource<bool> _purchaseDrained = CompletedDrain();
    private TaskCompletionSource<bool> _resourceExchangeDrained = CompletedDrain();

    public EconomySafetyGate(
        IEconomySafetyDependencyProbe dependencies,
        ExtractionOperationGate operationGate,
        IOptions<ExtractionModeOptions> modeOptions,
        IOptions<EconomySafetyOptions> safetyOptions,
        IOptions<ExtractionPersistenceOptions> persistenceOptions,
        IWebHostEnvironment environment)
    {
        _dependencies = dependencies;
        _operationGate = operationGate;
        _modeOptions = modeOptions.Value;
        _safetyOptions = safetyOptions.Value;
        var configured = persistenceOptions.Value.DataDirectory;
        var directory = Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(environment.ContentRootPath, configured));
        Directory.CreateDirectory(directory);
        _legacyPath = Path.Combine(directory, "economy-safety-gate.json");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "extraction-commerce.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
        InitializeStateStore();
        ImportLegacyStateOnce();
        var persisted = Load();
        _state = persisted ?? DefaultState();
        if (persisted is null)
        {
            Persist(_state);
        }
    }

    public int DeliveryBacklogCapacity => _safetyOptions.DeliveryBacklogCapacity;

    public EconomySafetyGateState Current
    {
        get
        {
            lock (_sync)
            {
                return _state with
                {
                    Purchase = _state.Purchase with { },
                    ResourceExchange = _state.ResourceExchange with { }
                };
            }
        }
    }

    public async Task<EconomySafetyDecision> EvaluateAsync(
        EconomyWriteFeature feature,
        EconomySafetyContext? context,
        EconomyQueueSnapshot? queue,
        CancellationToken cancellationToken) => await EvaluateCoreAsync(
            feature,
            context,
            queue,
            ignoreMaintenance: false,
            cancellationToken: cancellationToken);

    public async Task<EconomySafetyDecision> EvaluateForMaintenanceReopenAsync(
        EconomyWriteFeature feature,
        EconomyQueueSnapshot? queue,
        CancellationToken cancellationToken)
    {
        if (!_operationGate.Current.Maintenance)
        {
            throw new InvalidOperationException(
                "Maintenance-reopen dependency evaluation requires the operation gate to remain closed.");
        }
        return await EvaluateCoreAsync(
            feature,
            context: null,
            queue: queue,
            ignoreMaintenance: true,
            cancellationToken: cancellationToken);
    }

    private async Task<EconomySafetyDecision> EvaluateCoreAsync(
        EconomyWriteFeature feature,
        EconomySafetyContext? context,
        EconomyQueueSnapshot? queue,
        bool ignoreMaintenance,
        CancellationToken cancellationToken)
    {
        List<ApiError> blockers = [];
        if (!_modeOptions.Enabled)
        {
            blockers.Add(new ApiError(
                "EXTRACTION_MODE_DISABLED",
                "周世界资源经济玩法未启用。"));
        }

        if (feature == EconomyWriteFeature.Purchase &&
            !_safetyOptions.PalDefenderGrantReceiptSemanticsVerified)
        {
            blockers.Add(new ApiError(
                "PALDEFENDER_GRANT_RECEIPT_UNVERIFIED",
                "PalDefender item-grant response semantics have not been approved for this exact game and adapter version."));
        }

        var circuit = GetCircuit(feature);
        if (!circuit.WritesEnabled)
        {
            blockers.Add(new ApiError(
                feature == EconomyWriteFeature.Purchase
                    ? "PURCHASE_CIRCUIT_OPEN"
                    : "RESOURCE_EXCHANGE_CIRCUIT_OPEN",
                circuit.Reason));
        }

        var maintenance = _operationGate.Current;
        if (maintenance.Maintenance && !ignoreMaintenance)
        {
            blockers.Add(new ApiError(
                "EXTRACTION_MAINTENANCE",
                $"资源经济已进入换档维护：{maintenance.Reason}"));
        }

        if (queue is not null)
        {
            AddQueueBlockers(feature, queue, blockers);
        }

        if (_modeOptions.Enabled)
        {
            try
            {
                blockers.AddRange(await _dependencies.ProbeAsync(
                    feature,
                    context,
                    cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                blockers.Add(new ApiError(
                    feature == EconomyWriteFeature.Purchase
                        ? "PURCHASE_DEPENDENCY_PROBE_FAILED"
                        : "RESOURCE_EXCHANGE_DEPENDENCY_PROBE_FAILED",
                    "经济写入依赖探针失败；只读服务仍保持可用。"));
            }
        }

        var distinct = blockers
            .GroupBy(blocker => blocker.Code, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        return new EconomySafetyDecision(
            feature,
            distinct.Length == 0,
            distinct,
            circuit,
            DateTimeOffset.UtcNow);
    }

    public async Task<IDisposable> AcquireAsync(
        EconomyWriteFeature feature,
        EconomySafetyContext? context,
        EconomyQueueSnapshot? queue,
        CancellationToken cancellationToken)
    {
        var purchaseAdmissionHeld = false;
        if (feature == EconomyWriteFeature.Purchase)
        {
            await _purchaseAdmission.WaitAsync(cancellationToken);
            purchaseAdmissionHeld = true;
        }
        try
        {
            var decision = await EvaluateAsync(feature, context, queue, cancellationToken);
            ThrowIfBlocked(decision);

            var operationLease = _operationGate.AcquireOperation();
            try
            {
                lock (_sync)
                {
                    var circuit = CircuitFrom(_state, feature);
                    if (!circuit.WritesEnabled)
                    {
                        throw CircuitException(feature, circuit.Reason);
                    }
                    if (feature == EconomyWriteFeature.Purchase)
                    {
                        if (_activePurchase == 0)
                        {
                            _purchaseDrained = NewDrain();
                        }
                        _activePurchase = checked(_activePurchase + 1);
                    }
                    else
                    {
                        if (_activeResourceExchange == 0)
                        {
                            _resourceExchangeDrained = NewDrain();
                        }
                        _activeResourceExchange = checked(_activeResourceExchange + 1);
                    }
                }
                return new SafetyLease(
                    this,
                    feature,
                    operationLease,
                    purchaseAdmissionHeld);
            }
            catch
            {
                operationLease.Dispose();
                throw;
            }
        }
        catch
        {
            if (purchaseAdmissionHeld)
            {
                _purchaseAdmission.Release();
            }
            throw;
        }
    }

    public async Task RequireAsync(
        EconomyWriteFeature feature,
        EconomySafetyContext? context,
        EconomyQueueSnapshot? queue,
        CancellationToken cancellationToken)
    {
        var decision = await EvaluateAsync(feature, context, queue, cancellationToken);
        ThrowIfBlocked(decision);
    }

    public async Task<EconomySafetyGateState> SetCircuitAsync(
        EconomyWriteFeature feature,
        bool writesEnabled,
        string reason,
        string actor,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (reason.Trim().Length is < 3 or > 500 || reason.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Circuit reason must contain 3 to 500 non-control characters.",
                nameof(reason));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        if (actor.Trim().Length > 256 || actor.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Circuit actor must contain at most 256 non-control characters.",
                nameof(actor));
        }

        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            EconomySafetyGateState previous;
            lock (_sync)
            {
                previous = _state;
            }
            var previousCircuit = feature == EconomyWriteFeature.Purchase
                ? previous.Purchase
                : previous.ResourceExchange;
            if (previousCircuit.WritesEnabled == writesEnabled &&
                string.Equals(previousCircuit.Reason, reason.Trim(), StringComparison.Ordinal) &&
                string.Equals(previousCircuit.Actor, actor.Trim(), StringComparison.Ordinal))
            {
                return previous;
            }
            var circuit = new EconomyCircuitState(
                writesEnabled,
                reason.Trim(),
                actor.Trim(),
                DateTimeOffset.UtcNow);
            var updated = WithCircuit(previous, feature, circuit);
            if (!writesEnabled)
            {
                lock (_sync)
                {
                    _state = updated;
                }
                try
                {
                    await PersistAsync(updated, cancellationToken);
                }
                catch
                {
                    lock (_sync)
                    {
                        _state = previous;
                    }
                    throw;
                }

                Task drain;
                lock (_sync)
                {
                    drain = feature == EconomyWriteFeature.Purchase
                        ? (_activePurchase == 0 ? Task.CompletedTask : _purchaseDrained.Task)
                        : (_activeResourceExchange == 0
                            ? Task.CompletedTask
                            : _resourceExchangeDrained.Task);
                }
                await drain.WaitAsync(cancellationToken);
            }
            else
            {
                await PersistAsync(updated, cancellationToken);
                lock (_sync)
                {
                    _state = updated;
                }
            }
            return Current;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private static void AddQueueBlockers(
        EconomyWriteFeature feature,
        EconomyQueueSnapshot queue,
        ICollection<ApiError> blockers)
    {
        var prefix = feature == EconomyWriteFeature.Purchase
            ? "SHOP_DELIVERY"
            : "RESOURCE_EXCHANGE";
        if (!queue.Ready)
        {
            blockers.Add(new ApiError(
                $"{prefix}_QUEUE_NOT_READY",
                "经济写入队列当前不可用。"));
        }
        if (queue.Capacity <= 0 || queue.Pending >= queue.Capacity)
        {
            blockers.Add(new ApiError(
                $"{prefix}_QUEUE_FULL",
                "经济写入队列已达到容量上限。"));
        }
        if (queue.Backlog is int backlog &&
            queue.BacklogCapacity is int backlogCapacity &&
            (backlogCapacity <= 0 || backlog >= backlogCapacity))
        {
            blockers.Add(new ApiError(
                $"{prefix}_BACKLOG_FULL",
                "经济待处理事务已达到安全容量上限。"));
        }
    }

    private static void ThrowIfBlocked(EconomySafetyDecision decision)
    {
        if (decision.Enabled)
        {
            return;
        }
        var blocker = decision.Blockers[0];
        var status = blocker.Code.EndsWith("_FULL", StringComparison.Ordinal)
            ? StatusCodes.Status429TooManyRequests
            : blocker.Code is "EXTRACTION_MAINTENANCE" or
                "PURCHASE_CIRCUIT_OPEN" or
                "RESOURCE_EXCHANGE_CIRCUIT_OPEN" or
                "SEASON_ROLLOVER_REQUIRED" or
                "SEASON_WORLD_UNBOUND" or
                "SEASON_WORLD_MISMATCH"
                ? StatusCodes.Status423Locked
                : StatusCodes.Status503ServiceUnavailable;
        throw new ExtractionModeException(blocker.Code, blocker.Message, status);
    }

    private EconomyCircuitState GetCircuit(EconomyWriteFeature feature)
    {
        lock (_sync)
        {
            return CircuitFrom(_state, feature) with { };
        }
    }

    private void Release(
        EconomyWriteFeature feature,
        IDisposable operationLease,
        bool releasePurchaseAdmission)
    {
        TaskCompletionSource<bool>? completed = null;
        lock (_sync)
        {
            if (feature == EconomyWriteFeature.Purchase)
            {
                if (_activePurchase <= 0)
                {
                    throw new InvalidOperationException("Purchase safety-gate lease count underflowed.");
                }
                _activePurchase--;
                if (_activePurchase == 0)
                {
                    completed = _purchaseDrained;
                }
            }
            else
            {
                if (_activeResourceExchange <= 0)
                {
                    throw new InvalidOperationException("Resource safety-gate lease count underflowed.");
                }
                _activeResourceExchange--;
                if (_activeResourceExchange == 0)
                {
                    completed = _resourceExchangeDrained;
                }
            }
        }
        operationLease.Dispose();
        if (releasePurchaseAdmission)
        {
            _purchaseAdmission.Release();
        }
        completed?.TrySetResult(true);
    }

    private EconomySafetyGateState? Load()
    {
        using var connection = OpenStateStore();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT state_json FROM economy_gate_state WHERE gate_key = 'safety';
            """;
        if (command.ExecuteScalar() is not string json)
        {
            return null;
        }
        var state = JsonSerializer.Deserialize<EconomySafetyGateState>(json, JsonOptions);
        if (state is null ||
            !IsValidCircuit(state.Purchase) ||
            !IsValidCircuit(state.ResourceExchange))
        {
            throw new InvalidDataException("The economy safety-gate state is invalid.");
        }
        return state;
    }

    private async Task PersistAsync(
        EconomySafetyGateState state,
        CancellationToken cancellationToken)
    {
        ValidateState(state);
        await using var connection = OpenStateStore();
        await using var command = CreateStateUpsert(connection, state);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private void Persist(EconomySafetyGateState state)
    {
        ValidateState(state);
        using var connection = OpenStateStore();
        using var command = CreateStateUpsert(connection, state);
        command.ExecuteNonQuery();
    }

    private void InitializeStateStore()
    {
        using var connection = OpenStateStore();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS economy_schema_migrations (
                component TEXT NOT NULL,
                version INTEGER NOT NULL CHECK (version > 0),
                applied_at TEXT NOT NULL,
                PRIMARY KEY (component, version)
            );
            CREATE TABLE IF NOT EXISTS economy_gate_state (
                gate_key TEXT PRIMARY KEY CHECK (gate_key IN ('operation', 'safety')),
                state_json TEXT NOT NULL CHECK (json_valid(state_json)),
                revision INTEGER NOT NULL CHECK (revision > 0),
                updated_at TEXT NOT NULL
            );
            INSERT OR IGNORE INTO economy_schema_migrations (component, version, applied_at)
            VALUES ('economy-gate-state', 1, $appliedAt);
            """;
        command.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private void ImportLegacyStateOnce()
    {
        using var connection = OpenStateStore();
        using (var check = connection.CreateCommand())
        {
            check.CommandText = """
                SELECT 1 FROM economy_schema_migrations
                WHERE component = 'safety-gate-legacy-json' AND version = 1;
                """;
            if (check.ExecuteScalar() is not null)
            {
                return;
            }
        }
        EconomySafetyGateState? legacy = null;
        if (File.Exists(_legacyPath))
        {
            legacy = JsonSerializer.Deserialize<EconomySafetyGateState>(
                File.ReadAllBytes(_legacyPath), JsonOptions)
                ?? throw new InvalidDataException("The legacy economy safety-gate state is invalid.");
            ValidateState(legacy);
        }
        using var transaction = connection.BeginTransaction();
        if (legacy is not null)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO economy_gate_state (
                    gate_key, state_json, revision, updated_at)
                VALUES ('safety', $state, 1, $updatedAt);
                """;
            insert.Parameters.AddWithValue("$state", JsonSerializer.Serialize(legacy, JsonOptions));
            insert.Parameters.AddWithValue("$updatedAt", LatestUpdatedAt(legacy).ToString("O"));
            insert.ExecuteNonQuery();
        }
        using (var marker = connection.CreateCommand())
        {
            marker.Transaction = transaction;
            marker.CommandText = """
                INSERT INTO economy_schema_migrations (component, version, applied_at)
                VALUES ('safety-gate-legacy-json', 1, $appliedAt);
                """;
            marker.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
            marker.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private SqliteConnection OpenStateStore()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static SqliteCommand CreateStateUpsert(
        SqliteConnection connection,
        EconomySafetyGateState state)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO economy_gate_state (gate_key, state_json, revision, updated_at)
            VALUES ('safety', $state, 1, $updatedAt)
            ON CONFLICT(gate_key) DO UPDATE SET
                state_json = excluded.state_json,
                revision = economy_gate_state.revision + 1,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$state", JsonSerializer.Serialize(state, JsonOptions));
        command.Parameters.AddWithValue("$updatedAt", LatestUpdatedAt(state).ToString("O"));
        return command;
    }

    private static DateTimeOffset LatestUpdatedAt(EconomySafetyGateState state) =>
        state.Purchase.UpdatedAt >= state.ResourceExchange.UpdatedAt
            ? state.Purchase.UpdatedAt
            : state.ResourceExchange.UpdatedAt;

    private static void ValidateState(EconomySafetyGateState state)
    {
        if (!IsValidCircuit(state.Purchase) || !IsValidCircuit(state.ResourceExchange))
        {
            throw new InvalidDataException("The economy safety-gate state is invalid.");
        }
    }

    private static EconomySafetyGateState DefaultState()
    {
        var now = DateTimeOffset.UtcNow;
        return new EconomySafetyGateState(
            new EconomyCircuitState(true, "商城购买正常", "system-bootstrap", now),
            new EconomyCircuitState(true, "资源兑换正常", "system-bootstrap", now));
    }

    private static bool IsValidCircuit(EconomyCircuitState? circuit) =>
        circuit is not null &&
        !string.IsNullOrWhiteSpace(circuit.Reason) &&
        circuit.Reason.Length <= 500 &&
        !circuit.Reason.Any(char.IsControl) &&
        !string.IsNullOrWhiteSpace(circuit.Actor) &&
        circuit.Actor.Length <= 256;

    private static EconomyCircuitState CircuitFrom(
        EconomySafetyGateState state,
        EconomyWriteFeature feature) =>
        feature == EconomyWriteFeature.Purchase ? state.Purchase : state.ResourceExchange;

    private static EconomySafetyGateState WithCircuit(
        EconomySafetyGateState state,
        EconomyWriteFeature feature,
        EconomyCircuitState circuit) =>
        feature == EconomyWriteFeature.Purchase
            ? state with { Purchase = circuit }
            : state with { ResourceExchange = circuit };

    private static ExtractionModeException CircuitException(
        EconomyWriteFeature feature,
        string reason) =>
        new(
            feature == EconomyWriteFeature.Purchase
                ? "PURCHASE_CIRCUIT_OPEN"
                : "RESOURCE_EXCHANGE_CIRCUIT_OPEN",
            reason,
            StatusCodes.Status423Locked);

    private static TaskCompletionSource<bool> NewDrain() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<bool> CompletedDrain()
    {
        var source = NewDrain();
        source.SetResult(true);
        return source;
    }

    private sealed class SafetyLease : IDisposable
    {
        private EconomySafetyGate? _owner;
        private IDisposable? _operationLease;
        private readonly EconomyWriteFeature _feature;
        private readonly bool _releasePurchaseAdmission;

        public SafetyLease(
            EconomySafetyGate owner,
            EconomyWriteFeature feature,
            IDisposable operationLease,
            bool releasePurchaseAdmission)
        {
            _owner = owner;
            _feature = feature;
            _operationLease = operationLease;
            _releasePurchaseAdmission = releasePurchaseAdmission;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            var operationLease = Interlocked.Exchange(ref _operationLease, null);
            if (owner is not null && operationLease is not null)
            {
                owner.Release(
                    _feature,
                    operationLease,
                    _releasePurchaseAdmission);
            }
        }
    }
}
