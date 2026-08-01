using LibraryService;
using Microsoft.AspNetCore.OData;

var builder = WebApplication.CreateBuilder(args);

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
