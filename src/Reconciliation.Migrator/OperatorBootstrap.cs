using System.Net.Mail;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace Reconciliation.Migrator;

public static class OperatorBootstrap
{
    public static async Task SeedAsync(string connectionString, string email, string password, CancellationToken cancellationToken = default)
    {
        if (!MailAddress.TryCreate(email, out var address) || address.Address != email || password.Length < 12)
            throw new ArgumentException("Operator requires a valid email and password of at least 12 characters.");
        var hash = new PasswordHasher<string>().HashPassword(email, password);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        // Never promote an existing non-operator or reset an existing account's password.
        await using var command = new NpgsqlCommand("""
            INSERT INTO app_user(email, password_hash, is_operator) VALUES (@email, @hash, true)
            ON CONFLICT (lower(email)) DO NOTHING
            """, connection);
        command.Parameters.AddWithValue("email", email);
        command.Parameters.AddWithValue("hash", hash);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            Console.WriteLine("operator already exists, not modified");
    }
}
