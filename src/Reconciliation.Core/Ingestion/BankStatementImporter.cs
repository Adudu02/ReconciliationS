using System.Text.RegularExpressions;
using Dapper;
using Npgsql;
using Reconciliation.Core.Tenancy;
using static Reconciliation.Core.Common.DapperExtensions;

namespace Reconciliation.Core.Ingestion;

public sealed partial class BankStatementImporter(TenantContext companyScope, IEnumerable<IBankStatementParser> statementParsers, IngestionOptions options)
{
    public IReadOnlyList<string> SupportedBanks => statementParsers.Select(statementParser => statementParser.Bank).Order(StringComparer.Ordinal).ToArray();

    public async Task<ImportReport> ImportAsync(Guid companyId, Guid actorId, string bank, string accountReference,
        IReadOnlyList<UploadedFile> files, CancellationToken cancellationToken = default)
    {
        options.EnsureWithinLimits(files);
        var statementParser = statementParsers.SingleOrDefault(parser => parser.Bank == bank);
        var uploadRejectionReason = statementParser is null ? "Unknown bank; supported banks: " + string.Join(", ", SupportedBanks)
            : accountReference is null || !AccountLabel().IsMatch(accountReference) || string.IsNullOrWhiteSpace(accountReference)
                ? "accountReference must be a synthetic label of 1-40 letters, digits, spaces, hyphens or underscores." : null;
        var uploadId = Guid.NewGuid();
        var fileOutcomes = new List<FileImportResult>();
        try
        {
            foreach (var uploadedStatement in files)
            {
                var (parsedLines, rejectionReason) = ParseStatement(uploadedStatement, statementParser, uploadRejectionReason);
                fileOutcomes.Add(await PersistStatementAsync(companyId, actorId, uploadId, bank, accountReference, uploadedStatement, parsedLines, rejectionReason, cancellationToken));
            }
        }
        catch (Exception error) when (error is NpgsqlException or CompanyReadOnlyException)
        {
            throw new UploadFailedException(uploadId, error);
        }
        return new ImportReport(uploadId, fileOutcomes);
    }

    private (IReadOnlyList<BankStatementLine>? ParsedLines, string? RejectionReason) ParseStatement(UploadedFile uploadedStatement,
        IBankStatementParser? statementParser, string? uploadRejectionReason)
    {
        IReadOnlyList<BankStatementLine>? parsedLines = null;
        string? rejectionReason = null;
        try
        {
            var fileBytes = uploadedStatement.RequireContent();
            if (uploadedStatement.ContentLength > options.MaxFileBytes) throw new InvalidDataException(UploadErrors.FileLimit);
            if (uploadRejectionReason is not null) throw new InvalidDataException(uploadRejectionReason);
            parsedLines = statementParser!.Parse(fileBytes, uploadedStatement.ContentLength);
        }
        catch (InvalidDataException error) { rejectionReason = error.Message; }
        return (parsedLines, rejectionReason);
    }

    private Task<FileImportResult> PersistStatementAsync(Guid companyId, Guid actorId, Guid uploadId, string bank, string? accountReference,
        UploadedFile uploadedStatement, IReadOnlyList<BankStatementLine>? parsedLines, string? rejectionReason, CancellationToken cancellationToken) =>
        companyScope.ExecuteWriteAsync(companyId, async (connection, transaction, transactionCancellationToken) =>
        {
            var importStatus = rejectionReason is null ? ImportStatus.Imported : ImportStatus.Rejected;
            var fileId = await ImportStorage.InsertFileAsync(connection, transaction, companyId, uploadId, uploadedStatement, ImportFileKind.BankStatement, importStatus == ImportStatus.Imported ? ImportStatus.Importing : importStatus, rejectionReason, options.RetentionYears, transactionCancellationToken);
            var newMovements = 0;
            var duplicateMovements = 0;
            if (parsedLines is not null)
            {
                await connection.ExecuteAsync(Command("""
                INSERT INTO bank_account(company_id, bank, account_reference) VALUES (@companyId, @bank, @accountReference)
                ON CONFLICT (company_id, bank, account_reference) DO NOTHING
                """, new { companyId, bank, accountReference }, transaction, cancellationToken: transactionCancellationToken));
                var bankAccountId = await connection.QuerySingleAsync<Guid>(Command(
                    "SELECT id FROM bank_account WHERE bank = @bank AND account_reference = @accountReference", new { bank, accountReference }, transaction, cancellationToken: transactionCancellationToken));
                foreach (var chunk in parsedLines.Chunk(5000))
                {
                    newMovements += await connection.ExecuteAsync(Command("""
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
            await ImportStorage.AuditAsync(connection, transaction, companyId, actorId, ImportFileKind.BankStatement.ToDatabaseValue() + "." + importStatus.ToDatabaseValue(), fileId, transactionCancellationToken);
            return new FileImportResult(fileId, uploadedStatement.Name, importStatus.ToDatabaseValue(), rejectionReason, ImportedMovements: newMovements, DuplicateMovements: duplicateMovements);
        }, cancellationToken);

    [GeneratedRegex(@"\A[A-Za-z0-9 _-]{1,40}\z", RegexOptions.CultureInvariant)]
    private static partial Regex AccountLabel();
}
