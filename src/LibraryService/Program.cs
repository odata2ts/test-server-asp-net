using LibraryService;
using LibraryService.Data;
using Microsoft.AspNetCore.OData;

var builder = WebApplication.CreateBuilder(args);

// One store for the whole process: the seed data is the contract consumers assert against.
builder.Services.AddSingleton<LibraryData>();

builder.Services.AddControllers().AddOData(options =>
    options
        .AddRouteComponents("odata/v4/library", EdmModelBuilder.Build())
        .Select()
        .Filter()
        .OrderBy()
        .Expand()
        .Count()
        .SetMaxTop(1000)
        .SkipToken());

var app = builder.Build();

app.UseRouting();
app.MapControllers();

app.Run();

/// <summary>Exposed so integration tests can host the service in-process.</summary>
public partial class Program;
