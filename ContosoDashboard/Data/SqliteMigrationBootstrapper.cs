using System.Data.Common;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ContosoDashboard.Data;

public sealed class SqliteMigrationBootstrapper
{
    private const string HistoryTableName = "__EFMigrationsHistory";
    private const string BaselineMigrationName = "_InitialBaseline";
    private readonly ApplicationDbContext context;

    public SqliteMigrationBootstrapper(ApplicationDbContext context)
    {
        this.context = context;
    }

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        if (!context.Database.IsSqlite())
        {
            throw new InvalidOperationException("The SQLite migration bootstrapper requires the SQLite provider.");
        }

        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = context.Database.GetDbConnection();
            var schema = await ReadSchemaAsync(connection, cancellationToken);
            var hasHistoryTable = schema.Any(item => item.Type == "table" && item.Name == HistoryTableName);
            var hasApplicationSchema = schema.Any(item => !IsEfInfrastructureObject(item.Name));

            if (hasHistoryTable)
            {
                var appliedMigrations = await context.Database.GetAppliedMigrationsAsync(cancellationToken);
                if (appliedMigrations.Any())
                {
                    await context.Database.MigrateAsync(cancellationToken);
                    return;
                }

                if (hasApplicationSchema)
                {
                    throw new InvalidOperationException(
                        "The SQLite database contains an empty EF migration history and existing application tables. " +
                        "No changes were made; back up the database and investigate its migration history before retrying.");
                }
            }

            if (!hasApplicationSchema)
            {
                await context.Database.MigrateAsync(cancellationToken);
                return;
            }

            var baselineMigrationId = GetBaselineMigrationId();
            var expectedSchema = await ReadExpectedBaselineSchemaAsync(baselineMigrationId, cancellationToken);
            if (!SchemaMatches(schema, expectedSchema))
            {
                throw new InvalidOperationException(
                    "The existing SQLite database schema does not match the expected InitialBaseline schema. " +
                    "No changes were made; back up the database and resolve the schema mismatch before starting the application. " +
                    DescribeSchemaMismatch(schema, expectedSchema));
            }

            await RecordBaselineAsync(connection, baselineMigrationId, cancellationToken);
            await context.Database.MigrateAsync(cancellationToken);
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    private string GetBaselineMigrationId()
    {
        var baselineMigrationId = context.Database.GetMigrations()
            .FirstOrDefault(migrationId => migrationId.EndsWith(BaselineMigrationName, StringComparison.Ordinal));

        return baselineMigrationId ?? throw new InvalidOperationException(
            "The InitialBaseline EF migration was not found. Generate the baseline migration before starting the application.");
    }

    private static async Task<IReadOnlyList<SchemaObject>> ReadExpectedBaselineSchemaAsync(
        string baselineMigrationId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var expectedContext = new ApplicationDbContext(options);
        var migrator = expectedContext.GetService<IMigrator>();
        await migrator.MigrateAsync(baselineMigrationId, cancellationToken);

        return await ReadSchemaAsync(connection, cancellationToken);
    }

    private static async Task<IReadOnlyList<SchemaObject>> ReadSchemaAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT type, name, tbl_name, sql
            FROM sqlite_master
            WHERE type IN ('table', 'index', 'view', 'trigger')
              AND substr(name, 1, 7) <> 'sqlite_'
            ORDER BY type, name
            """;

        var schema = new List<SchemaObject>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            schema.Add(new SchemaObject(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : NormalizeSql(reader.GetString(3))));
        }

        return schema;
    }

    private static bool SchemaMatches(
        IReadOnlyList<SchemaObject> actualSchema,
        IReadOnlyList<SchemaObject> expectedSchema)
    {
        var actual = actualSchema
            .Where(item => !IsEfInfrastructureObject(item.Name))
            .OrderBy(item => item.Type, StringComparer.Ordinal)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ToArray();
        var expected = expectedSchema
            .Where(item => !IsEfInfrastructureObject(item.Name))
            .OrderBy(item => item.Type, StringComparer.Ordinal)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ToArray();

        return actual.SequenceEqual(expected);
    }

    private static string DescribeSchemaMismatch(
        IReadOnlyList<SchemaObject> actualSchema,
        IReadOnlyList<SchemaObject> expectedSchema)
    {
        var actual = actualSchema.Where(item => !IsEfInfrastructureObject(item.Name)).ToDictionary(item => item.Name);
        var expected = expectedSchema.Where(item => !IsEfInfrastructureObject(item.Name)).ToDictionary(item => item.Name);
        var names = actual.Keys.Union(expected.Keys, StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal);
        return string.Join(Environment.NewLine, names
            .Where(name => !actual.TryGetValue(name, out var actualObject)
                || !expected.TryGetValue(name, out var expectedObject)
                || actualObject != expectedObject)
            .Select(name => !actual.ContainsKey(name)
                ? $"Missing expected schema object: {name}."
                : !expected.ContainsKey(name)
                    ? $"Unexpected schema object: {name}."
                    : $"Schema definition differs for object: {name}."));
    }

    private static bool IsEfInfrastructureObject(string name) =>
        name is HistoryTableName or "__EFMigrationsLock";

    private async Task RecordBaselineAsync(
        DbConnection connection,
        string baselineMigrationId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var createHistory = connection.CreateCommand())
        {
            createHistory.Transaction = transaction;
            createHistory.CommandText = """
                CREATE TABLE "__EFMigrationsHistory" (
                    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                    "ProductVersion" TEXT NOT NULL
                )
                """;
            await createHistory.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insertBaseline = connection.CreateCommand())
        {
            insertBaseline.Transaction = transaction;
            insertBaseline.CommandText = """
                INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                VALUES ($migrationId, $productVersion)
                """;
            insertBaseline.Parameters.Add(new SqliteParameter("$migrationId", baselineMigrationId));
            insertBaseline.Parameters.Add(new SqliteParameter("$productVersion", GetEfProductVersion()));
            await insertBaseline.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static string GetEfProductVersion()
    {
        var version = typeof(DbContext).Assembly.GetName().Version;
        return version is null
            ? throw new InvalidOperationException("Could not determine the EF Core product version for migration history.")
            : version.ToString(3);
    }

    private static string NormalizeSql(string sql) => Regex.Replace(sql, @"\s+", " ").Trim();

    private sealed record SchemaObject(string Type, string Name, string TableName, string? Sql);
}