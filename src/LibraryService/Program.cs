using LibraryService;
using LibraryService.Data;
using LibraryService.Query;
using Microsoft.AspNetCore.OData;
using Microsoft.AspNetCore.OData.Batch;
using Microsoft.AspNetCore.OData.Query.Expressions;
using Microsoft.EntityFrameworkCore;
using LibraryService.Annotations;

// Regenerates db/01-schema.sql from the mapping in LibraryContext and exits. Run it after changing the
// model - the schema is SQL the database applies on its own, but it is not written by hand, because then
// it and the EF mapping would drift apart. No server is contacted; the script is produced from the model.
if (args is ["--emit-schema", var schemaTarget, ..])
{
    await DatabaseInit.EmitSchemaAsync(schemaTarget);
    return;
}

var builder = WebApplication.CreateBuilder(args);

// Where the database is depends on how the service was started, and that is the only difference between
// the two ways to run it:
//
//   - In the published image the entrypoint has already started a Postgres next to the service and put its
//     connection string in the environment. See Dockerfile.
//   - Locally there is nothing to configure and nothing to install: with no connection string the service
//     starts its own Postgres container and waits for it.
//
// Both apply the identical db/*.sql, so both end up at the identical, well-known state.
var connectionString = builder.Configuration.GetConnectionString("Library");
await using var ownedDatabase = string.IsNullOrWhiteSpace(connectionString)
    ? await DatabaseInit.StartOwnPostgresAsync()
    : null;
connectionString ??= ownedDatabase!.GetConnectionString();

// Scoped, which is the point of the exercise: every request gets its own change tracker and its own unit
// of work, and each sub-request of a $batch gets its own too.
builder.Services.AddDbContext<LibraryContext>(options => options.UseNpgsql(connectionString));

// Applies the managed-property annotations the model publishes to every delta-shaped write, so that a
// PATCH carrying a computed or immutable value ignores it rather than storing it - see
// IgnoreManagedPropertiesFilter. Global on purpose: a controller cannot forget it.
builder.Services.AddControllers(mvc => mvc.Filters.Add<IgnoreManagedPropertiesFilter>()).AddOData(options =>
    options
        // The query binders have to go into the *per-route* container: OData resolves its query
        // components from there, not from the application's service provider. Registered globally they are
        // never found, and $search then answers 200 with the unfiltered set.
        .AddRouteComponents(
            "odata/v4/library",
            EdmModelBuilder.Build(),
            services => services
                .AddSingleton<ISearchBinder, MediumSearchBinder>()
                // Replaces the stock filter binder, which compares Edm.Date and Edm.TimeOfDay as
                // arithmetic on their parts rather than as values - see DateComparisonBinder.
                .AddSingleton<IFilterBinder, DateComparisonBinder>()
                .AddSingleton<ODataBatchHandler, DefaultODataBatchHandler>())
        .Select()
        .Filter()
        .OrderBy()
        .Expand()
        .Count()
        .SetMaxTop(1000)
        .SkipToken());

var app = builder.Build();

// No schema creation and no seeding here on purpose: the database arrives populated. Both scripts under
// db/ are applied by Postgres itself before it accepts its first connection, so by the time this process
// can reach the database the well-known state is already there - and the service has no code path that
// could write it a second time, or differently.

// Buffer the body so navigation bindings can be read back after model binding consumed it - see
// NavigationBinding for why they must not go through Delta.
app.Use(async (context, next) =>
{
    context.Request.EnableBuffering();
    await next();
});

// Buffer the *response* as well, so that a query option the database cannot answer fails visibly.
//
// OData serialises straight onto the network while it enumerates the IQueryable. Over the in-memory store
// that enumeration could not fail; over a database it can - EF throws when a $filter has no SQL
// translation - and by then the 200 and the opening bytes of the payload have already gone out. The
// client was left holding a truncated body under a success status, which is the one failure mode a test
// server must not have: "unsupported" became indistinguishable from "no matches".
//
// Writing into memory first keeps the real response untouched until the whole payload exists, so the
// exception can still turn into an honest 500. Nothing here makes an untranslatable option work - that
// stays a genuine limit of OData over an ORM, and FEATURE-COVERAGE.md lists which ones hit it.
app.Use(async (context, next) =>
{
    var responseBody = context.Response.Body;
    using var buffer = new MemoryStream();
    context.Response.Body = buffer;

    try
    {
        await next();
        buffer.Position = 0;
        await buffer.CopyToAsync(responseBody);
    }
    catch (Exception exception)
    {
        // A non-UTC timestamp is the client's doing, so it answers 400 - see UtcOnlyException. Everything
        // else reaching this point is the server's, and stays a 500. EF wraps what a converter throws
        // during SaveChanges, so the search has to go down the chain rather than look at the top only.
        var utcOnly = Unwrap<UtcOnlyException>(exception);

        context.Response.Clear();
        context.Response.StatusCode = utcOnly is null
            ? StatusCodes.Status500InternalServerError
            : StatusCodes.Status400BadRequest;
        context.Response.ContentType = "application/json";

        var reported = utcOnly ?? exception;
        var payload = System.Text.Json.JsonSerializer.Serialize(
            new
            {
                error = new
                {
                    code = reported.GetType().Name,
                    message = reported.Message,
                },
            });

        await responseBody.WriteAsync(System.Text.Encoding.UTF8.GetBytes(payload));
    }
    finally
    {
        context.Response.Body = responseBody;
    }
});

// Accept the query options in the request body: POST <resource>/$query with a text/plain body. The
// middleware rewrites such a request into the equivalent GET before routing, so no controller knows
// about it - which is exactly why it has to sit in front of UseRouting.
app.UseODataQueryRequest();

app.UseODataBatching();
app.UseRouting();
app.MapControllers();

app.Run();

/// <summary>The first <typeparamref name="T" /> in an exception's chain, or null if there is none.</summary>
static T? Unwrap<T>(Exception exception)
    where T : Exception
{
    for (Exception? current = exception; current is not null; current = current.InnerException)
    {
        if (current is T match)
        {
            return match;
        }
    }

    return null;
}

/// <summary>Exposed so integration tests can host the service in-process.</summary>
public partial class Program;
