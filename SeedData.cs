using Microsoft.OData.Edm;
using TestServer.Model;

public static class SeedData
{
    /// <summary>
    /// Add some basic seed data.
    /// Mostly tests will create their own data, this is for singletons and ref data
    /// <summary>
    public static void Seed(IServiceProvider diProvider)
    {
        // "using var" will ensure that ctxt.Dispose() is called at
        // the end of this method, even if an exception happens
        using var ctxt = diProvider.CreateScope();

        // asp net dbContext gives access to a database
        // this database is configured to be in memory
        var dbContext = ctxt.ServiceProvider.GetRequiredService<EntityDbContext>();

        dbContext.AppDetails.AddRange(new[]
        {
            new AppDetails
            {
                AppName = "Blog app"
            }
        });

        dbContext.UserRoles.AddRange(new[]
        {
            new UserRole
            {
                Key = UserType.Admin,
                Description = "Admin"
            },
            new UserRole
            {
                Key = UserType.User,
                Description = "User"
            }
        });

        dbContext.UserProfiles.AddRange(new[]
        {
            new UserProfile
            {
                Key = UserProfileType.Advanced,
                Description = "Advanced"
            },
            new UserProfile
            {
                Key = UserProfileType.Standard,
                Description = "Standard"
            }
        });

        dbContext.OneOfEverythings.Add(new OneOfEverything
        {
            String = "Str",
            Guid = Guid.Parse("486fd5a2-4326-45c0-9a3f-ddc88dcb36d2"),
            Boolean = true,
            Date = new DateTime(1999, 2, 1),
            DateTimeOffset = new DateTimeOffset(
                new DateTime(1999, 1, 1, 11, 30, 30, 123),
                TimeSpan.FromMinutes(90)),
            TimeOfDay = new TimeOfDay(12, 1, 1, 1),
            Int16 = 1,
            Int32 = 2,
            Int64 = 3,
            Decimal = 3.3M,
            Double = 2.2,
            Single = 1.1F,
            Byte = 0x11,
            Binary = new byte[] { 0x12 },
            Duration = TimeSpan.FromDays(2)
                + TimeSpan.FromHours(3)
                + TimeSpan.FromMinutes(4)
                + TimeSpan.FromSeconds(5)
                + TimeSpan.FromMilliseconds(6),
            SByte = 0x10
        });

        dbContext.Users.Add(new User
        {
            Id = "Me",
            Name = "Me",
            UserType = UserType.Admin,
            Score = 0,
            UserProfileType = UserProfileType.Advanced,
            Blogs = new List<Blog>
            {
                new Blog
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = "Owners Blog"
                }
            }
        });

        dbContext.SaveChanges();
    }
}