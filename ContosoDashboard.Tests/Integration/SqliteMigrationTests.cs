using ContosoDashboard.Data;
using ContosoDashboard.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ContosoDashboard.Tests.Integration;

public sealed class SqliteMigrationTests : IClassFixture<ContosoWebApplicationFactory>
{
    private readonly ContosoWebApplicationFactory factory;

    public SqliteMigrationTests(ContosoWebApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task ExistingEnsureCreatedDatabaseRetainsSeedDataAndIsAdoptedByMigrations()
    {
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        Assert.Equal(4, await context.Users.CountAsync());
        Assert.Equal(1, await context.Projects.CountAsync());
        Assert.Equal(3, await context.Tasks.CountAsync());

        var connection = (SqliteConnection)context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '__EFMigrationsHistory'";
        var historyTableExists = Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;

        Assert.True(historyTableExists, "The populated legacy schema must be adopted into EF migration history.");
    }

    [Fact]
    public async Task EmptyDatabaseAppliesBaselineMigration()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var bootstrapper = new SqliteMigrationBootstrapper(context);

        await bootstrapper.MigrateAsync();

        var appliedMigrations = await context.Database.GetAppliedMigrationsAsync();
        Assert.Contains(appliedMigrations, migration => migration.EndsWith("_InitialBaseline", StringComparison.Ordinal));
        Assert.Contains(appliedMigrations, migration => migration.EndsWith("_AddDocuments", StringComparison.Ordinal));
        Assert.Contains(appliedMigrations, migration => migration.EndsWith("_AddDocumentShares", StringComparison.Ordinal));
        Assert.Contains(appliedMigrations, migration => migration.EndsWith("_AddDocumentTasks", StringComparison.Ordinal));
        Assert.Equal(4, await context.Users.CountAsync());
        Assert.Equal(1, await context.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = 'DocumentTasks'").SingleAsync());
    }

    [Fact]
    public async Task PopulatedEnsureCreatedDatabaseIsAdoptedWithoutReplayingBaseline()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var baselineMigrationId = context.Database.GetMigrations()
            .Single(migrationId => migrationId.EndsWith("_InitialBaseline", StringComparison.Ordinal));
        await context.GetService<IMigrator>().MigrateAsync(baselineMigrationId);
        await context.Database.ExecuteSqlRawAsync("DROP TABLE \"__EFMigrationsHistory\"");
        var usersBeforeAdoption = await context.Users.CountAsync();
        var bootstrapper = new SqliteMigrationBootstrapper(context);

        await bootstrapper.MigrateAsync();

        Assert.Equal(usersBeforeAdoption, await context.Users.CountAsync());
        var appliedMigrations = await context.Database.GetAppliedMigrationsAsync();
        Assert.Contains(appliedMigrations, migrationId => migrationId.EndsWith("_InitialBaseline", StringComparison.Ordinal));
        Assert.Contains(appliedMigrations, migrationId => migrationId.EndsWith("_AddDocuments", StringComparison.Ordinal));
        Assert.Contains(appliedMigrations, migrationId => migrationId.EndsWith("_AddDocumentShares", StringComparison.Ordinal));
        Assert.Contains(appliedMigrations, migrationId => migrationId.EndsWith("_AddDocumentTasks", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnknownExistingSchemaIsRejectedWithoutChangingIt()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.Database.ExecuteSqlRawAsync("CREATE TABLE Unexpected (Id INTEGER NOT NULL)");
        var bootstrapper = new SqliteMigrationBootstrapper(context);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => bootstrapper.MigrateAsync());

        Assert.Contains("schema does not match", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await context.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = 'Unexpected'").SingleAsync());
        Assert.Equal(0, await context.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = '__EFMigrationsHistory'").SingleAsync());
    }
}
