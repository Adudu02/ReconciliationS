using System.IdentityModel.Tokens.Jwt;
using System.Net.Mail;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace Reconciliation.Core.Tenancy;

public sealed record CreateCompanyRequest(string Rfc, string Name, string AdminEmail, string? AdminPassword);
public sealed record CompanySettings(int DateToleranceDays, decimal AmountTolerance, bool ComplementNextBizDay);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SettingsChange(int? DateToleranceDays, decimal? AmountTolerance, bool? ComplementNextBizDay);

public static partial class CompanyManagement
{
    [GeneratedRegex(@"\A[A-ZÑ&]{3,4}[0-9]{6}[A-Z0-9]{3}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RfcFormat();

    public static IEndpointRouteBuilder MapCompanyManagement(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/companies", CreateAsync).RequireAuthorization("platform-operator");
        routes.MapGet("/companies/{companyId:guid}/settings", (Guid companyId, TenantContext companyScope, CancellationToken cancellationToken) =>
            companyScope.ExecuteAsync<IResult>(companyId, async (connection, transaction, transactionCancellationToken) =>
            {
                var companySettings = await ReadSettingsAsync(connection, transaction, transactionCancellationToken);
                return companySettings is null ? Results.NotFound() : Results.Ok(companySettings);
            }, cancellationToken)).RequireAuthorization("company-member");
        routes.MapPatch("/companies/{companyId:guid}/settings", ChangeSettingsAsync).RequireAuthorization("administrator");
        routes.MapPost("/companies/{companyId:guid}/archive", (Guid companyId, HttpContext httpContext, TenantContext companyScope, CancellationToken cancellationToken) =>
            companyScope.ExecuteAsync<IResult>(companyId, async (connection, transaction, transactionCancellationToken) =>
            {
                var rowsInserted = await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE company SET archived_at = now() WHERE archived_at IS NULL", transaction: transaction, cancellationToken: transactionCancellationToken));
                if (rowsInserted > 0) await AuditAsync(connection, transaction, companyId, httpContext, "company.archived", null, new { archived = true }, transactionCancellationToken);
                return Results.NoContent();
            }, cancellationToken)).RequireAuthorization("administrator");
        return routes;
    }

    private static Task<IResult> CreateAsync(CreateCompanyRequest companyToCreate, HttpContext httpContext, TenantContext companyScope,
        IPasswordHasher<string> passwordHasher, CancellationToken cancellationToken)
    {
        if (companyToCreate.Rfc is null || !RfcFormat().IsMatch(companyToCreate.Rfc))
            return Task.FromResult<IResult>(Results.BadRequest(new { error = "RFC has an invalid format." }));
        if (string.IsNullOrWhiteSpace(companyToCreate.Name) || !MailAddress.TryCreate(companyToCreate.AdminEmail, out var parsedEmail) || parsedEmail.Address != companyToCreate.AdminEmail)
            return Task.FromResult<IResult>(Results.BadRequest(new { error = "Name and valid administrator email are required." }));
        var companyId = Guid.NewGuid();
        return companyScope.ExecuteAsync<IResult>(companyId, async (connection, transaction, transactionCancellationToken) =>
        {
            var adminUserId = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
                "SELECT id FROM app_user WHERE lower(email) = lower(@AdminEmail)", companyToCreate, transaction, cancellationToken: transactionCancellationToken));
            if (adminUserId is null)
            {
                if (companyToCreate.AdminPassword is null || companyToCreate.AdminPassword.Length < 12)
                    return Results.BadRequest(new { error = "Administrator password requires at least 12 characters." });
                var adminPasswordHash = passwordHasher.HashPassword(companyToCreate.AdminEmail, companyToCreate.AdminPassword);
                // Concurrent creation may have inserted the email; attach without changing that password.
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO app_user(email, password_hash) VALUES (@AdminEmail, @hash)
                    ON CONFLICT (lower(email)) DO NOTHING
                    """, new { companyToCreate.AdminEmail, hash = adminPasswordHash }, transaction, cancellationToken: transactionCancellationToken));
                adminUserId = await connection.QuerySingleAsync<Guid>(new CommandDefinition(
                    "SELECT id FROM app_user WHERE lower(email) = lower(@AdminEmail)", companyToCreate, transaction, cancellationToken: transactionCancellationToken));
            }
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO company(id, rfc, name) VALUES (@id, @Rfc, @Name);
                INSERT INTO membership(company_id, user_id, role) VALUES (@id, @admin, 'administrator');
                """, new { id = companyId, companyToCreate.Rfc, companyToCreate.Name, admin = adminUserId }, transaction, cancellationToken: transactionCancellationToken));
            var company = new CompanyResponse(companyId, companyToCreate.Rfc, companyToCreate.Name, null);
            await AuditAsync(connection, transaction, companyId, httpContext, "company.created", null, company, transactionCancellationToken);
            return Results.Created($"/companies/{companyId}", company);
        }, cancellationToken);
    }

    private static Task<IResult> ChangeSettingsAsync(Guid companyId, SettingsChange settingsChange, HttpContext httpContext, TenantContext companyScope, CancellationToken cancellationToken) =>
        companyScope.ExecuteWriteAsync<IResult>(companyId, async (connection, transaction, transactionCancellationToken) =>
        {
            if (settingsChange.DateToleranceDays is < 0 or > 366 || settingsChange.AmountTolerance < 0 || settingsChange.AmountTolerance > 9999999999999999.99m ||
                (settingsChange.AmountTolerance is decimal amount && decimal.Round(amount, 2) != amount))
                return Results.BadRequest(new { error = "Tolerances must be nonnegative; amount must fit numeric(18,2)." });
            var before = await ReadSettingsAsync(connection, transaction, transactionCancellationToken, forUpdate: true);
            if (before is null) return Results.NotFound();
            var after = new CompanySettings(settingsChange.DateToleranceDays ?? before.DateToleranceDays,
                settingsChange.AmountTolerance ?? before.AmountTolerance, settingsChange.ComplementNextBizDay ?? before.ComplementNextBizDay);
            if (after != before)
            {
                await connection.ExecuteAsync(new CommandDefinition("""
                    UPDATE company SET date_tolerance_days = @DateToleranceDays,
                      amount_tolerance = @AmountTolerance, complement_next_biz_day = @ComplementNextBizDay
                    """, after, transaction, cancellationToken: transactionCancellationToken));
                await AuditAsync(connection, transaction, companyId, httpContext, "company.settings_changed", before, after, transactionCancellationToken);
            }
            return Results.Ok(after);
        }, cancellationToken);

    private static Task<CompanySettings?> ReadSettingsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken, bool forUpdate = false) =>
        connection.QuerySingleOrDefaultAsync<CompanySettings>(new CommandDefinition("""
            SELECT date_tolerance_days AS DateToleranceDays, amount_tolerance AS AmountTolerance,
              complement_next_biz_day AS ComplementNextBizDay FROM company
            """ + (forUpdate ? " FOR UPDATE" : ""), transaction: transaction, cancellationToken: cancellationToken));

    // shortcut: minimal transactional tenancy audit; group 5 adds hash chaining and rejection triggers.
    private static Task<int> AuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid companyId, HttpContext httpContext,
        string auditAction, object? previousValues, object currentValues, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO audit_entry(company_id, actor_id, action, old_values, new_values)
            VALUES (@companyId, @actor::uuid, @action, @oldValues::jsonb, @newValues::jsonb)
            """, new
        {
            companyId,
            actor = httpContext.User.FindFirstValue(JwtRegisteredClaimNames.Sub),
            action = auditAction,
            oldValues = previousValues is null ? null : JsonSerializer.Serialize(previousValues, JsonSerializerOptions.Web),
            newValues = JsonSerializer.Serialize(currentValues, JsonSerializerOptions.Web)
        }, transaction, cancellationToken: cancellationToken));
}
