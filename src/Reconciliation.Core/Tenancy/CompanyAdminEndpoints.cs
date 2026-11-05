using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using Reconciliation.Core.Audit;
using Reconciliation.Core.Common;
using static Reconciliation.Core.Common.DapperExtensions;

namespace Reconciliation.Core.Tenancy;

public sealed record CreateCompanyRequest(string Rfc, string Name, string AdminEmail, string? AdminPassword);
public sealed record CompanySettings(int DateToleranceDays, decimal AmountTolerance, bool ComplementNextBizDay);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SettingsChange(int? DateToleranceDays, decimal? AmountTolerance, bool? ComplementNextBizDay);

public static partial class CompanyAdminEndpoints
{
    [GeneratedRegex(@"\A[A-ZÑ&]{3,4}[0-9]{6}[A-Z0-9]{3}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RfcFormat();

    public static IEndpointRouteBuilder MapCompanyManagement(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/companies", CreateAsync).RequireAuthorization(Policies.PlatformOperator);
        routes.MapGet("/companies/{companyId:guid}/settings", (Guid companyId, CompanyScope companyScope, CancellationToken cancellationToken) =>
            companyScope.ExecuteResultAsync(companyId, async (connection, transaction, transactionCancellationToken) =>
            {
                var companySettings = await ReadSettingsAsync(connection, transaction, transactionCancellationToken);
                return CompanyScope.OkOrNotFound(companySettings);
            }, cancellationToken)).RequireAuthorization(Policies.CompanyMember);
        routes.MapPatch("/companies/{companyId:guid}/settings", ChangeSettingsAsync).RequireAuthorization(Policies.Administrator);
        routes.MapPost("/companies/{companyId:guid}/archive", (Guid companyId, HttpContext httpContext, CompanyScope companyScope, CancellationToken cancellationToken) =>
            companyScope.ExecuteResultAsync(companyId, async (connection, transaction, transactionCancellationToken) =>
            {
                var rowsInserted = await connection.ExecuteAsync(Command(
                    "UPDATE company SET archived_at = now() WHERE archived_at IS NULL", transaction: transaction, cancellationToken: transactionCancellationToken));
                if (rowsInserted > 0) await AuditLog.WriteAsync(connection, transaction, companyId, httpContext.User.UserId()!.Value, "company.archived", null, new { archived = true }, transactionCancellationToken);
                return Results.NoContent();
            }, cancellationToken)).RequireAuthorization(Policies.Administrator);
        return routes;
    }

    private static Task<IResult> CreateAsync(CreateCompanyRequest companyToCreate, HttpContext httpContext, CompanyScope companyScope,
        IPasswordHasher<string> passwordHasher, CancellationToken cancellationToken)
    {
        if (companyToCreate.Rfc is null || !RfcFormat().IsMatch(companyToCreate.Rfc))
            return Task.FromResult<IResult>(Results.BadRequest(new { error = "RFC has an invalid format." }));
        if (string.IsNullOrWhiteSpace(companyToCreate.Name) || !EmailAddressRules.IsValid(companyToCreate.AdminEmail))
            return Task.FromResult<IResult>(Results.BadRequest(new { error = "Name and valid administrator email are required." }));
        var companyId = Guid.NewGuid();
        return companyScope.ExecuteResultAsync(companyId, async (connection, transaction, transactionCancellationToken) =>
        {
            var adminUserId = await connection.QuerySingleOrDefaultAsync<Guid?>(Command(
                "SELECT id FROM app_user WHERE lower(email) = lower(@AdminEmail)", companyToCreate, transaction, cancellationToken: transactionCancellationToken));
            if (adminUserId is null)
            {
                if (companyToCreate.AdminPassword is null || companyToCreate.AdminPassword.Length < PasswordPolicy.MinimumLength)
                    return Results.BadRequest(new { error = "Administrator password requires at least 12 characters." });
                var adminPasswordHash = passwordHasher.HashPassword(companyToCreate.AdminEmail, companyToCreate.AdminPassword);
                // Concurrent creation may have inserted the email; attach without changing that password.
                await connection.ExecuteAsync(Command("""
                    INSERT INTO app_user(email, password_hash) VALUES (@AdminEmail, @hash)
                    ON CONFLICT (lower(email)) DO NOTHING
                    """, new { companyToCreate.AdminEmail, hash = adminPasswordHash }, transaction, cancellationToken: transactionCancellationToken));
                adminUserId = await connection.QuerySingleAsync<Guid>(Command(
                    "SELECT id FROM app_user WHERE lower(email) = lower(@AdminEmail)", companyToCreate, transaction, cancellationToken: transactionCancellationToken));
            }
            await connection.ExecuteAsync(Command("""
                INSERT INTO company(id, rfc, name) VALUES (@id, @Rfc, @Name);
                INSERT INTO membership(company_id, user_id, role) VALUES (@id, @admin, 'administrator');
                """, new { id = companyId, companyToCreate.Rfc, companyToCreate.Name, admin = adminUserId }, transaction, cancellationToken: transactionCancellationToken));
            var company = new CompanyResponse(companyId, companyToCreate.Rfc, companyToCreate.Name, null);
            await AuditLog.WriteAsync(connection, transaction, companyId, httpContext.User.UserId()!.Value, "company.created", null, company, transactionCancellationToken, [new("user", adminUserId.Value.ToString())]);
            return Results.Created($"/companies/{companyId}", company);
        }, cancellationToken);
    }

    private static Task<IResult> ChangeSettingsAsync(Guid companyId, SettingsChange settingsChange, HttpContext httpContext, CompanyScope companyScope, CancellationToken cancellationToken) =>
        companyScope.ExecuteWriteResultAsync(companyId, async (connection, transaction, transactionCancellationToken) =>
        {
            if (settingsChange.DateToleranceDays is < 0 or > 366 || settingsChange.AmountTolerance < 0 || settingsChange.AmountTolerance > 1.00m ||
                (settingsChange.AmountTolerance is decimal amount && !Money.HasAtMostTwoDecimals(amount)))
                return Results.BadRequest(new { error = "Tolerances must be nonnegative; amount tolerance must be at most 1.00 with at most two decimals." });
            var before = await ReadSettingsForUpdateAsync(connection, transaction, transactionCancellationToken);
            if (before is null) return Results.NotFound();
            var after = new CompanySettings(settingsChange.DateToleranceDays ?? before.DateToleranceDays,
                settingsChange.AmountTolerance ?? before.AmountTolerance, settingsChange.ComplementNextBizDay ?? before.ComplementNextBizDay);
            if (after != before)
            {
                await connection.ExecuteAsync(Command("""
                    UPDATE company SET date_tolerance_days = @DateToleranceDays,
                      amount_tolerance = @AmountTolerance, complement_next_biz_day = @ComplementNextBizDay
                    """, after, transaction, cancellationToken: transactionCancellationToken));
                await AuditLog.WriteAsync(connection, transaction, companyId, httpContext.User.UserId()!.Value, "company.settings_changed", before, after, transactionCancellationToken);
            }
            return Results.Ok(after);
        }, cancellationToken);

    private static Task<CompanySettings?> ReadSettingsForUpdateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken) =>
        ReadSettingsQueryAsync(connection, transaction, " FOR UPDATE", cancellationToken);

    private static Task<CompanySettings?> ReadSettingsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken) =>
        ReadSettingsQueryAsync(connection, transaction, "", cancellationToken);

    private static Task<CompanySettings?> ReadSettingsQueryAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string lockClause, CancellationToken cancellationToken) =>
        connection.QuerySingleOrDefaultAsync<CompanySettings>(Command("""
            SELECT date_tolerance_days AS DateToleranceDays, amount_tolerance AS AmountTolerance,
              complement_next_biz_day AS ComplementNextBizDay FROM company
            """ + lockClause, transaction: transaction, cancellationToken: cancellationToken));

}
