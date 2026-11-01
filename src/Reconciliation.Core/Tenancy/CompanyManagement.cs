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
        routes.MapGet("/companies/{id:guid}/settings", (Guid id, TenantContext tenant, CancellationToken ct) =>
            tenant.ExecuteAsync<IResult>(id, async (connection, transaction, token) =>
            {
                var settings = await ReadSettingsAsync(connection, transaction, token);
                return settings is null ? Results.NotFound() : Results.Ok(settings);
            }, ct)).RequireAuthorization("company-member");
        routes.MapPatch("/companies/{id:guid}/settings", ChangeSettingsAsync).RequireAuthorization("administrator");
        routes.MapPost("/companies/{id:guid}/archive", (Guid id, HttpContext http, TenantContext tenant, CancellationToken ct) =>
            tenant.ExecuteAsync<IResult>(id, async (connection, transaction, token) =>
            {
                var changed = await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE company SET archived_at = now() WHERE archived_at IS NULL", transaction: transaction, cancellationToken: token));
                if (changed > 0) await AuditAsync(connection, transaction, id, http, "company.archived", null, new { archived = true }, token);
                return Results.NoContent();
            }, ct)).RequireAuthorization("administrator");
        return routes;
    }

    private static Task<IResult> CreateAsync(CreateCompanyRequest request, HttpContext http, TenantContext tenant,
        IPasswordHasher<string> hasher, CancellationToken ct)
    {
        if (request.Rfc is null || !RfcFormat().IsMatch(request.Rfc))
            return Task.FromResult<IResult>(Results.BadRequest(new { error = "RFC has an invalid format." }));
        if (string.IsNullOrWhiteSpace(request.Name) || !MailAddress.TryCreate(request.AdminEmail, out var address) || address.Address != request.AdminEmail)
            return Task.FromResult<IResult>(Results.BadRequest(new { error = "Name and valid administrator email are required." }));
        var id = Guid.NewGuid();
        return tenant.ExecuteAsync<IResult>(id, async (connection, transaction, token) =>
        {
            var admin = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
                "SELECT id FROM app_user WHERE lower(email) = lower(@AdminEmail)", request, transaction, cancellationToken: token));
            if (admin is null)
            {
                if (request.AdminPassword is null || request.AdminPassword.Length < 12)
                    return Results.BadRequest(new { error = "Administrator password requires at least 12 characters." });
                var hash = hasher.HashPassword(request.AdminEmail, request.AdminPassword);
                // Concurrent creation may have inserted the email; attach without changing that password.
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO app_user(email, password_hash) VALUES (@AdminEmail, @hash)
                    ON CONFLICT (lower(email)) DO NOTHING
                    """, new { request.AdminEmail, hash }, transaction, cancellationToken: token));
                admin = await connection.QuerySingleAsync<Guid>(new CommandDefinition(
                    "SELECT id FROM app_user WHERE lower(email) = lower(@AdminEmail)", request, transaction, cancellationToken: token));
            }
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO company(id, rfc, name) VALUES (@id, @Rfc, @Name);
                INSERT INTO membership(company_id, user_id, role) VALUES (@id, @admin, 'administrator');
                """, new { id, request.Rfc, request.Name, admin }, transaction, cancellationToken: token));
            var company = new CompanyResponse(id, request.Rfc, request.Name, null);
            await AuditAsync(connection, transaction, id, http, "company.created", null, company, token);
            return Results.Created($"/companies/{id}", company);
        }, ct);
    }

    private static Task<IResult> ChangeSettingsAsync(Guid id, SettingsChange request, HttpContext http, TenantContext tenant, CancellationToken ct) =>
        tenant.ExecuteAsync<IResult>(id, async (connection, transaction, token) =>
        {
            if (request.DateToleranceDays < 0 || request.AmountTolerance < 0 || request.AmountTolerance > 9999999999999999.99m ||
                (request.AmountTolerance is decimal amount && decimal.Round(amount, 2) != amount))
                return Results.BadRequest(new { error = "Tolerances must be nonnegative; amount must fit numeric(18,2)." });
            var old = await ReadSettingsAsync(connection, transaction, token, forUpdate: true);
            if (old is null) return Results.NotFound();
            var updated = new CompanySettings(request.DateToleranceDays ?? old.DateToleranceDays,
                request.AmountTolerance ?? old.AmountTolerance, request.ComplementNextBizDay ?? old.ComplementNextBizDay);
            if (updated != old)
            {
                await connection.ExecuteAsync(new CommandDefinition("""
                    UPDATE company SET date_tolerance_days = @DateToleranceDays,
                      amount_tolerance = @AmountTolerance, complement_next_biz_day = @ComplementNextBizDay
                    """, updated, transaction, cancellationToken: token));
                await AuditAsync(connection, transaction, id, http, "company.settings_changed", old, updated, token);
            }
            return Results.Ok(updated);
        }, ct);

    private static Task<CompanySettings?> ReadSettingsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct, bool forUpdate = false) =>
        connection.QuerySingleOrDefaultAsync<CompanySettings>(new CommandDefinition("""
            SELECT date_tolerance_days AS DateToleranceDays, amount_tolerance AS AmountTolerance,
              complement_next_biz_day AS ComplementNextBizDay FROM company
            """ + (forUpdate ? " FOR UPDATE" : ""), transaction: transaction, cancellationToken: ct));

    // shortcut: minimal transactional tenancy audit; group 5 adds hash chaining and rejection triggers.
    private static Task<int> AuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid companyId, HttpContext http,
        string action, object? previous, object current, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO audit_entry(company_id, actor_id, action, old_values, new_values)
            VALUES (@companyId, @actor::uuid, @action, @oldValues::jsonb, @newValues::jsonb)
            """, new
        {
            companyId,
            actor = http.User.FindFirstValue(JwtRegisteredClaimNames.Sub),
            action,
            oldValues = previous is null ? null : JsonSerializer.Serialize(previous, JsonSerializerOptions.Web),
            newValues = JsonSerializer.Serialize(current, JsonSerializerOptions.Web)
        }, transaction, cancellationToken: ct));
}
