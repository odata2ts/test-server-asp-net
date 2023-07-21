using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace TestServer.Model;

# nullable disable

public interface IHasMutableId
{
    string Id { get; set; }
}

public class HasId : IHasMutableId
{
    [Key]
    public string Id { get; set; }
}

/// <summary>
/// User role ref. Has a string based enum as a key
/// </summary>
public class UserRole
{
    [Key]
    public UserType Key { get; set; }

    [Required]
    public string Description { get; set; }
}

public class AppDetailsBase
{
    [Key]
    public int Id { get; set; }
}

/// <summary>
/// Singleton describing the blog app
/// </summary>
public class AppDetails : AppDetailsBase
{
    [Required]
    public string AppName { get; set; }

    public IEnumerable<string> AppNameWords => AppName.Split(" ", StringSplitOptions.RemoveEmptyEntries);

    public IEnumerable<UserType> UserTypes => new[] { UserType.Admin, UserType.User };

    public IList<UserProfileType> UserProfileTypes => new[] { UserProfileType.Advanced, UserProfileType.Standard };
}

/// <summary>
/// User profile ref. Has an int based enum as a key
/// </summary>
public class UserProfile
{
    [Key]
    public UserProfileType Key { get; set; }

    [Required]
    public string Description { get; set; }
}

public enum UserType
{
    User,
    Admin
}

public enum UserProfileType
{
    Standard = 10,
    Advanced = 11
}

/// <summary>
/// A user who can create blogs + comment
/// </summary>
public class User : HasId
{
    [Required]
    public string Name { get; set; }

    [Required]
    public UserType UserType { get; set; }

    [Required]
    public double Score { get; set; }

    [Required]
    public UserProfileType UserProfileType { get; set; }

    public IList<Blog> Blogs { get; set; }
    public IList<Comment> BlogPostComments { get; set; }
}

/// <summary>
/// A blog that can contain many blog posts
/// </summary>
public class Blog : HasId
{
    [Required]
    public string Name { get; set; }

    [Required]
    public string UserId { get; set; }
    public User User { get; set; }
    public IList<BlogPost> Posts { get; set; }
    public IQueryable<string> BlogPostNames => GetBlogPostNames().AsQueryable();
    public IEnumerable<string> GetBlogPostNames() => Posts.OrEmpty().Select(x => x.Name);
}

/// <summary>
/// A blog post which can have many comments
/// </summary>
public class BlogPost : HasId
{
    [Required]
    public string Name { get; set; }

    [Required]
    public string Content { get; set; }

    [Required]
    public long Likes { get; set; }

    public long? AgeRestriction { get; set; }

    [Required]
    public DateTimeOffset Date { get; set; }

    [Required]
    public string BlogId { get; set; }
    public Blog Blog { get; set; }
    public IList<Comment> Comments { get; set; }
    public IQueryable<string> Words => Regex.Split(Content, @"\s").Where(x => !string.IsNullOrWhiteSpace(x)).AsQueryable();
}

/// <summary>
/// A blog post comment
/// </summary>
public class Comment : HasId
{
    [Required]
    public string Title { get; set; }

    [Required]
    public string Text { get; set; }

    [Required]
    public string BlogPostId { get; set; }
    public BlogPost BlogPost { get; set; }

#nullable enable
    public string? UserId { get; set; }
    public User? User { get; set; }
    public IQueryable<string> Words => Regex.Split(Text, @"\s").Where(x => !string.IsNullOrWhiteSpace(x)).AsQueryable();
    public IList<CommentTag>? Tags { get; set; }
    public CommentMood? Mood { get; set; }
#nullable disable
}

/// <summary>
/// A tag on a blog post comment
/// registered as odata complex type (not entity)
/// </summary>
public class CommentTag : IHasMutableId
{
    [Key]
    public string Tag { get; set; }
    public IList<Comment> Comments { get; set; }
    string IHasMutableId.Id { get => Tag; set => Tag = value; }
}

public enum Mood
{
    Happy = 1,
    Sad
}

/// <summary>
/// The mood a user was in when commenting
/// registered as odata complex type (not entity)
/// </summary>
public class CommentMood
{
    [Key]
    public string Key { get; set; }

    [Required]
    public Mood Mood { get; set; }

    public string CommentId { get; set; }
    public Comment Comment { get; set; }
}

public static class Utils
{
    public static IEnumerable<T> OrEmpty<T>(this IEnumerable<T> x) => x ?? Enumerable.Empty<T>();
}