using Library.Catalog;
using Library.Circulation;
using Microsoft.Spatial;

namespace LibraryService.Data;

/// <summary>
/// In-memory store with fixed seed data.
///
/// Deliberately not a database: this service exists to probe which OData features an implementation
/// supports, and a persistence layer would only add its own limitations on top (spatial types, TPH
/// inheritance, contained entities). Everything is exposed as <see cref="IQueryable{T}" />, so the query
/// options are executed by LINQ.
///
/// The keys are fixed so that consumers can assert against them.
/// </summary>
public sealed class LibraryData
{
    public static readonly Guid DerProzessId = new("11111111-1111-1111-1111-111111111111");
    public static readonly Guid AudiobookId = new("22222222-2222-2222-2222-222222222222");
    public static readonly Guid EBookId = new("33333333-3333-3333-3333-333333333333");
    public static readonly Guid MagazineId = new("44444444-4444-4444-4444-444444444444");
    public static readonly Guid TradeJournalId = new("55555555-5555-5555-5555-555555555555");
    public static readonly Guid DvdId = new("66666666-6666-6666-6666-666666666666");
    public static readonly Guid CollectorsItemId = new("77777777-7777-7777-7777-777777777777");
    public static readonly Guid LoanId = new("88888888-8888-8888-8888-888888888888");

    public List<Medium> Media { get; } = [];
    public List<Copy> Copies { get; } = [];
    public List<Member> Members { get; } = [];
    public List<Loan> Loans { get; } = [];
    public List<Reservation> Reservations { get; } = [];
    public List<IdDocument> IdDocuments { get; } = [];
    public List<Branch> Branches { get; } = [];
    public List<Bookmobile> Bookmobiles { get; } = [];
    public List<PublisherRegistry.Publisher> Publishers { get; } = [];
    public List<PublisherRegistry.Branch> PublisherBranches { get; } = [];

    public Branch MainBranch => Branches[0];

    public LibraryData() => Seed();

    private void Seed()
    {
        var suhrkamp = new PublisherRegistry.Publisher
        {
            Id = 1,
            Name = "Suhrkamp",
            Country = "DE",
            Founded = new DateOnly(1950, 7, 1),
        };
        var penguin = new PublisherRegistry.Publisher
        {
            Id = 2,
            Name = "Penguin",
            Country = "GB",
            Founded = new DateOnly(1935, 7, 30),
        };
        Publishers.AddRange([suhrkamp, penguin]);

        PublisherBranches.AddRange(
            [
                new PublisherRegistry.Branch { Id = 1, City = "Berlin", Country = "DE" },
                new PublisherRegistry.Branch { Id = 2, City = "London", Country = "GB" },
            ]);

        var central = new Branch
        {
            Id = 1,
            Name = "Central Library",
            Address = new PostalAddress
            {
                Street = "Hauptstraße 1",
                City = "Berlin",
                PostalCode = "10115",
                Country = "DE",
            },
            Location = GeographyPoint.Create(52.5200, 13.4050),
            LowestFloor = -2,
            OpensAt = new TimeOnly(9, 0),
            ClosesAt = new TimeOnly(20, 0),
            Amenities = Library.Catalog.Amenities.WheelchairAccessible | Library.Catalog.Amenities.Café,
            Population = 3_600_000,
        };
        var suburban = new Branch
        {
            Id = 2,
            Name = "Suburban Branch",
            Location = GeographyPoint.Create(52.4800, 13.3200),
            LowestFloor = 0,
            OpensAt = new TimeOnly(10, 0),
            ClosesAt = new TimeOnly(18, 0),
            Amenities = Library.Catalog.Amenities.Parking | Library.Catalog.Amenities.KidsArea,
            Population = 120_000,
        };
        Branches.AddRange([central, suburban]);

        Bookmobiles.Add(
            new Bookmobile
            {
                Id = 1,
                LicensePlate = "B-LIB-1",
                CurrentPosition = GeographyPoint.Create(52.5100, 13.3900),
            });

        var derProzess = new Book
        {
            Id = DerProzessId,
            Title = "Der Prozess",
            Language = "de",
            PublicationDate = new DateOnly(1925, 4, 26),
            Keywords = ["Roman", "Klassiker", "Fragment"],
            PopularityScore = 9.1,
            ISBN = "9783518188002",
            PageCount = 320,
            AgeRating = 16,
            Publisher = suhrkamp,
        };
        var audiobook = new Audiobook
        {
            Id = AudiobookId,
            Title = "Die Verwandlung (Hörbuch)",
            Language = "de",
            PublicationDate = new DateOnly(2015, 3, 1),
            Keywords = ["Hörbuch", "Klassiker"],
            PopularityScore = 7.4,
            Duration = TimeSpan.FromMinutes(112),
            Narrator = "Anna Beispiel",
            Chapters =
            [
                new AudiobookChapter { Id = 1, Title = "Erwachen" },
                new AudiobookChapter { Id = 2, Title = "Der Apfel" },
            ],
        };
        var ebook = new EBook
        {
            Id = EBookId,
            Title = "Digitale Aufklärung",
            Language = "de",
            PublicationDate = new DateOnly(2021, 9, 15),
            Keywords = ["Sachbuch"],
            PopularityScore = 5.2,
            FileFormat = "EPUB",
        };
        var magazine = new Magazine
        {
            Id = MagazineId,
            Title = "Stadtmagazin",
            Language = "de",
            PublicationDate = new DateOnly(2026, 1, 1),
            PopularityScore = 3.0,
            ISBN = "9770000000001",
            IssueNumber = 142,
        };
        var journal = new TradeJournal
        {
            Id = TradeJournalId,
            Title = "Journal of Library Science",
            Language = "en",
            PublicationDate = new DateOnly(2025, 11, 1),
            PopularityScore = 4.5,
            IssueNumber = 12,
            Field = "Information Science",
        };
        var dvd = new DVD
        {
            Id = DvdId,
            Title = "Metropolis",
            Language = "de",
            PublicationDate = new DateOnly(1927, 1, 10),
            PopularityScore = 8.0,
            Duration = TimeSpan.FromMinutes(153),
            RegionCode = 2,
        };
        var collectors = new CollectorsItem
        {
            Id = CollectorsItemId,
            Title = "Erstausgabe 1899",
            Language = "de",
            PopularityScore = 9.9,
            ExtraData = "provenance unknown",
            StorageLocation = central,
            DynamicProperties = { ["Appraisal"] = 12500, ["Insured"] = true },
        };
        Media.AddRange([derProzess, audiobook, ebook, magazine, journal, dvd, collectors]);
        suhrkamp.Books.Add(derProzess);

        var copy1 = new Copy
        {
            MediumId = DerProzessId,
            InventoryNumber = 1,
            Condition = 2,
            IsLoanable = true,
            Status = AvailabilityStatus.OnLoan,
            AcquisitionDate = new DateOnly(2019, 5, 2),
            WeightKg = 0.42f,
            Location_ = "A-12",
            Medium = derProzess,
            Location = central,
        };
        var copy2 = new Copy
        {
            MediumId = DerProzessId,
            InventoryNumber = 2,
            Condition = 1,
            IsLoanable = true,
            Status = AvailabilityStatus.Available,
            AcquisitionDate = new DateOnly(2022, 8, 17),
            WeightKg = 0.41f,
            Location_ = "A-13",
            Medium = derProzess,
            Location = central,
        };
        var copy3 = new Copy
        {
            MediumId = DvdId,
            InventoryNumber = 1,
            Condition = 3,
            IsLoanable = false,
            Status = AvailabilityStatus.InRepair,
            AcquisitionDate = new DateOnly(2018, 2, 1),
            WeightKg = 0.09f,
            Location_ = "C-01",
            Medium = dvd,
            Location = suburban,
        };
        Copies.AddRange([copy1, copy2, copy3]);
        derProzess.Copies.Add(copy1);
        derProzess.Copies.Add(copy2);
        dvd.Copies.Add(copy3);

        var alice = new Member
        {
            Id = 1,
            Name = "Alice Muster",
            DateOfBirth = new DateOnly(1988, 4, 12),
            Address = new PostalAddress
            {
                Street = "Lindenweg 4",
                City = "Berlin",
                PostalCode = "10115",
                Country = "DE",
            },
            PreviousAddresses = [new PostalAddress { Street = "Alte Gasse 9", City = "Potsdam", PostalCode = "14467", Country = "DE" }],
            ActiveSince = new DateTimeOffset(2015, 1, 5, 9, 0, 0, TimeSpan.Zero),
            Balance = 12.50m,
        };
        var bob = new Member
        {
            Id = 2,
            Name = "Bob Beispiel",
            DateOfBirth = new DateOnly(1975, 11, 30),
            ActiveSince = new DateTimeOffset(2020, 6, 20, 14, 30, 0, TimeSpan.Zero),
            Balance = 0m,
        };
        Members.AddRange([alice, bob]);

        var loan = new Loan
        {
            Id = LoanId,
            LoanedAt = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero),
            DueDate = new DateOnly(2026, 7, 1),
            LateFee = 2.50m,
            Member = alice,
            Copy = copy1,
        };
        Loans.Add(loan);
        alice.Loans.Add(loan);

        var reservation = new Reservation
        {
            Id = new Guid("99999999-9999-9999-9999-999999999999"),
            ReservedAt = new DateTimeOffset(2026, 7, 20, 8, 15, 0, TimeSpan.Zero),
        };
        Reservations.Add(reservation);
        alice.Reservations.Add(reservation);

        var idDocument = new IdDocument
        {
            Id = new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Scan = [0x01, 0x02, 0x03, 0x04],
            UploadedAt = new DateTimeOffset(2015, 1, 5, 9, 5, 0, TimeSpan.Zero),
        };
        IdDocuments.Add(idDocument);
        alice.IdDocument = idDocument;
    }
}
