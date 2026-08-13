using LibraryService;
using LibraryService.Data;
using LibraryService.Query;
using Microsoft.AspNetCore.OData;
using Microsoft.AspNetCore.OData.Batch;
using Microsoft.AspNetCore.OData.Query.Expressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// SQLite in memory, shared by the whole process. The connection is opened here and never closed on
// purpose: an in-memory database lives exactly as long as a connection to it is open, so letting EF open
// and close one per request would throw the schema away between requests. One store for the whole
// process, as before - the seed data is the contract consumers assert against.
var connection = new SqliteConnection("Data Source=library;Mode=Memory;Cache=Shared");
connection.Open();
builder.Services.AddSingleton(connection);

// Scoped, which is the point of the exercise: every request gets its own change tracker and its own unit
// of work, and each sub-request of a $batch gets its own too.
builder.Services.AddDbContext<LibraryContext>(options => options.UseSqlite(connection));

builder.Services.AddControllers().AddOData(options =>
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

// Create the schema and fill it before the first request. Nothing is generated at runtime, so this is
// the whole of the server's state - every process starts from the identical, well-known data.
using (var scope = app.Services.CreateScope())
{
    LibrarySeed.Apply(scope.ServiceProvider.GetRequiredService<LibraryContext>());
}

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
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";

        var payload = System.Text.Json.JsonSerializer.Serialize(
            new
            {
                error = new
                {
                    code = exception.GetType().Name,
                    message = exception.Message,
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

/// <summary>Exposed so integration tests can host the service in-process.</summary>
public partial class Program;
