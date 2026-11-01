using System.Text.RegularExpressions;
using Dapper;
using Npgsql;
using Reconciliation.Core.Tenancy;

namespace Reconciliation.Core.Ingestion;

public sealed partial class BankStatementImporter(TenantContext tenant, IEnumerable<IBankStatementParser> parsers, IngestionOptions options)
{
    public IReadOnlyList<string> SupportedBanks => parsers.Select(parser => parser.Bank).Order(StringComparer.Ordinal).ToArray();

    public async Task<ImportReport> ImportAsync(Guid companyId, Guid actorId, string bank, string accountReference,
        IReadOnlyList<UploadedFile> files, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count > options.MaxFiles || files.Sum(file => (long)file.ContentLength) > options.MaxUploadBytes)
            throw new InvalidDataException("Upload exceeds file-count or total-size limit.");
        var uploadId = Guid.NewGuid();
        var results = new List<FileImportResult>();
        try
        {
            foreach (var file in files)
            {
                IReadOnlyList<BankStatementLine>? lines = null;
                string? reason = null;
                try
                {
                    if (file.Error is not null || file.Content is null) throw new InvalidDataException(file.Error ?? "Missing file bytes.");
                    if (file.ContentLength > options.MaxFileBytes) throw new InvalidDataException("File exceeds MaxFileBytes.");
                    var parser = parsers.SingleOrDefault(parser => parser.Bank == bank)
                        ?? throw new InvalidDataException("Unknown bank; supported banks: " + string.Join(", ", SupportedBanks));
                    if (accountReference is null || !AccountLabel().IsMatch(accountReference) || string.IsNullOrWhiteSpace(accountReference))
                        throw new InvalidDataException("accountReference must be a synthetic label of 1-40 letters, digits, spaces, hyphens or underscores.");
                    lines = parser.Parse(file.Content, file.ContentLength);
                }
                catch (InvalidDataException error) { reason = error.Message; }
                results.Add(await tenant.ExecuteWriteAsync(companyId, async (connection, transaction, ct) =>
                {
                    var status = reason is null ? "imported" : "rejected";
                    var fileId = await ImportStorage.InsertFileAsync(connection, transaction, companyId, uploadId, file, "bank-statement", status == "imported" ? "importing" : status, reason, options.RetentionYears, ct);
                    var imported = 0;
                    var duplicates = 0;
                    if (lines is not null)
                    {
                        await connection.ExecuteAsync(new CommandDefinition("""
                        INSERT INTO bank_account(company_id, bank, account_reference) VALUES (@companyId, @bank, @accountReference)
                        ON CONFLICT (company_id, bank, account_reference) DO NOTHING
                        """, new { companyId, bank, accountReference }, transaction, cancellationToken: ct));
                        var accountId = await connection.QuerySingleAsync<Guid>(new CommandDefinition(
                            "SELECT id FROM bank_account WHERE bank = @bank AND account_reference = @accountReference", new { bank, accountReference }, transaction, cancellationToken: ct));
                        foreach (var chunk in lines.Chunk(5000))
                        {
                            imported += await connection.ExecuteAsync(new CommandDefinition("""
                            INSERT INTO bank_movement(company_id, bank_account_id, import_file_id, movement_date, description, credit, debit, balance, reference)
                            SELECT @companyId, @accountId, @fileId, * FROM unnest(@dates::date[], @descriptions::text[],
                              @credits::numeric[], @debits::numeric[], @balances::numeric[], @references::text[])
                            ON CONFLICT (company_id, bank_account_id, movement_date, description, credit, debit, balance) DO NOTHING
                            """, new
                            {
                                companyId,
                                accountId,
                                fileId,
                                dates = chunk.Select(line => line.MovementDate).ToArray(),
                                descriptions = chunk.Select(line => line.Description).ToArray(),
                                credits = chunk.Select(line => line.Credit).ToArray(),
                                debits = chunk.Select(line => line.Debit).ToArray(),
                                balances = chunk.Select(line => line.Balance).ToArray(),
                                references = chunk.Select(line => line.Reference).ToArray()
                            }, transaction, cancellationToken: ct));
                        }
                        duplicates = lines.Count - imported;
                        await ImportStorage.FinalizeAsync(connection, transaction, fileId, file, options.RetentionYears, imported, duplicates, ct);
                    }
                    await ImportStorage.AuditAsync(connection, transaction, companyId, actorId, "bank-statement." + status, fileId, ct);
                    return new FileImportResult(fileId, file.Name, status, reason, ImportedMovements: imported, DuplicateMovements: duplicates);
                }, cancellationToken));
            }
        }
        catch (Exception error) when (error is NpgsqlException or CompanyReadOnlyException)
        {
            throw new UploadFailedException(uploadId, error);
        }
        return new ImportReport(uploadId, results);
    }

    [GeneratedRegex(@"\A[A-Za-z0-9 _-]{1,40}\z", RegexOptions.CultureInvariant)]
    private static partial Regex AccountLabel();
}
