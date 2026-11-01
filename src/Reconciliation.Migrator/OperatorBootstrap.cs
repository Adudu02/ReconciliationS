using System.Net.Mail;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace Reconciliation.Migrator;

public static class OperatorBootstrap
{
    public static async Task SeedAsync(string connectionString, string operatorEmail, string operatorPassword, CancellationToken cancellationToken = default)
    {
        if (!MailAddress.TryCreate(operatorEmail, out var parsedEmail) || parsedEmail.Address != operatorEmail || operatorPassword.Length < 12)
            throw new ArgumentException("Operator requires a valid email and password of at least 12 characters.");
        var operatorPasswordHash = new PasswordHasher<string>().HashPassword(operatorEmail, operatorPassword);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        // Never promote an existing non-operator or reset an existing account's password.
        await using var insertOperatorCommand = new NpgsqlCommand("""
            INSERT INTO app_user(email, password_hash, is_operator) VALUES (@email, @hash, true)
            ON CONFLICT (lower(email)) DO NOTHING
            """, connection);
        insertOperatorCommand.Parameters.AddWithValue("email", operatorEmail);
        insertOperatorCommand.Parameters.AddWithValue("hash", operatorPasswordHash);
        if (await insertOperatorCommand.ExecuteNonQueryAsync(cancellationToken) == 0)
            Console.WriteLine("operator already exists, not modified");
    }
}
