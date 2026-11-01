using System.Text.RegularExpressions;
using Dapper;
using Npgsql;
using Reconciliation.Core.Tenancy;

namespace Reconciliation.Core.Ingestion;

public sealed partial class BankStatementImporter(TenantContext companyScope, IEnumerable<IBankStatementParser> statementParsers, IngestionOptions options)
{
    public IReadOnlyList<string> SupportedBanks => statementParsers.Select(statementParser => statementParser.Bank).Order(StringComparer.Ordinal).ToArray();

    public async Task<ImportReport> ImportAsync(Guid companyId, Guid actorId, string bank, string accountReference,
        IReadOnlyList<UploadedFile> files, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count > options.MaxFiles || files.Sum(uploadedStatement => (long)uploadedStatement.ContentLength) > options.MaxUploadBytes)
            throw new InvalidDataException("Upload exceeds file-count or total-size limit.");
        var uploadId = Guid.NewGuid();
        var fileOutcomes = new List<FileImportResult>();
        try
        {
            foreach (var uploadedStatement in files)
            {
                IReadOnlyList<BankStatementLine>? parsedLines = null;
                string? rejectionReason = null;
                try
                {
                    if (uploadedStatement.Error is not null || uploadedStatement.Content is null) throw new InvalidDataException(uploadedStatement.Error ?? "Missing file bytes.");
                    if (uploadedStatement.ContentLength > options.MaxFileBytes) throw new InvalidDataException("File exceeds MaxFileBytes.");
                    var statementParser = statementParsers.SingleOrDefault(statementParser => statementParser.Bank == bank)
                        ?? throw new InvalidDataException("Unknown bank; supported banks: " + string.Join(", ", SupportedBanks));
                    if (accountReference is null || !AccountLabel().IsMatch(accountReference) || string.IsNullOrWhiteSpace(accountReference))
                        throw new InvalidDataException("accountReference must be a synthetic label of 1-40 letters, digits, spaces, hyphens or underscores.");
                    parsedLines = statementParser.Parse(uploadedStatement.Content, uploadedStatement.ContentLength);
                }
                catch (InvalidDataException error) { rejectionReason = error.Message; }
                fileOutcomes.Add(await companyScope.ExecuteWriteAsync(companyId, async (connection, transaction, transactionCancellationToken) =>
                {
                    var importStatus = rejectionReason is null ? "imported" : "rejected";
                    var fileId = await ImportStorage.InsertFileAsync(connection, transaction, companyId, uploadId, uploadedStatement, "bank-statement", importStatus == "imported" ? "importing" : importStatus, rejectionReason, options.RetentionYears, transactionCancellationToken);
                    var newMovements = 0;
                    var duplicateMovements = 0;
                    if (parsedLines is not null)
                    {
                        await connection.ExecuteAsync(new CommandDefinition("""
                        INSERT INTO bank_account(company_id, bank, account_reference) VALUES (@companyId, @bank, @accountReference)
                        ON CONFLICT (company_id, bank, account_reference) DO NOTHING
                        """, new { companyId, bank, accountReference }, transaction, cancellationToken: transactionCancellationToken));
                        var bankAccountId = await connection.QuerySingleAsync<Guid>(new CommandDefinition(
                            "SELECT id FROM bank_account WHERE bank = @bank AND account_reference = @accountReference", new { bank, accountReference }, transaction, cancellationToken: transactionCancellationToken));
                        foreach (var chunk in parsedLines.Chunk(5000))
                        {
                            newMovements += await connection.ExecuteAsync(new CommandDefinition("""
                            INSERT INTO bank_movement(company_id, bank_account_id, import_file_id, movement_date, description, credit, debit, balance, reference)
                            SELECT @companyId, @accountId, @fileId, * FROM unnest(@dates::date[], @descriptions::text[],
                              @credits::numeric[], @debits::numeric[], @balances::numeric[], @references::text[])
                            ON CONFLICT (company_id, bank_account_id, movement_date, description, credit, debit, balance) DO NOTHING
                            """, new
                            {
                                companyId,
                                accountId = bankAccountId,
                                fileId,
                                dates = chunk.Select(statementLine => statementLine.MovementDate).ToArray(),
                                descriptions = chunk.Select(statementLine => statementLine.Description).ToArray(),
                                credits = chunk.Select(statementLine => statementLine.Credit).ToArray(),
                                debits = chunk.Select(statementLine => statementLine.Debit).ToArray(),
                                balances = chunk.Select(statementLine => statementLine.Balance).ToArray(),
                                references = chunk.Select(statementLine => statementLine.Reference).ToArray()
                            }, transaction, cancellationToken: transactionCancellationToken));
                        }
                        duplicateMovements = parsedLines.Count - newMovements;
                        await ImportStorage.FinalizeAsync(connection, transaction, fileId, uploadedStatement, options.RetentionYears, newMovements, duplicateMovements, transactionCancellationToken);
                    }
                    await ImportStorage.AuditAsync(connection, transaction, companyId, actorId, "bank-statement." + importStatus, fileId, transactionCancellationToken);
                    return new FileImportResult(fileId, uploadedStatement.Name, importStatus, rejectionReason, ImportedMovements: newMovements, DuplicateMovements: duplicateMovements);
                }, cancellationToken));
            }
        }
        catch (Exception error) when (error is NpgsqlException or CompanyReadOnlyException)
        {
            throw new UploadFailedException(uploadId, error);
        }
        return new ImportReport(uploadId, fileOutcomes);
    }

    [GeneratedRegex(@"\A[A-Za-z0-9 _-]{1,40}\z", RegexOptions.CultureInvariant)]
    private static partial Regex AccountLabel();
}
