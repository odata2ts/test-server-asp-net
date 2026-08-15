using System.ComponentModel.DataAnnotations;
using Library.Catalog;
using LibraryService.Annotations;
using Microsoft.Spatial;

namespace Library.Circulation;

public class OverdueNotice
{
    public string? Reason { get; set; }

    [Measures.ISOCurrency("EUR")]
    public decimal Amount { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public class LoanStats
{
    public long TotalLoans { get; set; }
    public TimeSpan AverageLoanDuration { get; set; }
}

public class BranchStats
{
    public int BranchId { get; set; }
    public long LoanCount { get; set; }
}

public class AnnualReport
{
    public int Year { get; set; }
    public long TotalLoans { get; set; }

    [Measures.ISOCurrency("EUR")]
    public decimal TotalLateFees { get; set; }
}

/// <summary>Complex type used as a *parameter* of an unbound function.</summary>
public class DateRange
{
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
}

[Core.Description("A person registered with the library, with their loans and reservations.")]
public class Member
{
    public int Id { get; set; }
    [MaxLength(100)]
    public string Name { get; set; } = "";
    public DateOnly? DateOfBirth { get; set; }

    /// <summary>Typed as the *derived* complex type, whose base is abstract.</summary>
    public Catalog.PostalAddress? Address { get; set; }

    /// <summary>Collection of complex type.</summary>
    public ICollection<Catalog.PostalAddress> PreviousAddresses { get; set; } = [];

    [Core.ComputedDefaultValue]
    public DateTimeOffset? ActiveSince { get; set; }

    /// <summary>
    /// Read alone: readable, never writable. What distinguishes it from <c>Core.Computed</c> is what it
    /// says about reading, which is nothing in the computed case.
    /// </summary>
    [Core.Permissions(Core.Permission.Read)]
    [Measures.ISOCurrency("EUR")]
    [Core.Description("Outstanding late fees; settled at the desk, never through this service.")]
    public decimal Balance { get; set; }

    /// <summary>Cascading delete in the reference model.</summary>
    public ICollection<Loan> Loans { get; set; } = [];

    public ICollection<Reservation> Reservations { get; set; } = [];
    public IdDocument? IdDocument { get; set; }
}

/// <summary>Composite key plus a referential constraint towards <see cref="Medium" />.</summary>
public class Copy
{
    public Guid MediumId { get; set; }
    public int InventoryNumber { get; set; }

    /// <summary>
    /// The concurrency token. Declared as one in <c>LibraryContext</c> with EF's own
    /// <c>IsConcurrencyToken()</c> rather than with <c>[ConcurrencyCheck]</c>, so that the fact reaches
    /// <c>$metadata</c> only through <c>EfCoreTranslation</c> - which is the point of it.
    /// </summary>
    [Validation.Minimum(0)]
    [Validation.Maximum(5)]
    [Core.Description("Physical condition on a 0-5 scale, 5 being as new. Doubles as the ETag.")]
    public byte Condition { get; set; }

    public bool IsLoanable { get; set; } = true;
    public Catalog.AvailabilityStatus? Status { get; set; }
    public DateOnly? AcquisitionDate { get; set; }

    [Measures.Unit("kg")]
    [Measures.Scale(2)]
    public float WeightKg { get; set; }

    /// <summary>
    /// Named <c>Location_</c> in the reference model so that it collides with the navigation property
    /// <c>Location</c> only after trailing-underscore trimming - a name-clash probe.
    /// </summary>
    [MaxLength(10)]
    public string? Location_ { get; set; }

    public Catalog.Medium? Medium { get; set; }
    public Branch? Location { get; set; }
}

public class Loan
{
    public Guid Id { get; set; }

    [Core.Immutable]
    public DateTimeOffset LoanedAt { get; set; }

    public DateOnly DueDate { get; set; }
    public DateTimeOffset? ReturnedAt { get; set; }

    [Measures.ISOCurrency("EUR")]
    public decimal? LateFee { get; set; }
    public Member? Member { get; set; }
    public Copy? Copy { get; set; }
}

public class Reservation
{
    public Guid Id { get; set; }
    public DateTimeOffset ReservedAt { get; set; }
}

public class IdDocument
{
    public Guid Id { get; set; }
    public byte[]? Scan { get; set; }
    public DateTimeOffset UploadedAt { get; set; }
}

/// <summary>Carrier of the spatial types and of the flags enum.</summary>
public class Branch
{
    public int Id { get; set; }
    [MaxLength(100)]
    public string Name { get; set; } = "";
    public Catalog.PostalAddress? Address { get; set; }
    public GeographyPoint? Location { get; set; }
    public GeographyPolygon? CatchmentArea { get; set; }
    public sbyte LowestFloor { get; set; }
    public GeometryPoint? FloorPlanOrigin { get; set; }
    public GeometryCollection? FloorPlanShapes { get; set; }
    public TimeOnly? OpensAt { get; set; }
    public TimeOnly? ClosesAt { get; set; }
    public Catalog.Amenities? Amenities { get; set; }

    [Core.Description("Inhabitants of the area the branch serves.")]
    public long Population { get; set; }
}

public class Bookmobile
{
    public int Id { get; set; }
    [MaxLength(12)]
    public string? LicensePlate { get; set; }
    public GeographyLineString? Route { get; set; }
    public GeographyPoint? CurrentPosition { get; set; }
}
