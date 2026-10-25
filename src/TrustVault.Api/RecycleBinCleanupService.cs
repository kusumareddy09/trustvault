
using Microsoft.EntityFrameworkCore;
using TrustVault.Api.Data;

namespace TrustVault.Api;

public sealed class RecycleBinCleanupService : BackgroundService
{
    // Documents are permanently deleted after 30 days.
    private static readonly TimeSpan RetentionPeriod =
        TimeSpan.FromDays(30);

    // Check for expired documents every 5 seconds.
    private static readonly TimeSpan CheckInterval =
        TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IWebHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RecycleBinCleanupService> _logger;

    public RecycleBinCleanupService(
        IServiceScopeFactory scopeFactory,
        IWebHostEnvironment environment,
        IConfiguration configuration,
        ILogger<RecycleBinCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _environment = environment;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DeleteExpiredDocumentsAsync(stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Recycle Bin cleanup failed. It will retry.");
            }

            try
            {
                await Task.Delay(
                    CheckInterval,
                    stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task DeleteExpiredDocumentsAsync(
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();

        var db = scope.ServiceProvider
            .GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;

        var deletedDocuments = await db.Documents
            .Where(d => d.IsDeleted)
            .ToListAsync(cancellationToken);

        foreach (var document in deletedDocuments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var deletedAt = document.DeletedAtUtc
                ?? document.UpdatedAtUtc;

            if (now - deletedAt < RetentionPeriod)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(document.StorageKey))
            {
                _logger.LogWarning(
                    "Skipping expired document {DocumentId}: storage key is empty.",
                    document.Id);

                continue;
            }

            var storageDirectory = PrivateDocumentStorage.GetDirectory(
                _environment,
                _configuration);

            var filePath = Path.GetFullPath(
                Path.Combine(
                    storageDirectory,
                    document.StorageKey));

            var expectedPrefix =
                storageDirectory.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            if (!filePath.StartsWith(
                    expectedPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError(
                    "Skipping document {DocumentId}: invalid storage path.",
                    document.Id);

                continue;
            }

            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch (IOException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not remove stored file for document {DocumentId}. Will retry.",
                    document.Id);

                continue;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Permission denied while removing document {DocumentId}. Will retry.",
                    document.Id);

                continue;
            }

            var documentId = document.Id;
            var ownerId = document.OwnerId;
            var fileName = document.OriginalFileName;

            var grants = await db.DocumentGrants
                .Where(g => g.DocumentId == documentId)
                .ToListAsync(cancellationToken);

            db.DocumentGrants.RemoveRange(grants);

            var auditEvents = await db.AuditEvents
                .Where(a => a.DocumentId == documentId)
                .ToListAsync(cancellationToken);

            foreach (var auditEvent in auditEvents)
            {
                auditEvent.DocumentId = null;

                var context =
                    $"Document={fileName}; DocumentId={documentId}";

                auditEvent.Details =
                    string.IsNullOrWhiteSpace(auditEvent.Details)
                        ? context
                        : $"{auditEvent.Details}; {context}";
            }

            db.Documents.Remove(document);

            db.AuditEvents.Add(new AuditEvent
            {
                ActorId = ownerId,
                DocumentId = null,
                Action = "DocumentAutoDeleted",
                Outcome = "Allowed",
                Details =
                    "Automatically permanently deleted after 30 days. " +
                    $"DocumentId={documentId}; FileName={fileName}"
            });

            try
            {
                await db.SaveChangesAsync(cancellationToken);

                _logger.LogInformation(
                    "Expired document {DocumentId} was permanently deleted.",
                    documentId);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(
                    ex,
                    "Database cleanup failed for document {DocumentId}.",
                    documentId);
            }
        }
    }
}
