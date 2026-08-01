using System.ComponentModel.DataAnnotations;

namespace PublisherRegistry;

public class Publisher
{
    public int Id { get; set; }
    [MaxLength(100)]
    public string Name { get; set; } = "";
    [MaxLength(60)]
    public string? Country { get; set; }
    public DateOnly? Founded { get; set; }
    public ICollection<Library.Catalog.Book> Books { get; set; } = [];
}

/// <summary>
/// Deliberately the same type name as <c>Library.Circulation.Branch</c>, in a different namespace:
/// a probe for servers and clients that key types by their short name.
/// </summary>
public class Branch
{
    public int Id { get; set; }
    [MaxLength(80)]
    public string? City { get; set; }
    [MaxLength(60)]
    public string? Country { get; set; }
}
