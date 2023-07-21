using Microsoft.AspNetCore.OData;
using Microsoft.AspNetCore.OData.Query.Expressions;
using Microsoft.OData.Edm;
using Microsoft.OData.ModelBuilder;
using TestServer.Controllers;
using TestServer.Model;

public class Program
{
    public const string OdataRoot = "odata/test-entities";

#nullable disable
    public static WebApplication App;
#nullable enable

    /// <summary>
    /// Asp net applications run as a console app, with a blocking loop
    /// </summary>
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Register components with DependencyInjection
        builder.Services
            .AddScoped<EntityDbContext>()
            .AddCors()
            .AddControllers()
            .AddJsonOptions(o =>
            {
                o.JsonSerializerOptions.PropertyNamingPolicy = null;
                o.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
            })
            .AddOData(opt => opt
                .EnableQueryFeatures()
                .AddRouteComponents(OdataRoot, GetEdmModel(), services =>
                {
                    // add $search functionality on Blog, BlogPost, Comment
                    services.Add(
                        new ServiceDescriptor(typeof(ISearchBinder), _ => new TestServer.Model.BlogSearchBinder(), ServiceLifetime.Singleton));
                }));

        // Build a web server
        App = builder.Build();

        // To add a listener to all requests uncomment the following example
        // var i = 0;
        // App.Use((ctxt, req) =>
        // {
        //     if (ctxt.Request.Method == "GET")
        //         Console.WriteLine($"GET Req: {Interlocked.Increment(ref i)}");

        //     return req(ctxt);
        // });

        // Add components to web server
        App.Use((ctxt, req) => req(ctxt));
        App.UseODataRouteDebug();
        App.UseRouting();
        App.UseCors(builder => builder.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
        App.UseEndpoints(endpoints => endpoints.MapControllers());

        // Add some singleton seed data to in memory database
        SeedData.Seed(App.Services);

        Console.WriteLine();
        Console.WriteLine("### OData test server ###");
        Console.WriteLine("To stop:");
        Console.WriteLine(" * Press (Ctrl+c) to stop -or-");
        Console.WriteLine(" * Send a GET or POST request to localhost:5432/kill");
        Console.WriteLine("Server port defined in launchSettings.json");
        Console.WriteLine();
        Console.WriteLine("Features:");
        Console.WriteLine(" * Auth and security disabled");
        Console.WriteLine(" * CORS disabled");
        Console.WriteLine(" * OData endpoints @ http://localhost:5432/$odata");
        Console.WriteLine(" * $metadata @ http://localhost:5432/odata/test-entities/$metadata");
        Console.WriteLine(" * $search (on Blog, BlogPost, Comment)");
        Console.WriteLine();

        Console.WriteLine("Blog data model:");
        Console.WriteLine(" * AppDetails (UserType*, UserProfileType*)");
        Console.WriteLine(" * UserProfile (User*, AppDetail*)");
        Console.WriteLine(" * UserRole (User*, AppDetail*)");
        Console.WriteLine(" * User (Blog*, Comment*, UserProfile)");
        Console.WriteLine(" * Blog (User, BlogPost*)");
        Console.WriteLine(" * BlogPost (Blog, Comment*)");
        Console.WriteLine(" * Comment (BlogPost, User, CommentTag*) + CommentMood (Comment)");
        Console.WriteLine(" * CommentTag (Comment*)");
        Console.WriteLine();

        Console.WriteLine("Other data model:");
        Console.WriteLine(" * OneOfEverything => an entity with all supported data types on it");
        Console.WriteLine(" * CompositeKeyItem => an entity with a composite key");

        Console.WriteLine("KEY: Entity1 (Entity2, Entity3*) =>");
        Console.WriteLine("    Entity 1 has a Link to 1 Entity2 and many Entity3s");
        Console.WriteLine("KEY: Entity1 + Entity2 =>");
        Console.WriteLine("    Entity2 is not an entity, it is a coplex type");
        Console.WriteLine();

        App.Run();
    }


    /// <summary>
    /// Build the $metadata file for Odata
    /// </summary>
    public static IEdmModel GetEdmModel()
    {
        var builder = new ODataConventionModelBuilder();

        builder.Namespace = "My.Odata.Entities";
        builder.ContainerName = "My/Odata.Container";

        builder.Singleton<AppDetails>("AppDetails");
        builder.Singleton<AppDetailsBase>("AppDetailsBase");
        builder
            .EntityType<AppDetails>()
            .Function("CountUsers")
            .Returns<int>();

        builder.EntitySet<UserProfile>("UserProfiles");
        builder.EntitySet<UserRole>("UserRoles");
        builder.EntitySet<HasId>("HasIds");
        builder.EntitySet<User>("Users");
        builder.EntitySet<Blog>("Blogs");
        builder.EntitySet<BlogPost>("BlogPosts");
        builder.EntitySet<BlogPost>("BlogPosts2");
        builder.EntitySet<Comment>("Comments");
        builder.ComplexType<CommentTag>();
        builder.ComplexType<CommentMood>();

        builder.Function("MyBlogs").ReturnsCollection<Blog>().ReturnNullable = false;

        var myBlogs2 = builder.Function("MyBlogs2");
        myBlogs2.Parameter<int>("take");
        myBlogs2
            .ReturnsCollection<Blog>().ReturnNullable = false;

        builder
            .EntityType<HasId>()
            .Function("JustReturn6")
            .Returns<int>();

        builder
            .EntityType<User>()
            .Function("JustReturn6")
            .Returns<string>();

        var calculator = builder.Function("Calculator");
        calculator.Parameter<int>("lhs");
        calculator.Parameter<int>("rhs");
        calculator.Returns<int>();

        var calculator2 = builder.Function("Calculator2");
        calculator2.CollectionParameter<int>("vals");
        calculator2.Returns<int>();

        var calculator3 = builder.Function("Calculator3");
        calculator3.CollectionParameter<Value<int>>("vals").Nullable = true;
        calculator3.Returns<int>().ReturnNullable = true;

        var calculator4 = builder.Function("Calculator4");
        calculator4.Parameter<Value<int>>("lhs").Nullable = true;
        calculator4.Parameter<Value<int>>("rhs").Nullable = true;
        calculator4.Returns<int>();

        var favouriteBlog = builder
            .EntityType<User>()
            .Function("FavouriteBlog");

        favouriteBlog
            .Returns<Blog>();

        favouriteBlog.IsComposable = true;

        var hasBlog = builder
            .EntityType<User>()
            .Function("HasBlog");

        hasBlog
            .Returns<bool>();

        hasBlog
            .Parameter<Blog>("blog");

        var isType = builder
            .EntityType<User>()
            .Function("IsType");

        isType
            .Returns<bool>();

        isType
            .Parameter<UserType>("userType");

        var isProfileType = builder
            .EntityType<User>()
            .Function("IsProfileType");

        isProfileType
            .Returns<bool>();

        isProfileType
            .Parameter<UserProfileType>("userProfileType");

        var wordCount1 = builder
            .EntityType<Blog>()
            .Function("WordCount");
        wordCount1.Parameter<bool>("filterCommentsOnly");
        wordCount1.Returns<int>();

        var wordCount2 = builder
            .EntityType<Blog>()
            .Function("WordCount");
        wordCount2.Returns<int>();

        var wordCount3 = builder
            .EntityType<Blog>()
            .Function("WordCount");
        wordCount3.Parameter<string>("countThisWord");
        wordCount3.Returns<int>();

        // NOTE: not implemented in controller layer
        var acceptsGuid = builder
            .EntityType<Blog>()
            .Function("AcceptsGuid");
        acceptsGuid.Parameter<Guid>("theGuid");
        acceptsGuid.Returns<Guid>();

        // NOTE: not implemented in controller layer
        var acceptsGuids = builder
            .EntityType<Blog>()
            .Function("AcceptsGuids");
        acceptsGuids.CollectionParameter<Guid>("theGuids");
        acceptsGuids.Returns<Guid>();

        // NOTE: not implemented in controller layer
        var acceptsEntityCollection = builder
            .EntityType<Blog>()
            .Function("IsFromUser");
        acceptsEntityCollection.CollectionParameter<User>("users");
        acceptsEntityCollection.Returns<bool>();

        builder
            .EntityType<Blog>()
            .Collection.Function("Top10BlogsByName")
            .ReturnsCollection<Blog>();

        var commentsByTag = builder
            .EntityType<Comment>()
            .Collection.Function("GetCommentsByTag");

        commentsByTag
            .ReturnsCollection<Comment>();

        commentsByTag.Parameter<CbtInput>("input");

        builder.EntitySet<CompositeKeyItem>("CompositeKeyItems");
        builder.EntitySet<OneOfEverything>("OneOfEverythings");

        return builder.GetEdmModel();
    }
}