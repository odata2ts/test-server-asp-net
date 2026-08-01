using LibraryService;
using LibraryService.Data;
using LibraryService.Query;
using Microsoft.AspNetCore.OData;
using Microsoft.AspNetCore.OData.Batch;
using Microsoft.AspNetCore.OData.Query.Expressions;

var builder = WebApplication.CreateBuilder(args);

// One store for the whole process: the seed data is the contract consumers assert against.
builder.Services.AddSingleton<LibraryData>();

builder.Services.AddControllers().AddOData(options =>
    options
        // The search binder has to go into the *per-route* container: OData resolves its query
        // components from there, not from the application's service provider. Registered globally it is
        // never found, and $search then answers 200 with the unfiltered set.
        .AddRouteComponents(
            "odata/v4/library",
            EdmModelBuilder.Build(),
            services => services
                .AddSingleton<ISearchBinder, MediumSearchBinder>()
                .AddSingleton<ODataBatchHandler, DefaultODataBatchHandler>())
        .Select()
        .Filter()
        .OrderBy()
        .Expand()
        .Count()
        .SetMaxTop(1000)
        .SkipToken());

var app = builder.Build();

// Buffer the body so navigation bindings can be read back after model binding consumed it - see
// NavigationBinding for why they must not go through Delta.
app.Use(async (context, next) =>
{
    context.Request.EnableBuffering();
    await next();
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
