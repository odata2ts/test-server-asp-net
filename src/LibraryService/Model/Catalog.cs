using System.ComponentModel.DataAnnotations;
using Microsoft.OData.ModelBuilder;

// The CLR namespace becomes the EDM namespace, which is how the four schemas of the reference model
// (model/library.xml) are reproduced. Do not rename without adjusting the expected $metadata.
namespace Library.Catalog;

/// <summary>Flags enum, reference model <c>Library.Catalog.Amenities</c>.</summary>
/// <remarks>
/// The member <c>Café</c> is spelled with a non-ASCII character on purpose: identifiers in CSDL are
/// unicode, and a server tripping over it is worth knowing about.
/// </remarks>
[Flags]
public enum Amenities
{
    WheelchairAccessible = 1,
    Parking = 2,
    Café = 4,
    KidsArea = 8,
    StudyRoom = 16,
    FullService = 31,
}

/// <summary>Enum with a non-default underlying type, reference model <c>Library.Catalog.AvailabilityStatus</c>.</summary>
public enum AvailabilityStatus : byte
{
    Available = 0,
    OnLoan = 1,
    InRepair = 2,
    Missing = 3,
}

/// <summary>Abstract complex type - a base for <see cref="PostalAddress" />, never instantiated itself.</summary>
public abstract class Address
{
    [MaxLength(120)]
    public string? Street { get; set; }
    [MaxLength(80)]
    public string? City { get; set; }
}

public class PostalAddress : Address
{
    [MaxLength(10)]
    public string? PostalCode { get; set; }
    [MaxLength(60)]
    public string? Country { get; set; }
}

public class ConditionReport
{
    public byte ConditionBefore { get; set; }
    public byte ConditionAfter { get; set; }
    public string? Remark { get; set; }
}

public class MediumStats
{
    public long TotalLoanCount { get; set; }
    public TimeSpan AverageLoanDuration { get; set; }
}

/// <summary>
/// Root of the media hierarchy. Abstract, three levels deep in places
/// (<c>Medium</c> → <c>PrintMedium</c> → <c>Magazine</c> → <c>TradeJournal</c>) and the carrier of the
/// features that are interesting in *combination* with inheritance: streams, open types, containment.
/// </summary>
public abstract class Medium
{
    public Guid Id { get; set; }
    [MaxLength(200)]
    public string Title { get; set; } = "";
    [MaxLength(40)]
    public string? Language { get; set; }
    public DateOnly? PublicationDate { get; set; }
    public ICollection<string> Keywords { get; set; } = [];

    /// <summary>Server-computed, annotated <c>Core.Computed</c> in the reference model.</summary>
    public double? PopularityScore { get; set; }

    public ICollection<Circulation.Copy> Copies { get; set; } = [];
}

/// <summary>Abstract intermediate level; carries the <c>Core.AlternateKeys</c> annotation on <see cref="ISBN" />.</summary>
public abstract class PrintMedium : Medium
{
    /// <summary>Reference model: <c>Library.Catalog.ISBN</c>, a <c>TypeDefinition</c> over <c>Edm.String</c>.</summary>
    [MaxLength(13)]
    public string? ISBN { get; set; }
}

public class Book : PrintMedium
{
    public short PageCount { get; set; }
    public byte AgeRating { get; set; }
    public PublisherRegistry.Publisher? Publisher { get; set; }
}

public class Magazine : PrintMedium
{
    public int IssueNumber { get; set; }
}

public class TradeJournal : Magazine
{
    public string? Field { get; set; }
}

public abstract class AudioMedium : Medium
{
    public TimeSpan? Duration { get; set; }
}

public class Audiobook : AudioMedium
{
    public string? Narrator { get; set; }

    /// <summary><c>Edm.Stream</c> property, next to a contained collection of media entities.</summary>
    public Stream? Sample { get; set; }

    /// <summary>Contained entities (<c>ContainsTarget="true"</c>) - addressable only through the parent.</summary>
    public ICollection<AudiobookChapter> Chapters { get; set; } = [];
}

/// <summary>Media entity (<c>HasStream="true"</c>) that is at the same time a contained entity.</summary>
[MediaType]
public class AudiobookChapter
{
    public int Id { get; set; }
    public string? Title { get; set; }
}

public class DVD : AudioMedium
{
    public byte RegionCode { get; set; }
}

/// <summary>Media entity inside the inheritance hierarchy.</summary>
[MediaType]
public class EBook : Medium
{
    [MaxLength(20)]
    public string? FileFormat { get; set; }
}

/// <summary>Open type - accepts undeclared properties - with an <c>Edm.Untyped</c> property on top.</summary>
public class CollectorsItem : Medium
{
    public object? ExtraData { get; set; }
    public Circulation.Branch? StorageLocation { get; set; }
    public IDictionary<string, object?> DynamicProperties { get; set; } = new Dictionary<string, object?>();
}
