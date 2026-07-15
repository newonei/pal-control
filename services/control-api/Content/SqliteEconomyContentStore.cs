using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Extraction;
using PalControl.ControlApi.Infrastructure;

namespace PalControl.ControlApi.Content;

public interface IEconomyContentStore
{
    Task<EconomyContentDraft> CreateDraftAsync(
        string serverId,
        string name,
        Guid? basedOnVersionId,
        EconomyContentDefinition definition,
        string actor,
        CancellationToken cancellationToken);

    Task<EconomyContentDraft?> GetDraftAsync(Guid draftId, CancellationToken cancellationToken);

    Task<IReadOnlyList<EconomyContentDraft>> ListDraftsAsync(
        string serverId,
        bool includePublished,
        CancellationToken cancellationToken);

    Task<EconomyContentDraft> UpdateDraftAsync(
        Guid draftId,
        long expectedRevision,
        EconomyContentDefinition definition,
        string actor,
        CancellationToken cancellationToken);

    Task<ContentValidationResult> ValidateDraftAsync(
        Guid draftId,
        long expectedRevision,
        EconomyContentValidationContext context,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ContentDiffEntry>> DiffDraftAsync(
        Guid draftId,
        CancellationToken cancellationToken);

    Task<ContentPublishResult> PublishAsync(
        Guid draftId,
        long expectedRevision,
        DateOnly businessDate,
        string idempotencyKey,
        string actor,
        EconomyContentValidationContext context,
        CancellationToken cancellationToken);

    Task<PreparedContentPublish> PreparePublishAsync(
        Guid draftId,
        long expectedRevision,
        DateOnly businessDate,
        string idempotencyKey,
        string actor,
        EconomyContentValidationContext context,
        CancellationToken cancellationToken);

    Task<EconomyContentPointer?> GetCurrentAsync(string serverId, CancellationToken cancellationToken);

    Task<EconomyContentVersion?> GetVersionAsync(Guid versionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<EconomyContentVersion>> ListVersionsAsync(
        string serverId,
        CancellationToken cancellationToken);

    Task<ContentRollbackResult> RollbackAsync(
        string serverId,
        Guid? targetVersionId,
        Guid expectedCurrentVersionId,
        string idempotencyKey,
        string actor,
        CancellationToken cancellationToken);

    Task<PreparedContentRollback> PrepareRollbackAsync(
        string serverId,
        Guid? targetVersionId,
        Guid expectedCurrentVersionId,
        string idempotencyKey,
        string actor,
        CancellationToken cancellationToken);

    Task<ContentProductDefinition> ResolveCurrentProductAsync(
        string serverId,
        Guid offeredVersionId,
        string offeredContentHash,
        string sku,
        CancellationToken cancellationToken);
}

/// <summary>
/// Stores immutable content versions and their current pointer in the same
/// SQLite database as the economy ledger. Publishing and rollback only expose
/// a version after the complete document and pointer commit together.
/// </summary>
public sealed class SqliteEconomyContentStore : IEconomyContentStore, IDisposable, IAsyncDisposable
{
    private const int ComponentSchemaVersion = 1;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _connectionString;
    private readonly EconomyContentDefinitionValidator _validator;
    private readonly TimeProvider _timeProvider;
    private readonly IContentPublishFaultInjector? _faultInjector;
    private bool _disposed;

    public SqliteEconomyContentStore(
        IOptions<ExtractionPersistenceOptions> options,
        IWebHostEnvironment environment,
        TimeProvider timeProvider)
        : this(ResolveDataDirectory(options.Value.DataDirectory, environment.ContentRootPath), timeProvider)
    {
    }

    public SqliteEconomyContentStore(
        string dataDirectory,
        TimeProvider? timeProvider = null,
        IContentPublishFaultInjector? faultInjector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var fullPath = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(fullPath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(fullPath, "extraction-commerce.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
            ForeignKeys = true
        }.ToString();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _validator = new EconomyContentDefinitionValidator(_timeProvider);
        _faultInjector = faultInjector;
        Initialize();
    }

    public async Task<EconomyContentDraft> CreateDraftAsync(
        string serverId,
        string name,
        Guid? basedOnVersionId,
        EconomyContentDefinition definition,
        string actor,
        CancellationToken cancellationToken)
    {
        var normalizedServer = NormalizeKey(serverId, 64, nameof(serverId));
        var normalizedName = NormalizeText(name, 128, nameof(name));
        var normalizedActor = NormalizeText(actor, 128, nameof(actor));
        var normalized = EconomyContentCanonicalizer.Normalize(definition);
        EnsureDocumentServer(normalizedServer, normalized.ServerId);
        var json = EconomyContentCanonicalizer.Serialize(normalized);
        var hash = Hash(json);
        var now = _timeProvider.GetUtcNow();
        var draftId = Guid.NewGuid();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureNotDisposed();
            await using var connection = await OpenAsync(cancellationToken);
            if (basedOnVersionId is Guid baseId)
            {
                var baseVersion = await LoadVersionAsync(connection, null, baseId, cancellationToken)
                    ?? throw new ContentStoreException("CONTENT_VERSION_NOT_FOUND", "The base content version does not exist.");
                EnsureDocumentServer(normalizedServer, baseVersion.ServerId);
            }
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO content_drafts (
                    draft_id, server_id, name, state, based_on_version_id,
                    revision, content_hash, document_json, validation_json,
                    published_version_id, created_by, updated_by, created_at, updated_at)
                VALUES (
                    $draftId, $serverId, $name, 'draft', $basedOnVersionId,
                    0, $contentHash, $documentJson, NULL,
                    NULL, $actor, $actor, $now, $now);
                """;
            command.Parameters.AddWithValue("$draftId", draftId.ToString("D"));
            command.Parameters.AddWithValue("$serverId", normalizedServer);
            command.Parameters.AddWithValue("$name", normalizedName);
            command.Parameters.AddWithValue("$basedOnVersionId", DbValue(basedOnVersionId));
            command.Parameters.AddWithValue("$contentHash", hash);
            command.Parameters.AddWithValue("$documentJson", json);
            command.Parameters.AddWithValue("$actor", normalizedActor);
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
            return (await LoadDraftAsync(connection, null, draftId, cancellationToken))!;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EconomyContentDraft?> GetDraftAsync(Guid draftId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureNotDisposed();
            await using var connection = await OpenAsync(cancellationToken);
            return await LoadDraftAsync(connection, null, draftId, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<EconomyContentDraft>> ListDraftsAsync(
        string serverId,
        bool includePublished,
        CancellationToken cancellationToken)
    {
        var normalizedServer = NormalizeKey(serverId, 64, nameof(serverId));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureNotDisposed();
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT draft_id
                FROM content_drafts
                WHERE server_id = $serverId COLLATE NOCASE
                  AND ($includePublished = 1 OR state = 'draft')
                ORDER BY updated_at DESC, draft_id;
                """;
            command.Parameters.AddWithValue("$serverId", normalizedServer);
            command.Parameters.AddWithValue("$includePublished", includePublished ? 1 : 0);
            var ids = new List<Guid>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    ids.Add(Guid.Parse(reader.GetString(0)));
                }
            }
            var result = new List<EconomyContentDraft>(ids.Count);
            foreach (var id in ids)
            {
                result.Add((await LoadDraftAsync(connection, null, id, cancellationToken))!);
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EconomyContentDraft> UpdateDraftAsync(
        Guid draftId,
        long expectedRevision,
        EconomyContentDefinition definition,
        string actor,
        CancellationToken cancellationToken)
    {
        var normalizedActor = NormalizeText(actor, 128, nameof(actor));
        var normalized = EconomyContentCanonicalizer.Normalize(definition);
        var json = EconomyContentCanonicalizer.Serialize(normalized);
        var hash = Hash(json);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureNotDisposed();
            await using var connection = await OpenAsync(cancellationToken);
            var draft = await LoadDraftAsync(connection, null, draftId, cancellationToken)
                ?? throw new ContentStoreException("CONTENT_DRAFT_NOT_FOUND", "The content draft does not exist.");
            EnsureDraftEditable(draft, expectedRevision);
            EnsureDocumentServer(draft.ServerId, normalized.ServerId);
            var now = _timeProvider.GetUtcNow();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE content_drafts
                SET revision = revision + 1,
                    content_hash = $contentHash,
                    document_json = $documentJson,
                    validation_json = NULL,
                    updated_by = $actor,
                    updated_at = $now
                WHERE draft_id = $draftId AND state = 'draft' AND revision = $expectedRevision;
                """;
            command.Parameters.AddWithValue("$contentHash", hash);
            command.Parameters.AddWithValue("$documentJson", json);
            command.Parameters.AddWithValue("$actor", normalizedActor);
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            command.Parameters.AddWithValue("$draftId", draftId.ToString("D"));
            command.Parameters.AddWithValue("$expectedRevision", expectedRevision);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new ContentStoreException("CONTENT_DRAFT_REVISION_CONFLICT", "The content draft changed concurrently.");
            }
            return (await LoadDraftAsync(connection, null, draftId, cancellationToken))!;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ContentValidationResult> ValidateDraftAsync(
        Guid draftId,
        long expectedRevision,
        EconomyContentValidationContext context,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureNotDisposed();
            await using var connection = await OpenAsync(cancellationToken);
            var draft = await LoadDraftAsync(connection, null, draftId, cancellationToken)
                ?? throw new ContentStoreException("CONTENT_DRAFT_NOT_FOUND", "The content draft does not exist.");
            if (draft.Revision != expectedRevision)
            {
                throw new ContentStoreException("CONTENT_DRAFT_REVISION_CONFLICT", "The content draft changed concurrently.");
            }
            var validation = _validator.Validate(draft.Definition, context);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE content_drafts
                SET validation_json = $validationJson
                WHERE draft_id = $draftId AND revision = $expectedRevision;
                """;
            command.Parameters.AddWithValue("$validationJson", JsonSerializer.Serialize(validation, EconomyContentJson.Options));
            command.Parameters.AddWithValue("$draftId", draftId.ToString("D"));
            command.Parameters.AddWithValue("$expectedRevision", expectedRevision);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new ContentStoreException("CONTENT_DRAFT_REVISION_CONFLICT", "The content draft changed concurrently.");
            }
            return validation;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ContentDiffEntry>> DiffDraftAsync(
        Guid draftId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureNotDisposed();
            await using var connection = await OpenAsync(cancellationToken);
            var draft = await LoadDraftAsync(connection, null, draftId, cancellationToken)
                ?? throw new ContentStoreException("CONTENT_DRAFT_NOT_FOUND", "The content draft does not exist.");
            EconomyContentDefinition? before = null;
            if (draft.BasedOnVersionId is Guid baseId)
            {
                before = (await LoadVersionAsync(connection, null, baseId, cancellationToken))?.Definition
                    ?? throw new ContentStoreException("CONTENT_BASE_VERSION_NOT_FOUND", "The draft base version no longer exists.");
            }
            return EconomyContentDiff.Create(before, draft.Definition);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ContentPublishResult> PublishAsync(
        Guid draftId,
        long expectedRevision,
        DateOnly businessDate,
        string idempotencyKey,
        string actor,
        EconomyContentValidationContext context,
        CancellationToken cancellationToken)
    {
        var normalizedKey = NormalizeIdempotencyKey(idempotencyKey);
        var normalizedActor = NormalizeText(actor, 128, nameof(actor));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureNotDisposed();
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var draft = await LoadDraftAsync(connection, transaction, draftId, cancellationToken)
                ?? throw new ContentStoreException("CONTENT_DRAFT_NOT_FOUND", "The content draft does not exist.");
            if (draft.Revision != expectedRevision)
            {
                throw new ContentStoreException("CONTENT_DRAFT_REVISION_CONFLICT", "The content draft changed concurrently.");
            }
            var requestHash = Hash($"publish|{draftId:D}|{expectedRevision}|{businessDate:yyyy-MM-dd}|{draft.ContentHash}");
            var scope = $"content.publish:{draft.ServerId.ToLowerInvariant()}:{normalizedKey}";
            var replayId = await FindIdempotencyAsync(connection, transaction, scope, requestHash, cancellationToken);
            if (replayId is Guid publishedId)
            {
                var replayVersion = await LoadVersionAsync(connection, transaction, publishedId, cancellationToken)
                    ?? throw new InvalidDataException("Content publish idempotency references a missing version.");
                var replayPointer = await LoadCurrentAsync(connection, transaction, draft.ServerId, cancellationToken)
                    ?? throw new InvalidDataException("Content publish idempotency has no current pointer.");
                await transaction.CommitAsync(cancellationToken);
                return new ContentPublishResult(replayVersion, replayPointer, false, false, true);
            }
            if (draft.State == ContentDraftState.Published)
            {
                var versionId = draft.PublishedVersionId
                    ?? throw new InvalidDataException("Published draft has no published version.");
                var publishedVersion = await LoadVersionAsync(connection, transaction, versionId, cancellationToken)
                    ?? throw new InvalidDataException("Published draft references a missing version.");
                var pointer = await LoadCurrentAsync(connection, transaction, draft.ServerId, cancellationToken)
                    ?? throw new InvalidDataException("Published draft has no content pointer.");
                if (pointer.VersionId != publishedVersion.VersionId)
                {
                    throw new ContentStoreException(
                        "CONTENT_DRAFT_ALREADY_PUBLISHED",
                        "This draft was already published and is no longer the current version.");
                }
                await InsertIdempotencyAsync(
                    connection,
                    transaction,
                    scope,
                    requestHash,
                    publishedVersion.VersionId,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new ContentPublishResult(publishedVersion, pointer, false, false, true);
            }
            if (draft.State != ContentDraftState.Draft)
            {
                throw new ContentStoreException("CONTENT_DRAFT_NOT_EDITABLE", "The content draft cannot be published.");
            }

            var currentBeforePublish = await LoadCurrentAsync(
                connection,
                transaction,
                draft.ServerId,
                cancellationToken);
            if (currentBeforePublish?.VersionId != draft.BasedOnVersionId)
            {
                throw new ContentStoreException(
                    "CONTENT_POINTER_CONFLICT",
                    "The current content version changed after this draft was created; create a new draft from the current version.");
            }

            var validation = _validator.Validate(draft.Definition, context);
            if (!validation.Valid)
            {
                throw new ContentValidationException(validation);
            }
            if (!string.Equals(validation.ContentHash, draft.ContentHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The stored draft hash does not match its canonical content.");
            }

            var existingVersion = await FindVersionAsync(
                connection,
                transaction,
                draft.ServerId,
                businessDate,
                draft.ContentHash,
                cancellationToken);
            var versionCreated = existingVersion is null;
            EconomyContentVersion version;
            if (existingVersion is null)
            {
                var versionNumber = await NextVersionNumberAsync(connection, transaction, draft.ServerId, cancellationToken);
                version = new EconomyContentVersion(
                    Guid.NewGuid(),
                    draft.ServerId,
                    versionNumber,
                    businessDate,
                    draft.Definition.Dependencies.RulesVersion,
                    draft.ContentHash,
                    draft.Definition,
                    draft.DraftId,
                    normalizedActor,
                    _timeProvider.GetUtcNow());
                await InsertVersionAsync(connection, transaction, version, cancellationToken);
                _faultInjector?.ThrowIfRequested(ContentPublishFaultPoint.AfterVersionInserted);
            }
            else
            {
                version = existingVersion;
            }

            var previous = currentBeforePublish;
            var pointerChanged = previous?.VersionId != version.VersionId;
            var now = _timeProvider.GetUtcNow();
            await UpsertPointerAsync(connection, transaction, version, now, cancellationToken);
            _faultInjector?.ThrowIfRequested(ContentPublishFaultPoint.AfterPointerUpdated);
            if (pointerChanged)
            {
                await InsertActivationAsync(
                    connection,
                    transaction,
                    draft.ServerId,
                    previous?.VersionId,
                    version.VersionId,
                    "publish",
                    normalizedActor,
                    now,
                    cancellationToken);
            }
            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE content_drafts
                    SET state = 'published',
                        validation_json = $validationJson,
                        published_version_id = $versionId,
                        updated_by = $actor,
                        updated_at = $now
                    WHERE draft_id = $draftId AND state = 'draft' AND revision = $expectedRevision;
                    """;
                update.Parameters.AddWithValue("$validationJson", JsonSerializer.Serialize(validation, EconomyContentJson.Options));
                update.Parameters.AddWithValue("$versionId", version.VersionId.ToString("D"));
                update.Parameters.AddWithValue("$actor", normalizedActor);
                update.Parameters.AddWithValue("$now", now.ToString("O"));
                update.Parameters.AddWithValue("$draftId", draftId.ToString("D"));
                update.Parameters.AddWithValue("$expectedRevision", expectedRevision);
                if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw new ContentStoreException("CONTENT_DRAFT_REVISION_CONFLICT", "The content draft changed concurrently.");
                }
            }
            await InsertIdempotencyAsync(connection, transaction, scope, requestHash, version.VersionId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ContentPublishResult(
                version,
                ToPointer(version, now),
                versionCreated,
                pointerChanged,
                false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PreparedContentPublish> PreparePublishAsync(
        Guid draftId,
        long expectedRevision,
        DateOnly businessDate,
        string idempotencyKey,
        string actor,
        EconomyContentValidationContext context,
        CancellationToken cancellationToken)
    {
        var normalizedKey = NormalizeIdempotencyKey(idempotencyKey);
        var normalizedActor = NormalizeText(actor, 128, nameof(actor));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureNotDisposed();
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var draft = await LoadDraftAsync(connection, transaction, draftId, cancellationToken)
                ?? throw new ContentStoreException("CONTENT_DRAFT_NOT_FOUND", "The content draft does not exist.");
            if (draft.Revision != expectedRevision)
            {
                throw new ContentStoreException("CONTENT_DRAFT_REVISION_CONFLICT", "The content draft changed concurrently.");
            }

            var requestHash = Hash(
                $"prepare-publish|{draftId:D}|{expectedRevision}|{businessDate:yyyy-MM-dd}|{draft.ContentHash}");
            var scope = $"content.prepare-publish:{draft.ServerId.ToLowerInvariant()}:{normalizedKey}";
            var replayId = await FindIdempotencyAsync(
                connection,
                transaction,
                scope,
                requestHash,
                cancellationToken);
            if (replayId is Guid replayVersionId)
            {
                var replayVersion = await LoadVersionAsync(
                    connection,
                    transaction,
                    replayVersionId,
                    cancellationToken)
                    ?? throw new InvalidDataException(
                        "Prepared content publish idempotency references a missing version.");
                await transaction.CommitAsync(cancellationToken);
                return new PreparedContentPublish(
                    replayVersion,
                    draft.BasedOnVersionId,
                    false,
                    true);
            }

            if (draft.State == ContentDraftState.Published)
            {
                var publishedVersionId = draft.PublishedVersionId
                    ?? throw new InvalidDataException("Published draft has no published version.");
                var publishedVersion = await LoadVersionAsync(
                    connection,
                    transaction,
                    publishedVersionId,
                    cancellationToken)
                    ?? throw new InvalidDataException(
                        "Published draft references a missing version.");
                await InsertIdempotencyAsync(
                    connection,
                    transaction,
                    scope,
                    requestHash,
                    publishedVersion.VersionId,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new PreparedContentPublish(
                    publishedVersion,
                    draft.BasedOnVersionId,
                    false,
                    true);
            }
            if (draft.State != ContentDraftState.Draft)
            {
                throw new ContentStoreException(
                    "CONTENT_DRAFT_NOT_EDITABLE",
                    "The content draft cannot be published.");
            }

            var current = await LoadCurrentAsync(
                connection,
                transaction,
                draft.ServerId,
                cancellationToken);
            if (current?.VersionId != draft.BasedOnVersionId)
            {
                throw new ContentStoreException(
                    "CONTENT_POINTER_CONFLICT",
                    "The current content version changed after this draft was created; create a new draft from the current version.");
            }

            var validation = _validator.Validate(draft.Definition, context);
            if (!validation.Valid)
            {
                throw new ContentValidationException(validation);
            }
            if (!string.Equals(validation.ContentHash, draft.ContentHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The stored draft hash does not match its canonical content.");
            }

            var existingVersion = await FindVersionAsync(
                connection,
                transaction,
                draft.ServerId,
                businessDate,
                draft.ContentHash,
                cancellationToken);
            var versionCreated = existingVersion is null;
            EconomyContentVersion version;
            if (existingVersion is null)
            {
                var versionNumber = await NextVersionNumberAsync(
                    connection,
                    transaction,
                    draft.ServerId,
                    cancellationToken);
                version = new EconomyContentVersion(
                    Guid.NewGuid(),
                    draft.ServerId,
                    versionNumber,
                    businessDate,
                    draft.Definition.Dependencies.RulesVersion,
                    draft.ContentHash,
                    draft.Definition,
                    draft.DraftId,
                    normalizedActor,
                    _timeProvider.GetUtcNow());
                await InsertVersionAsync(connection, transaction, version, cancellationToken);
                _faultInjector?.ThrowIfRequested(ContentPublishFaultPoint.AfterVersionInserted);
            }
            else
            {
                version = existingVersion;
            }

            var now = _timeProvider.GetUtcNow();
            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE content_drafts
                    SET state = 'published',
                        validation_json = $validationJson,
                        published_version_id = $versionId,
                        updated_by = $actor,
                        updated_at = $now
                    WHERE draft_id = $draftId AND state = 'draft' AND revision = $expectedRevision;
                    """;
                update.Parameters.AddWithValue(
                    "$validationJson",
                    JsonSerializer.Serialize(validation, EconomyContentJson.Options));
                update.Parameters.AddWithValue("$versionId", version.VersionId.ToString("D"));
                update.Parameters.AddWithValue("$actor", normalizedActor);
                update.Parameters.AddWithValue("$now", now.ToString("O"));
                update.Parameters.AddWithValue("$draftId", draftId.ToString("D"));
                update.Parameters.AddWithValue("$expectedRevision", expectedRevision);
                if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw new ContentStoreException(
                        "CONTENT_DRAFT_REVISION_CONFLICT",
                        "The content draft changed concurrently.");
                }
            }
            await InsertIdempotencyAsync(
                connection,
                transaction,
                scope,
                requestHash,
                version.VersionId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new PreparedContentPublish(
                version,
                draft.BasedOnVersionId,
                versionCreated,
                false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EconomyContentPointer?> GetCurrentAsync(
        string serverId,
        CancellationToken cancellationToken)
    {
        var normalizedServer = NormalizeKey(serverId, 64, nameof(serverId));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureNotDisposed();
            await using var connection = await OpenAsync(cancellationToken);
            return await LoadCurrentAsync(connection, null, normalizedServer, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EconomyContentVersion?> GetVersionAsync(
        Guid versionId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureNotDisposed();
            await using var connection = await OpenAsync(cancellationToken);
            return await LoadVersionAsync(connection, null, versionId, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<EconomyContentVersion>> ListVersionsAsync(
        string serverId,
        CancellationToken cancellationToken)
    {
        var normalizedServer = NormalizeKey(serverId, 64, nameof(serverId));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureNotDisposed();
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT version_id
                FROM content_versions
                WHERE server_id = $serverId COLLATE NOCASE
                ORDER BY version_number;
                """;
            command.Parameters.AddWithValue("$serverId", normalizedServer);
            var ids = new List<Guid>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    ids.Add(Guid.Parse(reader.GetString(0)));
                }
            }
            var result = new List<EconomyContentVersion>(ids.Count);
            foreach (var id in ids)
            {
                result.Add((await LoadVersionAsync(connection, null, id, cancellationToken))!);
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ContentRollbackResult> RollbackAsync(
        string serverId,
        Guid? targetVersionId,
        Guid expectedCurrentVersionId,
        string idempotencyKey,
        string actor,
        CancellationToken cancellationToken)
    {
        var normalizedServer = NormalizeKey(serverId, 64, nameof(serverId));
        var normalizedKey = NormalizeIdempotencyKey(idempotencyKey);
        var normalizedActor = NormalizeText(actor, 128, nameof(actor));
        var requestHash = Hash($"rollback|{normalizedServer}|{targetVersionId?.ToString("D") ?? "previous"}|{expectedCurrentVersionId:D}");
        var scope = $"content.rollback:{normalizedServer.ToLowerInvariant()}:{normalizedKey}";
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureNotDisposed();
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var replayId = await FindIdempotencyAsync(connection, transaction, scope, requestHash, cancellationToken);
            if (replayId is Guid replayVersionId)
            {
                var replayPointer = await LoadCurrentAsync(connection, transaction, normalizedServer, cancellationToken)
                    ?? throw new InvalidDataException("Rollback idempotency has no current pointer.");
                await transaction.CommitAsync(cancellationToken);
                return new ContentRollbackResult(replayPointer, expectedCurrentVersionId, false, true);
            }
            var current = await LoadCurrentAsync(connection, transaction, normalizedServer, cancellationToken)
                ?? throw new ContentStoreException("CONTENT_POINTER_NOT_FOUND", "No content version is currently published.");
            if (current.VersionId != expectedCurrentVersionId)
            {
                throw new ContentStoreException("CONTENT_POINTER_CONFLICT", "The current content pointer changed concurrently.");
            }
            var resolvedTarget = targetVersionId ?? await FindPreviousVersionIdAsync(
                connection, transaction, normalizedServer, current.VersionId, cancellationToken)
                ?? throw new ContentStoreException("CONTENT_PREVIOUS_VERSION_NOT_FOUND", "No previous complete content version exists.");
            var target = await LoadVersionAsync(connection, transaction, resolvedTarget, cancellationToken)
                ?? throw new ContentStoreException("CONTENT_VERSION_NOT_FOUND", "The rollback target does not exist.");
            EnsureDocumentServer(normalizedServer, target.ServerId);
            var changed = target.VersionId != current.VersionId;
            var now = _timeProvider.GetUtcNow();
            if (changed)
            {
                await UpsertPointerAsync(connection, transaction, target, now, cancellationToken);
                _faultInjector?.ThrowIfRequested(ContentPublishFaultPoint.AfterPointerUpdated);
                await InsertActivationAsync(
                    connection,
                    transaction,
                    normalizedServer,
                    current.VersionId,
                    target.VersionId,
                    "rollback",
                    normalizedActor,
                    now,
                    cancellationToken);
            }
            await InsertIdempotencyAsync(connection, transaction, scope, requestHash, target.VersionId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ContentRollbackResult(ToPointer(target, now), current.VersionId, changed, false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PreparedContentRollback> PrepareRollbackAsync(
        string serverId,
        Guid? targetVersionId,
        Guid expectedCurrentVersionId,
        string idempotencyKey,
        string actor,
        CancellationToken cancellationToken)
    {
        var normalizedServer = NormalizeKey(serverId, 64, nameof(serverId));
        var normalizedKey = NormalizeIdempotencyKey(idempotencyKey);
        _ = NormalizeText(actor, 128, nameof(actor));
        var requestHash = Hash(
            $"prepare-rollback|{normalizedServer}|{targetVersionId?.ToString("D") ?? "previous"}|{expectedCurrentVersionId:D}");
        var scope = $"content.prepare-rollback:{normalizedServer.ToLowerInvariant()}:{normalizedKey}";
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureNotDisposed();
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var replayId = await FindIdempotencyAsync(
                connection,
                transaction,
                scope,
                requestHash,
                cancellationToken);
            if (replayId is Guid replayVersionId)
            {
                var replayVersion = await LoadVersionAsync(
                    connection,
                    transaction,
                    replayVersionId,
                    cancellationToken)
                    ?? throw new InvalidDataException(
                        "Prepared content rollback idempotency references a missing version.");
                await transaction.CommitAsync(cancellationToken);
                return new PreparedContentRollback(
                    replayVersion,
                    expectedCurrentVersionId,
                    true);
            }

            var current = await LoadCurrentAsync(
                connection,
                transaction,
                normalizedServer,
                cancellationToken)
                ?? throw new ContentStoreException(
                    "CONTENT_POINTER_NOT_FOUND",
                    "No content version is currently published.");
            if (current.VersionId != expectedCurrentVersionId)
            {
                throw new ContentStoreException(
                    "CONTENT_POINTER_CONFLICT",
                    "The current content pointer changed concurrently.");
            }
            var resolvedTarget = targetVersionId ?? await FindPreviousVersionIdAsync(
                connection,
                transaction,
                normalizedServer,
                current.VersionId,
                cancellationToken)
                ?? throw new ContentStoreException(
                    "CONTENT_PREVIOUS_VERSION_NOT_FOUND",
                    "No previous complete content version exists.");
            var target = await LoadVersionAsync(
                connection,
                transaction,
                resolvedTarget,
                cancellationToken)
                ?? throw new ContentStoreException(
                    "CONTENT_VERSION_NOT_FOUND",
                    "The rollback target does not exist.");
            EnsureDocumentServer(normalizedServer, target.ServerId);
            await InsertIdempotencyAsync(
                connection,
                transaction,
                scope,
                requestHash,
                target.VersionId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new PreparedContentRollback(
                target,
                current.VersionId,
                false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ContentProductDefinition> ResolveCurrentProductAsync(
        string serverId,
        Guid offeredVersionId,
        string offeredContentHash,
        string sku,
        CancellationToken cancellationToken)
    {
        var normalizedServer = NormalizeKey(serverId, 64, nameof(serverId));
        var normalizedSku = NormalizeKey(sku, 64, nameof(sku)).ToUpperInvariant();
        var normalizedHash = NormalizeHash(offeredContentHash);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureNotDisposed();
            await using var connection = await OpenAsync(cancellationToken);
            var pointer = await LoadCurrentAsync(connection, null, normalizedServer, cancellationToken);
            if (pointer is null || pointer.VersionId != offeredVersionId ||
                !string.Equals(pointer.ContentHash, normalizedHash, StringComparison.Ordinal))
            {
                throw new ContentStoreException(
                    "OFFER_NOT_AVAILABLE",
                    "The offered content version is no longer current; refresh the catalog before purchasing.");
            }
            var version = await LoadVersionAsync(connection, null, pointer.VersionId, cancellationToken)
                ?? throw new InvalidDataException("The current content pointer references a missing version.");
            var product = version.Definition.Products.SingleOrDefault(item =>
                string.Equals(item.Sku, normalizedSku, StringComparison.OrdinalIgnoreCase));
            var now = _timeProvider.GetUtcNow();
            if (product is null || !product.Active ||
                (product.AvailableFrom is not null && now < product.AvailableFrom) ||
                (product.AvailableUntil is not null && now >= product.AvailableUntil))
            {
                throw new ContentStoreException("OFFER_NOT_AVAILABLE", "The offered product is not available.");
            }
            return product;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _gate.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void Initialize()
    {
        using var connection = Open();
        using (var durability = connection.CreateCommand())
        {
            durability.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;";
            durability.ExecuteNonQuery();
        }
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS content_schema_migrations (
                component TEXT PRIMARY KEY,
                version INTEGER NOT NULL CHECK (version > 0),
                applied_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS content_drafts (
                draft_id TEXT PRIMARY KEY,
                server_id TEXT NOT NULL COLLATE NOCASE,
                name TEXT NOT NULL,
                state TEXT NOT NULL CHECK (state IN ('draft', 'published', 'abandoned')),
                based_on_version_id TEXT NULL,
                revision INTEGER NOT NULL CHECK (revision >= 0),
                content_hash TEXT NOT NULL CHECK (length(content_hash) = 64),
                document_json TEXT NOT NULL,
                validation_json TEXT NULL,
                published_version_id TEXT NULL,
                created_by TEXT NOT NULL,
                updated_by TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_content_drafts_server_state
                ON content_drafts (server_id, state, updated_at);
            CREATE TABLE IF NOT EXISTS content_versions (
                version_id TEXT PRIMARY KEY,
                server_id TEXT NOT NULL COLLATE NOCASE,
                version_number INTEGER NOT NULL CHECK (version_number > 0),
                business_date TEXT NOT NULL,
                rules_version TEXT NOT NULL,
                content_hash TEXT NOT NULL CHECK (length(content_hash) = 64),
                document_json TEXT NOT NULL,
                source_draft_id TEXT NOT NULL,
                published_by TEXT NOT NULL,
                published_at TEXT NOT NULL,
                UNIQUE (server_id, version_number),
                UNIQUE (server_id, business_date, content_hash)
            );
            CREATE INDEX IF NOT EXISTS ix_content_versions_server_date
                ON content_versions (server_id, business_date, version_number);
            CREATE TABLE IF NOT EXISTS content_current (
                server_id TEXT PRIMARY KEY COLLATE NOCASE,
                version_id TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                FOREIGN KEY (version_id) REFERENCES content_versions(version_id)
            );
            CREATE TABLE IF NOT EXISTS content_activations (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                activation_id TEXT NOT NULL UNIQUE,
                server_id TEXT NOT NULL COLLATE NOCASE,
                previous_version_id TEXT NULL,
                version_id TEXT NOT NULL,
                reason TEXT NOT NULL CHECK (reason IN ('publish', 'rollback')),
                actor TEXT NOT NULL,
                activated_at TEXT NOT NULL,
                FOREIGN KEY (previous_version_id) REFERENCES content_versions(version_id),
                FOREIGN KEY (version_id) REFERENCES content_versions(version_id)
            );
            CREATE INDEX IF NOT EXISTS ix_content_activations_server_sequence
                ON content_activations (server_id, sequence DESC);
            CREATE TABLE IF NOT EXISTS content_idempotency (
                scope TEXT PRIMARY KEY,
                request_hash TEXT NOT NULL CHECK (length(request_hash) = 64),
                resource_id TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
        using var migration = connection.CreateCommand();
        migration.Transaction = transaction;
        migration.CommandText = """
            INSERT INTO content_schema_migrations (component, version, applied_at)
            VALUES ('economy-content', $version, $appliedAt)
            ON CONFLICT(component) DO NOTHING;
            """;
        migration.Parameters.AddWithValue("$version", ComponentSchemaVersion);
        migration.Parameters.AddWithValue("$appliedAt", _timeProvider.GetUtcNow().ToString("O"));
        migration.ExecuteNonQuery();
        using var verify = connection.CreateCommand();
        verify.Transaction = transaction;
        verify.CommandText = "SELECT version FROM content_schema_migrations WHERE component = 'economy-content';";
        var storedVersion = Convert.ToInt32(verify.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (storedVersion != ComponentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported economy content schema version {storedVersion}; expected {ComponentSchemaVersion}.");
        }
        transaction.Commit();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static async Task<EconomyContentDraft?> LoadDraftAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid draftId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT draft_id, server_id, name, state, based_on_version_id,
                   revision, content_hash, document_json, validation_json,
                   published_version_id, created_by, updated_by, created_at, updated_at
            FROM content_drafts
            WHERE draft_id = $draftId;
            """;
        command.Parameters.AddWithValue("$draftId", draftId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return ReadDraft(reader);
    }

    private static EconomyContentDraft ReadDraft(SqliteDataReader reader)
    {
        var definition = DeserializeDefinition(reader.GetString(7));
        var validation = reader.IsDBNull(8)
            ? null
            : JsonSerializer.Deserialize<ContentValidationResult>(reader.GetString(8), EconomyContentJson.Options)
              ?? throw new InvalidDataException("Stored content validation result is null.");
        return new EconomyContentDraft(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            DraftStateFromStorage(reader.GetString(3)),
            reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)),
            reader.GetInt64(5),
            reader.GetString(6),
            definition,
            validation,
            reader.IsDBNull(9) ? null : Guid.Parse(reader.GetString(9)),
            reader.GetString(10),
            reader.GetString(11),
            DateTimeOffset.Parse(reader.GetString(12), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(13), CultureInfo.InvariantCulture));
    }

    private static async Task<EconomyContentVersion?> LoadVersionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid versionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT version_id, server_id, version_number, business_date,
                   rules_version, content_hash, document_json, source_draft_id,
                   published_by, published_at
            FROM content_versions
            WHERE version_id = $versionId;
            """;
        command.Parameters.AddWithValue("$versionId", versionId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadVersion(reader) : null;
    }

    private static async Task<EconomyContentVersion?> FindVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string serverId,
        DateOnly businessDate,
        string contentHash,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT version_id, server_id, version_number, business_date,
                   rules_version, content_hash, document_json, source_draft_id,
                   published_by, published_at
            FROM content_versions
            WHERE server_id = $serverId COLLATE NOCASE
              AND business_date = $businessDate
              AND content_hash = $contentHash;
            """;
        command.Parameters.AddWithValue("$serverId", serverId);
        command.Parameters.AddWithValue("$businessDate", businessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$contentHash", contentHash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadVersion(reader) : null;
    }

    private static EconomyContentVersion ReadVersion(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.GetInt64(2),
        DateOnly.ParseExact(reader.GetString(3), "yyyy-MM-dd", CultureInfo.InvariantCulture),
        reader.GetString(4),
        reader.GetString(5),
        DeserializeDefinition(reader.GetString(6)),
        Guid.Parse(reader.GetString(7)),
        reader.GetString(8),
        DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture));

    private static async Task<EconomyContentPointer?> LoadCurrentAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string serverId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT v.version_id, v.server_id, v.version_number, v.business_date,
                   v.rules_version, v.content_hash, c.updated_at
            FROM content_current c
            JOIN content_versions v ON v.version_id = c.version_id
            WHERE c.server_id = $serverId COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$serverId", serverId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return new EconomyContentPointer(
            reader.GetString(1),
            Guid.Parse(reader.GetString(0)),
            reader.GetInt64(2),
            DateOnly.ParseExact(reader.GetString(3), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            reader.GetString(4),
            reader.GetString(5),
            DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture));
    }

    private static async Task InsertVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EconomyContentVersion version,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO content_versions (
                version_id, server_id, version_number, business_date,
                rules_version, content_hash, document_json, source_draft_id,
                published_by, published_at)
            VALUES (
                $versionId, $serverId, $versionNumber, $businessDate,
                $rulesVersion, $contentHash, $documentJson, $sourceDraftId,
                $publishedBy, $publishedAt);
            """;
        command.Parameters.AddWithValue("$versionId", version.VersionId.ToString("D"));
        command.Parameters.AddWithValue("$serverId", version.ServerId);
        command.Parameters.AddWithValue("$versionNumber", version.VersionNumber);
        command.Parameters.AddWithValue("$businessDate", version.BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$rulesVersion", version.RulesVersion);
        command.Parameters.AddWithValue("$contentHash", version.ContentHash);
        command.Parameters.AddWithValue("$documentJson", EconomyContentCanonicalizer.Serialize(version.Definition));
        command.Parameters.AddWithValue("$sourceDraftId", version.SourceDraftId.ToString("D"));
        command.Parameters.AddWithValue("$publishedBy", version.PublishedBy);
        command.Parameters.AddWithValue("$publishedAt", version.PublishedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> NextVersionNumberAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string serverId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COALESCE(MAX(version_number), 0) + 1
            FROM content_versions
            WHERE server_id = $serverId COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$serverId", serverId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task UpsertPointerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EconomyContentVersion version,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO content_current (server_id, version_id, updated_at)
            VALUES ($serverId, $versionId, $updatedAt)
            ON CONFLICT(server_id) DO UPDATE SET
                version_id = excluded.version_id,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$serverId", version.ServerId);
        command.Parameters.AddWithValue("$versionId", version.VersionId.ToString("D"));
        command.Parameters.AddWithValue("$updatedAt", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertActivationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string serverId,
        Guid? previousVersionId,
        Guid versionId,
        string reason,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO content_activations (
                activation_id, server_id, previous_version_id, version_id,
                reason, actor, activated_at)
            VALUES (
                $activationId, $serverId, $previousVersionId, $versionId,
                $reason, $actor, $activatedAt);
            """;
        command.Parameters.AddWithValue("$activationId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$serverId", serverId);
        command.Parameters.AddWithValue("$previousVersionId", DbValue(previousVersionId));
        command.Parameters.AddWithValue("$versionId", versionId.ToString("D"));
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$activatedAt", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Guid?> FindPreviousVersionIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string serverId,
        Guid currentVersionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT previous_version_id
            FROM content_activations
            WHERE server_id = $serverId COLLATE NOCASE
              AND version_id = $currentVersionId
              AND previous_version_id IS NOT NULL
            ORDER BY sequence DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$serverId", serverId);
        command.Parameters.AddWithValue("$currentVersionId", currentVersionId.ToString("D"));
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string id ? Guid.Parse(id) : null;
    }

    private async Task<Guid?> FindIdempotencyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string scope,
        string requestHash,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT request_hash, resource_id FROM content_idempotency WHERE scope = $scope;";
        command.Parameters.AddWithValue("$scope", scope);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        if (!string.Equals(reader.GetString(0), requestHash, StringComparison.Ordinal))
        {
            throw new ContentStoreException(
                "IDEMPOTENCY_CONFLICT",
                "The content idempotency key was already used for a different request.");
        }
        return Guid.Parse(reader.GetString(1));
    }

    private async Task InsertIdempotencyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string scope,
        string requestHash,
        Guid resourceId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO content_idempotency (scope, request_hash, resource_id, created_at)
            VALUES ($scope, $requestHash, $resourceId, $createdAt);
            """;
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$requestHash", requestHash);
        command.Parameters.AddWithValue("$resourceId", resourceId.ToString("D"));
        command.Parameters.AddWithValue("$createdAt", _timeProvider.GetUtcNow().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static EconomyContentDefinition DeserializeDefinition(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<EconomyContentDefinition>(json, EconomyContentJson.Options)
                ?? throw new JsonException("Content definition is null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Stored economy content definition is invalid.", exception);
        }
    }

    private static EconomyContentPointer ToPointer(EconomyContentVersion version, DateTimeOffset updatedAt) => new(
        version.ServerId,
        version.VersionId,
        version.VersionNumber,
        version.BusinessDate,
        version.RulesVersion,
        version.ContentHash,
        updatedAt);

    private static ContentDraftState DraftStateFromStorage(string value) => value switch
    {
        "draft" => ContentDraftState.Draft,
        "published" => ContentDraftState.Published,
        "abandoned" => ContentDraftState.Abandoned,
        _ => throw new InvalidDataException($"Unknown content draft state '{value}'.")
    };

    private static void EnsureDraftEditable(EconomyContentDraft draft, long expectedRevision)
    {
        if (draft.State != ContentDraftState.Draft)
        {
            throw new ContentStoreException("CONTENT_DRAFT_NOT_EDITABLE", "Only draft content may be changed.");
        }
        if (draft.Revision != expectedRevision)
        {
            throw new ContentStoreException("CONTENT_DRAFT_REVISION_CONFLICT", "The content draft changed concurrently.");
        }
    }

    private static void EnsureDocumentServer(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new ContentStoreException(
                "CONTENT_SERVER_MISMATCH",
                "The content document does not belong to the requested server.");
        }
    }

    private static string NormalizeKey(string value, int maximumLength, string parameterName)
    {
        var normalized = NormalizeText(value, maximumLength, parameterName);
        if (normalized.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            throw new ArgumentException("Value contains unsupported characters.", parameterName);
        }
        return normalized;
    }

    private static string NormalizeText(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > maximumLength || normalized.Any(char.IsControl))
        {
            throw new ArgumentException($"Value must contain at most {maximumLength} non-control characters.", parameterName);
        }
        return normalized;
    }

    private static string NormalizeIdempotencyKey(string value) => NormalizeKey(value, 128, nameof(value));

    private static string NormalizeHash(string value)
    {
        var normalized = NormalizeText(value, 64, nameof(value)).ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new ArgumentException("Content hash must contain 64 hexadecimal characters.", nameof(value));
        }
        return normalized;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static object DbValue(Guid? value) => value is Guid id ? id.ToString("D") : DBNull.Value;

    private static string ResolveDataDirectory(string configured, string contentRoot) =>
        Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(contentRoot, configured));

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
