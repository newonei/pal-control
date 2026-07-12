using PalControl.ControlApi.Domain;

namespace PalControl.ControlApi.Infrastructure;

public sealed class BridgeState
{
    private readonly IConfiguration _configuration;
    private readonly PalworldRestClient _palworldRest;
    private readonly NativeBridgeState _nativeBridge;
    private readonly NativeBridgeClient _nativeBridgeClient;
    private readonly AnnouncementCommandQueue _announcementCommands;
    private readonly InGameNotificationCommandQueue _notificationCommands;
    private readonly InGameNotificationCapabilityService _notificationCapabilities;

    public BridgeState(
        IConfiguration configuration,
        PalworldRestClient palworldRest,
        NativeBridgeState nativeBridge,
        NativeBridgeClient nativeBridgeClient,
        AnnouncementCommandQueue announcementCommands,
        InGameNotificationCommandQueue notificationCommands,
        InGameNotificationCapabilityService notificationCapabilities)
    {
        _configuration = configuration;
        _palworldRest = palworldRest;
        _nativeBridge = nativeBridge;
        _nativeBridgeClient = nativeBridgeClient;
        _announcementCommands = announcementCommands;
        _notificationCommands = notificationCommands;
        _notificationCapabilities = notificationCapabilities;
    }

    public async Task<ServerCapabilities> GetCapabilitiesAsync(
        string serverId,
        CancellationToken cancellationToken)
    {
        var configuredServerId = _configuration["Palworld:ServerId"] ?? "local";
        var info = await _palworldRest.TryGetInfoAsync(cancellationToken);
        var officialRestConnected = info is not null;
        var announcementInfrastructureReady = _announcementCommands.IsReady;
        var notificationInfrastructureReady = _notificationCommands.IsReady;
        var nativeBridge = _nativeBridge.GetSnapshot();
        var effectiveServerId = serverId.Length == 0 ? configuredServerId : serverId;
        var overlayProbeTask = ProbeAnnouncementCapabilityAsync(
            effectiveServerId,
            nativeBridge,
            "announcements.overlay",
            "client-overlay",
            cancellationToken);
        var bannerProbeTask = ProbeAnnouncementCapabilityAsync(
            effectiveServerId,
            nativeBridge,
            "announcements.banner",
            "top-banner",
            cancellationToken);
        var notificationProbeTask = _notificationCapabilities.ProbeAsync(
            effectiveServerId,
            cancellationToken);
        await Task.WhenAll(overlayProbeTask, bannerProbeTask, notificationProbeTask);
        var overlaySignatureReady = await overlayProbeTask;
        var bannerSignatureReady = await bannerProbeTask;
        var notificationProbe = await notificationProbeTask;
        var publishChatAnnouncements =
            announcementInfrastructureReady && officialRestConnected;
        var publishClientOverlay =
            announcementInfrastructureReady &&
            nativeBridge.Connected &&
            nativeBridge.Capabilities.Contains("announcements.overlay.write") &&
            overlaySignatureReady;
        var publishTopBanner =
            announcementInfrastructureReady &&
            nativeBridge.Connected &&
            nativeBridge.Capabilities.Contains("announcements.banner.write") &&
            bannerSignatureReady;
        var sendInGameNotifications =
            notificationInfrastructureReady &&
            notificationProbe.Ready;
        var writeInventory = nativeBridge.Capabilities.Contains("inventory.write");
        var writePals = nativeBridge.Capabilities.Contains("pals.write");
        var writePlayerProgression =
            nativeBridge.Capabilities.Contains("players.progression.write");
        var reasons = new List<string>();

        if (!writePals)
        {
            reasons.Add("PAL_WRITE_FEATURE_FAIL_CLOSED");
        }
        if (!writeInventory)
        {
            reasons.Add("INVENTORY_WRITE_FEATURE_FAIL_CLOSED");
        }
        if (!writePlayerProgression)
        {
            reasons.Add("PLAYER_PROGRESSION_WRITE_FEATURE_FAIL_CLOSED");
        }

        if (!nativeBridge.Connected)
        {
            reasons.Add("PAL_MOD_BRIDGE_NOT_CONNECTED");
        }

        if (!officialRestConnected)
        {
            reasons.Add("PALWORLD_OFFICIAL_REST_UNAVAILABLE");
        }
        if (!announcementInfrastructureReady)
        {
            reasons.Add("ANNOUNCEMENT_COMMAND_INFRASTRUCTURE_UNAVAILABLE");
        }
        if (!publishClientOverlay)
        {
            reasons.Add("ANNOUNCEMENT_CLIENT_OVERLAY_UNAVAILABLE");
        }
        if (!publishTopBanner)
        {
            reasons.Add("ANNOUNCEMENT_TOP_BANNER_UNAVAILABLE");
        }
        if (!sendInGameNotifications)
        {
            reasons.Add("IN_GAME_NOTIFICATION_PRESET_UNAVAILABLE");
        }

        return new ServerCapabilities(
            ServerId: effectiveServerId,
            OfficialRestConnected: officialRestConnected,
            PublishAnnouncements: publishChatAnnouncements || publishClientOverlay || publishTopBanner,
            PublishChatAnnouncements: publishChatAnnouncements,
            PublishClientOverlay: publishClientOverlay,
            PublishTopBanner: publishTopBanner,
            SendInGameNotifications: sendInGameNotifications,
            CommandQueueReady: announcementInfrastructureReady,
            AuditReady: announcementInfrastructureReady,
            BridgeConnected: nativeBridge.Connected,
            ReadPlayers: officialRestConnected,
            ReadPlayerProgression:
                nativeBridge.Capabilities.Contains("players.progression.read"),
            WritePlayerProgression: writePlayerProgression,
            ReadInventory: nativeBridge.Capabilities.Contains("inventory.read"),
            WriteInventory: writeInventory,
            ReadPals: nativeBridge.Capabilities.Contains("pals.read"),
            WritePals: writePals,
            Mode: writePals || writeInventory || writePlayerProgression ||
                    publishClientOverlay || publishTopBanner || sendInGameNotifications
                ? "guarded-native-write"
                : "read-only-safe-mode",
            Reasons: reasons);
    }

    private async Task<bool> ProbeAnnouncementCapabilityAsync(
        string serverId,
        NativeBridgeSnapshot nativeBridge,
        string capabilityPrefix,
        string channelName,
        CancellationToken cancellationToken)
    {
        if (!nativeBridge.Connected ||
            !nativeBridge.Capabilities.Contains($"{capabilityPrefix}.probe") ||
            !nativeBridge.Capabilities.Contains($"{capabilityPrefix}.write"))
        {
            return false;
        }

        try
        {
            var probe = await _nativeBridgeClient.SendCommandAsync(
                serverId,
                $"{capabilityPrefix}.probe",
                new { },
                $"effective {channelName} capability probe",
                cancellationToken);
            return string.Equals(probe.State, "succeeded", StringComparison.Ordinal) &&
                   probe.Data is { } data &&
                   data.TryGetProperty("ready", out var ready) &&
                   ready.ValueKind == System.Text.Json.JsonValueKind.True &&
                   data.TryGetProperty("dispatched", out var dispatched) &&
                   dispatched.ValueKind == System.Text.Json.JsonValueKind.False;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}
