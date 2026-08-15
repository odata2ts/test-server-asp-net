using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Testcontainers.PostgreSql;

namespace LibraryService.Data;

/// <summary>
/// Getting a populated database in front of the service, and keeping the SQL that fills it in step with
/// the model.
///
/// The schema and the seed are SQL under <c>db/</c>, and Postgres applies them itself - the official image
/// runs everything in <c>/docker-entrypoint-initdb.d/</c> once, before it accepts the first connection.
/// That is the whole reason the service has no seeding code left: the database is already right when the
/// service reaches it, so there is no startup order to get wrong, no "has it been seeded yet" check, and no
/// second way for the data to come into being.
/// </summary>
public static class DatabaseInit
{
    /// <summary>The Postgres the service is developed and shipped against. Kept in step with the Dockerfile.</summary>
    public const string PostgresImage = "postgres:18-alpine";

    /// <summary>
    /// Where the SQL lives at runtime. Copied next to the assembly by the build - see LibraryService.csproj -
    /// so that the published image and a local <c>dotnet run</c> find it at the same path.
    /// </summary>
    private static string ScriptDirectory => Path.Combine(AppContext.BaseDirectory, "db");

    /// <summary>The init scripts in the order Postgres will run them: 01-schema.sql, then 02-seed.sql.</summary>
    private static IEnumerable<string> Scripts =>
        Directory.EnumerateFiles(ScriptDirectory, "*.sql").OrderBy(path => path, StringComparer.Ordinal);

    /// <summary>
    /// Starts a Postgres container owned by this process and waits until it has run the init scripts.
    ///
    /// This is the local path only - <c>dotnet run</c> with nothing installed and nothing configured. The
    /// published image does not come here; it is handed a connection string by its entrypoint.
    ///
    /// Testcontainers' own reaper removes the container when the process ends, including when it is killed,
    /// so a crashed debug run does not leave one behind.
    /// </summary>
    public static async Task<PostgreSqlContainer> StartOwnPostgresAsync()
    {
        var builder = new PostgreSqlBuilder(PostgresImage)
            .WithDatabase("library")
            .WithUsername("library")
            .WithPassword("library");

        // The same mechanism the published image uses, so both go through the identical SQL in the
        // identical order. Postgres only runs these on an empty data directory - which is every start here,
        // since the container is thrown away with the process.
        builder = Scripts.Aggregate(
            builder,
            (current, script) => current.WithResourceMapping(
                new FileInfo(script),
                "/docker-entrypoint-initdb.d/"));

        var container = builder.Build();
        await container.StartAsync();
        return container;
    }

    /// <summary>
    /// The EF model, without a database behind it - the mapping alone.
    ///
    /// The *design-time* model, not <c>DbContext.Model</c>: comments, precision and the rest of the
    /// relational configuration are stripped from the read-optimized runtime model, which answers
    /// "the requested configuration is not stored in the read-optimized model" if asked for them.
    ///
    /// Used by <see cref="Annotations.EfCoreTranslation" /> while the EDM is built, which is before any
    /// <c>DbContext</c> exists and before a connection string is known. No connection is opened, exactly
    /// as in <see cref="EmitSchemaAsync" />; the provider only has to be Npgsql.
    /// </summary>
    public static IModel MappingModel()
    {
        var options = new DbContextOptionsBuilder<LibraryContext>().UseNpgsql("Host=model-only").Options;
        using var context = new LibraryContext(options);

        return context.GetService<IDesignTimeModel>().Model;
    }

    /// <summary>
    /// Writes the <c>CREATE TABLE</c> script for the current model to <paramref name="target" />.
    ///
    /// The schema is applied as SQL but not authored as SQL: EF owns the mapping - discriminators, the
    /// shadow foreign keys, the column names owned types get - so hand-writing the DDL would mean keeping
    /// two descriptions of one thing in agreement by hand. Generating it makes the mapping in
    /// <see cref="LibraryContext" /> the single source and the script a build product that happens to be
    /// committed, so that the database can apply it without the service being involved.
    ///
    /// No connection is opened. The provider only has to be Npgsql for the SQL to come out in its dialect.
    /// </summary>
    public static async Task EmitSchemaAsync(string target)
    {
        var options = new DbContextOptionsBuilder<LibraryContext>()
            .UseNpgsql("Host=schema-generation-only")
            .Options;

        await using var context = new LibraryContext(options);

        await File.WriteAllTextAsync(
            target,
            $"""
             -- Generated from the EF Core model - do not edit.
             --
             -- Regenerate after changing LibraryContext or the model classes:
             --     dotnet run --project src/LibraryService -- --emit-schema ../../db/01-schema.sql
             --
             -- (the path is relative to the project directory, which is where `dotnet run` puts you)
             --
             -- Applied by Postgres before the service starts; the data that goes in is 02-seed.sql.

             {context.Database.GenerateCreateScript()}
             """);
    }
}
