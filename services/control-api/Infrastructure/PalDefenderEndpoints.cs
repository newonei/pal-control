using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PalControl.ControlApi.Domain;

namespace PalControl.ControlApi.Infrastructure;

public static class PalDefenderEndpoints
{
    private static readonly HashSet<string> BanlistQueryKeys = new(
        [
            "active",
            "entryType",
            "userId",
            "ip",
            "userIP",
            "issuerType",
            "issuerName",
            "issuerIP",
            "reason",
            "q"
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly object[] Catalog =
    [
        Entry("GET", "players", "REST.Players.Read", "玩家列表"),
        Entry("GET", "player/{playerIdentifier}", "REST.Player.Read", "玩家详情"),
        Entry("GET", "pals/{playerIdentifier}", "REST.Pals.Read", "玩家帕鲁"),
        Entry("GET", "items/{playerIdentifier}", "REST.Items.Read", "玩家物品"),
        Entry("GET", "techs/{playerIdentifier}", "REST.Techs.Read", "玩家科技"),
        Entry("GET", "progression/{playerIdentifier}", "REST.Progression.Read", "玩家进度"),
        Entry("GET", "guilds", "REST.Guilds.Read", "公会列表"),
        Entry("GET", "guild/{guildId}", "REST.Guild.Read", "公会详情与基地"),
        Entry("GET", "banlist", "REST.Banlist.Read", "封禁记录检索"),
        Entry("GET", "version", "REST.Version.Read", "版本与健康检查"),
        Entry("POST", "give/items/{playerIdentifier}", "REST.Items.Give", "发放物品"),
        Entry("POST", "give/pals/{playerIdentifier}", "REST.Pals.Give", "发放帕鲁"),
        Entry("POST", "give/paltemplate/{playerIdentifier}", "REST.PalTemplates.Give", "按模板发放帕鲁"),
        Entry("POST", "give/paleggs/{playerIdentifier}", "REST.PalEggs.Give", "发放帕鲁蛋"),
        Entry("POST", "give/progression/{playerIdentifier}", "REST.Progression.Give", "发放经验与点数"),
        Entry("POST", "learntech/{playerIdentifier}", "REST.Techs.Learn", "学习科技"),
        Entry("POST", "forgettech/{playerIdentifier}", "REST.Techs.Forget", "遗忘科技"),
        Entry("POST", "deletebase/{baseCampId}", "REST.Base.Delete", "删除基地"),
        Entry("POST", "ban/{playerIdentifier}", "REST.Punishments.Ban", "封禁玩家"),
        Entry("POST", "unban/{userId}", "REST.Punishments.Unban", "解除玩家封禁"),
        Entry("POST", "banip/{ip}", "REST.Punishments.BanIP", "封禁 IP"),
        Entry("POST", "unbanip/{ip}", "REST.Punishments.UnbanIP", "解除 IP 封禁"),
        Entry("POST", "kick/{playerIdentifier}", "REST.Punishments.Kick", "踢出玩家"),
        Entry("POST", "SendPlayerMessage", "REST.Messages.Send.*", "发送定向消息"),
        Entry("POST", "Broadcast", "REST.Messages.Broadcast", "广播聊天消息"),
        Entry("POST", "Alert", "REST.Messages.Alert", "发送警报"),
        Entry("POST", "ReloadConfig", "REST.Reload.Config", "热重载配置")
    ];

    public static RouteGroupBuilder MapPalDefenderEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet(
            "/paldefender-commands/{commandId:guid}",
            async (
                Guid commandId,
                PalDefenderCommandQueue commands,
                CancellationToken cancellationToken) =>
            {
                var command = await commands.GetStatusAsync(commandId, cancellationToken);
                return command is null
                    ? Results.NotFound(new ApiError(
                        "PALDEFENDER_COMMAND_NOT_FOUND",
                        $"PalDefender command '{commandId}' does not exist."))
                    : Results.Ok(command);
            });

        api.MapGet(
            "/audit/paldefender-commands",
            async (
                int? limit,
                PalDefenderCommandQueue commands,
                CancellationToken cancellationToken) =>
                Results.Ok(new
                {
                    items = await commands.GetAuditAsync(limit ?? 100, cancellationToken)
                }));

        var group = api.MapGroup("/servers/{serverId}/paldefender")
            .AddPlayerIdentityModerationLink();

        group.MapGet("/catalog", (string serverId, IConfiguration configuration) =>
            ValidateServerId(serverId, configuration) ?? Results.Ok(new
            {
                basePath = $"/api/v1/servers/{serverId}/paldefender",
                count = Catalog.Length,
                items = Catalog
            }));

        group.MapGet(
            "/status",
            async (
                string serverId,
                IConfiguration configuration,
                PalDefenderRestClient client,
                CancellationToken cancellationToken) =>
            {
                var validation = ValidateServerId(serverId, configuration);
                if (validation is not null)
                {
                    return validation;
                }
                if (!client.Enabled)
                {
                    return Results.Ok(new
                    {
                        enabled = false,
                        connected = false,
                        baseUrl = client.BaseUrl,
                        version = (JsonNode?)null,
                        error = new ApiError(
                            "PALDEFENDER_DISABLED",
                            "The PalDefender REST adapter is disabled.")
                    });
                }
                var response = await client.GetAsync("version", cancellationToken);
                return Results.Ok(new
                {
                    enabled = true,
                    connected = response.IsSuccess,
                    baseUrl = client.BaseUrl,
                    version = response.IsSuccess ? response.Json : null,
                    upstreamStatus = response.TransportError ? (int?)null : response.StatusCode,
                    error = response.IsSuccess
                        ? null
                        : new ApiError(
                            response.ErrorCode ?? "PALDEFENDER_PROBE_FAILED",
                            response.ErrorMessage ?? "The PalDefender version probe failed.")
                });
            });

        MapGet(group, "/players", "players");
        MapGet(group, "/player/{playerIdentifier}", "player/{0}");
        MapGet(group, "/pals/{playerIdentifier}", "pals/{0}");
        MapGet(group, "/items/{playerIdentifier}", "items/{0}");
        MapGet(group, "/techs/{playerIdentifier}", "techs/{0}");
        MapGet(group, "/progression/{playerIdentifier}", "progression/{0}");
        MapGet(group, "/guilds", "guilds");
        MapGet(group, "/guild/{guildId}", "guild/{0}", "guildId");
        MapGet(group, "/version", "version");

        group.MapGet(
            "/banlist",
            async (
                string serverId,
                HttpRequest request,
                IConfiguration configuration,
                PalDefenderRestClient client,
                CancellationToken cancellationToken) =>
            {
                var validation = ValidateServerId(serverId, configuration);
                if (validation is not null)
                {
                    return validation;
                }
                var unknown = request.Query.Keys.FirstOrDefault(key => !BanlistQueryKeys.Contains(key));
                if (unknown is not null)
                {
                    return Results.BadRequest(new ApiError(
                        "UNSUPPORTED_QUERY_PARAMETER",
                        $"Query parameter '{unknown}' is not supported for the banlist endpoint."));
                }
                var query = QueryString.Create(
                    request.Query.SelectMany(pair =>
                        pair.Value.Select(value =>
                            new KeyValuePair<string, string?>(pair.Key, value))));
                var response = await client.GetAsync(
                    "banlist" + query,
                    cancellationToken);
                return ToResult(response);
            });

        MapPost(group, "/give/items/{playerIdentifier}", "give/items/{0}");
        MapPost(group, "/give/pals/{playerIdentifier}", "give/pals/{0}");
        MapPost(group, "/give/paltemplate/{playerIdentifier}", "give/paltemplate/{0}");
        MapPost(group, "/give/paleggs/{playerIdentifier}", "give/paleggs/{0}");
        MapPost(group, "/give/progression/{playerIdentifier}", "give/progression/{0}");
        MapPost(group, "/learntech/{playerIdentifier}", "learntech/{0}");
        MapPost(group, "/forgettech/{playerIdentifier}", "forgettech/{0}");
        MapPost(group, "/ban/{playerIdentifier}", "ban/{0}");
        MapPost(group, "/unban/{userId}", "unban/{0}", "userId");
        MapPost(group, "/kick/{playerIdentifier}", "kick/{0}");
        MapPost(group, "/SendPlayerMessage", "SendPlayerMessage", null);
        MapPost(group, "/Broadcast", "Broadcast", null);
        MapPost(group, "/Alert", "Alert", null);
        MapPost(group, "/ReloadConfig", "ReloadConfig", null);

        group.MapPost(
            "/deletebase/{baseCampId}",
            async (
                string serverId,
                string baseCampId,
                HttpRequest request,
                JsonNode? body,
                IConfiguration configuration,
                PalDefenderCommandQueue commands,
                CancellationToken cancellationToken) =>
            {
                var validation = ValidateServerId(serverId, configuration);
                if (validation is not null)
                {
                    return validation;
                }
                if (!Guid.TryParse(baseCampId, out _))
                {
                    return Results.BadRequest(new ApiError(
                        "INVALID_BASE_CAMP_ID",
                        "baseCampId must be a valid GUID."));
                }
                return await EnqueueAsync(
                    serverId,
                    "deletebase/" + Uri.EscapeDataString(baseCampId),
                    body,
                    request,
                    commands,
                    cancellationToken);
            });

        MapIpPost(group, "/banip/{ip}", "banip/{0}");
        MapIpPost(group, "/unbanip/{ip}", "unbanip/{0}");

        return api;
    }

    private static void MapGet(
        RouteGroupBuilder group,
        string route,
        string upstreamTemplate,
        string parameterName = "playerIdentifier")
    {
        if (!upstreamTemplate.Contains("{0}", StringComparison.Ordinal))
        {
            group.MapGet(
                route,
                async (
                    string serverId,
                    IConfiguration configuration,
                    PalDefenderRestClient client,
                    CancellationToken cancellationToken) =>
                {
                    var validation = ValidateServerId(serverId, configuration);
                    return validation ?? ToResult(await client.GetAsync(
                        upstreamTemplate,
                        cancellationToken));
                });
            return;
        }

        group.MapGet(
            route,
            async (
                string serverId,
                HttpRequest request,
                IConfiguration configuration,
                PalDefenderRestClient client,
                CancellationToken cancellationToken) =>
            {
                var validation = ValidateServerId(serverId, configuration);
                if (validation is not null)
                {
                    return validation;
                }
                var routeValue = request.RouteValues[parameterName]?.ToString() ?? string.Empty;
                var identifierValidation = ValidatePathValue(routeValue, parameterName);
                if (identifierValidation is not null)
                {
                    return identifierValidation;
                }
                return ToResult(await client.GetAsync(
                    string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        upstreamTemplate,
                        Uri.EscapeDataString(routeValue)),
                    cancellationToken));
            });
    }

    private static void MapPost(
        RouteGroupBuilder group,
        string route,
        string upstreamTemplate,
        string? parameterName = "playerIdentifier")
    {
        group.MapPost(
            route,
            async (
                string serverId,
                HttpRequest request,
                JsonNode? body,
                IConfiguration configuration,
                PalDefenderCommandQueue commands,
                CancellationToken cancellationToken) =>
            {
                var validation = ValidateServerId(serverId, configuration);
                if (validation is not null)
                {
                    return validation;
                }
                var upstream = upstreamTemplate;
                if (parameterName is not null)
                {
                    var routeValue = request.RouteValues[parameterName]?.ToString() ?? string.Empty;
                    var identifierValidation = ValidatePathValue(routeValue, parameterName);
                    if (identifierValidation is not null)
                    {
                        return identifierValidation;
                    }
                    upstream = string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        upstreamTemplate,
                        Uri.EscapeDataString(routeValue));
                }
                return await EnqueueAsync(
                    serverId,
                    upstream,
                    body,
                    request,
                    commands,
                    cancellationToken);
            });
    }

    private static void MapIpPost(
        RouteGroupBuilder group,
        string route,
        string upstreamTemplate)
    {
        group.MapPost(
            route,
            async (
                string serverId,
                string ip,
                HttpRequest request,
                JsonNode? body,
                IConfiguration configuration,
                PalDefenderCommandQueue commands,
                CancellationToken cancellationToken) =>
            {
                var validation = ValidateServerId(serverId, configuration);
                if (validation is not null)
                {
                    return validation;
                }
                if (!IPAddress.TryParse(ip, out var parsedIp))
                {
                    return Results.BadRequest(new ApiError(
                        "INVALID_IP_ADDRESS",
                        "ip must be a valid IPv4 or IPv6 address."));
                }
                return await EnqueueAsync(
                    serverId,
                    string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        upstreamTemplate,
                        Uri.EscapeDataString(parsedIp.ToString())),
                    body,
                    request,
                    commands,
                    cancellationToken);
            });
    }

    private static async Task<IResult> EnqueueAsync(
        string serverId,
        string upstreamPath,
        JsonNode? body,
        HttpRequest request,
        PalDefenderCommandQueue commands,
        CancellationToken cancellationToken)
    {
        var metadataValidation = ValidateCommandMetadata(
            request,
            body,
            out var idempotencyKey,
            out var reason,
            out var upstreamBody);
        if (metadataValidation is not null)
        {
            return metadataValidation;
        }
        if (!commands.IsReady)
        {
            return Results.Json(
                new ApiError(
                    "PALDEFENDER_COMMAND_QUEUE_UNAVAILABLE",
                    "The durable PalDefender command queue is not ready."),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        PalDefenderCommandEnqueueResult result;
        try
        {
            result = await commands.EnqueueAsync(
                serverId,
                upstreamPath,
                upstreamBody,
                idempotencyKey,
                reason,
                GetActor(request),
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return Results.Json(
                new ApiError(
                    "PALDEFENDER_COMMAND_PERSISTENCE_FAILED",
                    "The PalDefender command could not be durably accepted."),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (result.IdempotencyConflict)
        {
            return Results.Conflict(new ApiError(
                "IDEMPOTENCY_KEY_REUSED",
                "The Idempotency-Key was already used for a different PalDefender operation."));
        }
        if (result.CapacityExceeded)
        {
            return Results.Json(
                new ApiError(
                    "PALDEFENDER_COMMAND_QUEUE_FULL",
                    "The durable PalDefender command queue is at capacity."),
                statusCode: StatusCodes.Status429TooManyRequests);
        }
        var command = result.Command
            ?? throw new InvalidOperationException("A PalDefender enqueue result has no command.");
        request.HttpContext.Response.Headers.Location = command.StatusUrl;
        request.HttpContext.Response.Headers.RetryAfter = "1";
        return Results.Json(
            command,
            statusCode: result.Created || command.State is "accepted" or "dispatched"
                ? StatusCodes.Status202Accepted
                : StatusCodes.Status200OK);
    }

    private static IResult? ValidateCommandMetadata(
        HttpRequest request,
        JsonNode? body,
        out string idempotencyKey,
        out string reason,
        out JsonNode? upstreamBody)
    {
        idempotencyKey = string.Empty;
        reason = string.Empty;
        upstreamBody = body;
        if (!request.Headers.TryGetValue("Idempotency-Key", out var idempotencyValues) ||
            idempotencyValues.Count != 1)
        {
            return Results.BadRequest(new ApiError(
                "IDEMPOTENCY_KEY_REQUIRED",
                "PalDefender write operations require one Idempotency-Key header."));
        }

        idempotencyKey = idempotencyValues[0]?.Trim() ?? string.Empty;
        if (idempotencyKey.Length is < 8 or > 128 || idempotencyKey.Any(char.IsControl))
        {
            return Results.BadRequest(new ApiError(
                "INVALID_IDEMPOTENCY_KEY",
                "Idempotency-Key must contain 8 to 128 non-control characters."));
        }

        if (request.Headers.TryGetValue("X-Operation-Reason", out var reasonValues))
        {
            if (reasonValues.Count != 1)
            {
                return Results.BadRequest(new ApiError(
                    "INVALID_OPERATION_REASON",
                    "X-Operation-Reason must be supplied exactly once."));
            }
            reason = reasonValues[0]?.Trim() ?? string.Empty;
        }
        if (body is JsonObject jsonObject)
        {
            var reasonProperty = jsonObject.FirstOrDefault(property =>
                string.Equals(property.Key, "reason", StringComparison.OrdinalIgnoreCase));
            if (reason.Length == 0 &&
                reasonProperty.Value is JsonValue reasonValue &&
                reasonValue.TryGetValue<string>(out var bodyReason))
            {
                reason = bodyReason.Trim();
            }

            foreach (var property in jsonObject)
            {
                if (string.Equals(property.Key, "payload", StringComparison.OrdinalIgnoreCase))
                {
                    upstreamBody = property.Value?.DeepClone();
                    break;
                }
            }
        }
        if (reason.Length is < 3 or > 500 || reason.Any(char.IsControl))
        {
            return Results.BadRequest(new ApiError(
                "OPERATION_REASON_REQUIRED",
                "Supply X-Operation-Reason, or a string reason field in the JSON body, containing 3 to 500 non-control characters."));
        }
        if (upstreamBody is not null &&
            Encoding.UTF8.GetByteCount(upstreamBody.ToJsonString()) > 64 * 1024)
        {
            return Results.BadRequest(new ApiError(
                "PALDEFENDER_PAYLOAD_TOO_LARGE",
                "PalDefender write payloads must not exceed 64 KiB."));
        }

        return null;
    }

    private static string GetActor(HttpRequest request)
    {
        if (request.HttpContext.User.Identity?.Name is { Length: > 0 } name)
        {
            return name;
        }
        return $"local:{request.HttpContext.Connection.RemoteIpAddress ?? System.Net.IPAddress.Loopback}";
    }

    private static IResult? ValidateServerId(string serverId, IConfiguration configuration)
    {
        var configuredServerId = configuration["Palworld:ServerId"] ?? "local";
        return string.Equals(serverId, configuredServerId, StringComparison.Ordinal)
            ? null
            : Results.NotFound(new ApiError(
                "SERVER_NOT_FOUND",
                $"Server '{serverId}' is not configured on this Control API instance."));
    }

    private static IResult? ValidatePathValue(string value, string name)
    {
        return string.IsNullOrWhiteSpace(value) ||
               value.Length > 160 ||
               value.Any(char.IsControl)
            ? Results.BadRequest(new ApiError(
                "INVALID_PATH_PARAMETER",
                $"{name} must contain 1 to 160 non-control characters."))
            : null;
    }

    private static IResult ToResult(PalDefenderApiResponse response)
    {
        if (response.TransportError)
        {
            return Results.Json(
                new
                {
                    error = new ApiError(
                        response.ErrorCode ?? "PALDEFENDER_UNAVAILABLE",
                        response.ErrorMessage ?? "The PalDefender REST API is unavailable."),
                    outcomeUncertain = response.OutcomeUncertain
                },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        if (response.Json is not null)
        {
            return Results.Json(response.Json, statusCode: response.StatusCode);
        }
        return Results.Text(
            response.Text ?? string.Empty,
            "text/plain; charset=utf-8",
            statusCode: response.StatusCode);
    }

    private static object Entry(string method, string path, string permission, string description) =>
        new { method, path, permission, description };
}
