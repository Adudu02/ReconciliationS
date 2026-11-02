using System.Text.Json;
using Dapper;
using Npgsql;
using Reconciliation.Core.Jobs;
using Reconciliation.Core.Tenancy;
using static Reconciliation.Core.Common.DapperExtensions;

namespace Reconciliation.Core.Ingestion;

public sealed class ImportJobService(CompanyScope companyScope, IngestionOptions ingestionOptions, JobOptions jobOptions,
    TimeProvider clock, CfdiImporter cfdiImporter, BankStatementImporter statementImporter)
{
    public Task<QueuedImport> EnqueueAsync(Guid companyId, Guid actorId, string jobType, IReadOnlyList<UploadedFile> files,
        string? bank, string? accountReference, CancellationToken cancellationToken)
    {
        ingestionOptions.EnsureWithinLimits(files);
        if (jobType is not ("import_cfdi" or "import_statement")) throw new InvalidDataException("Unsupported import job type.");
        var uploadId = Guid.NewGuid();
        return companyScope.ExecuteWriteAsync(companyId, async (connection, transaction, transactionCancellationToken) =>
        {
            for (var index = 0; index < files.Count; index++)
                await ImportFileStore.QueueAsync(connection, transaction, companyId, uploadId, files[index],
                    jobType == "import_cfdi" ? "cfdi" : "bank-statement", index, ingestionOptions.MaxFileBytes, clock.GetUtcNow(), transactionCancellationToken);
            var jobId = await JobStore.EnqueueAsync(connection, transaction, companyId, actorId, jobType,
                JsonSerializer.Serialize(new { bank, accountReference }), uploadId, jobOptions.MaxAttempts, clock.GetUtcNow(), transactionCancellationToken, files.Count);
            return new QueuedImport(jobId, uploadId);
        }, cancellationToken);
    }

    public async Task<string?> ProcessAsync(JobRecord job, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        if (job.InputRef is null) throw new InvalidDataException("Import job is missing its upload reference.");
        var stored = await connection.QueryAsync<StoredUpload>(Command("""
            SELECT f.id,f.filename AS Name,coalesce(f.pending_original,f.original,o.original) AS Bytes,f.sha256 AS Hash,
              CASE WHEN f.status IN ('queued','processing','rejected') THEN f.reason ELSE NULL END AS Error
            FROM import_file f LEFT JOIN import_file o ON o.id=f.original_file_id
            WHERE f.upload_id=@uploadId ORDER BY f.created_at,f.file_index,f.id
            """, new { uploadId = job.InputRef }, transaction, cancellationToken: cancellationToken));
        var lease = new JobLease(job, clock);
        var files = stored.Select(file => new UploadedFile(file.Name, file.Bytes, file.Hash, file.Error, StoredFileId: file.Id, Lease: lease)).ToArray();
        if (files.Length == 0) throw new InvalidDataException("Import input was not found.");
        Task ReportProgressAsync(int done) => companyScope.ExecuteAsync(job.CompanyId, async (progressConnection, progressTransaction, progressCancellationToken) =>
        {
            await lease.ProgressAsync(progressConnection, progressTransaction, done, files.Length, progressCancellationToken);
            return true;
        }, cancellationToken);
        if (job.Type == "import_cfdi")
            await cfdiImporter.ImportAsync(job.CompanyId, job.CreatedBy, files, job.InputRef, ReportProgressAsync, cancellationToken);
        else
        {
            using var payload = JsonDocument.Parse(job.Payload);
            var bank = payload.RootElement.TryGetProperty("bank", out var bankField) ? bankField.GetString() ?? "" : "";
            var accountReference = payload.RootElement.TryGetProperty("accountReference", out var accountField) ? accountField.GetString() ?? "" : "";
            await statementImporter.ImportAsync(job.CompanyId, job.CreatedBy, bank, accountReference, files, job.InputRef, ReportProgressAsync, cancellationToken);
        }
        return null;
    }

    private sealed record StoredUpload(Guid Id, string Name, byte[]? Bytes, byte[] Hash, string? Error);
}

public sealed class CfdiImportJobHandler(ImportJobService imports) : IJobHandler
{
    public string Type => "import_cfdi";
    public Task<string?> HandleAsync(JobRecord job, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken) =>
        imports.ProcessAsync(job, connection, transaction, cancellationToken);
}

public sealed class StatementImportJobHandler(ImportJobService imports) : IJobHandler
{
    public string Type => "import_statement";
    public Task<string?> HandleAsync(JobRecord job, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken) =>
        imports.ProcessAsync(job, connection, transaction, cancellationToken);
}
