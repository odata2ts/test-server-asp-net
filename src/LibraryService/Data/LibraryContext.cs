using Library.Catalog;
using Library.Circulation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Spatial;

namespace LibraryService.Data;

/// <summary>
/// The store, backed by SQLite held in memory through EF Core.
///
/// The point of a real persistence layer here is that the query options stop being a LINQ-to-Objects
/// exercise: <c>[EnableQuery]</c> now hands <c>$filter</c>, <c>$orderby</c>, <c>$expand</c> and friends to
/// a provider that has to translate them to SQL, and whatever it cannot translate is a finding about the
/// combination of OData and an ORM rather than something this service papered over. Change tracking,
/// cascading delete and the concurrency token on <see cref="Copy.Condition" /> become real for the same
/// reason.
///
/// It is still a *test* server: the database lives in memory, is created and seeded at startup, and every
/// process therefore starts from the identical, well-known state. The keys in <see cref="LibrarySeed" />
/// are fixed so that consumers can assert against them.
///
/// Three parts of the reference model have no faithful relational form - spatial values, the open type's
/// dynamic properties and the untyped property. Each goes through a converter in
/// <see cref="ValueConversions" />, and each converter's cost is recorded there and in FEATURE-COVERAGE.md.
/// </summary>
public sealed class LibraryContext(DbContextOptions<LibraryContext> options) : DbContext(options)
{
    public DbSet<Medium> Media => Set<Medium>();
    public DbSet<Copy> Copies => Set<Copy>();
    public DbSet<Member> Members => Set<Member>();
    public DbSet<Loan> Loans => Set<Loan>();
    public DbSet<Reservation> Reservations => Set<Reservation>();
    public DbSet<IdDocument> IdDocuments => Set<IdDocument>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<Bookmobile> Bookmobiles => Set<Bookmobile>();
    public DbSet<PublisherRegistry.Publisher> Publishers => Set<PublisherRegistry.Publisher>();
    public DbSet<PublisherRegistry.Branch> PublisherBranches => Set<PublisherRegistry.Branch>();

    /// <summary>
    /// The bytes behind <c>Edm.Stream</c>, in their own table rather than on the entities.
    ///
    /// A stream is a link in the payload and never an inline value, so the bytes must not travel with the
    /// entity - the same reason they sat beside it in the in-memory store. It is deliberately absent from
    /// the EDM: <see cref="EdmModelBuilder" /> builds the model from the reference model's types, so a
    /// table that exists only for persistence cannot leak into <c>$metadata</c>.
    /// </summary>
    public DbSet<StoredContent> Contents => Set<StoredContent>();

    /// <summary>The <c>MainBranch</c> singleton. Ordered rather than "the first row": SQLite makes no promise.</summary>
    public Branch MainBranch => Branches.OrderBy(b => b.Id).First();

    protected override void OnModelCreating(ModelBuilder model)
    {
        ConfigureCatalog(model);
        ConfigureCirculation(model);
        ConfigurePublisherRegistry(model);
        ConfigureContent(model);

        // Every key of the reference model is assigned by the seed or by a controller, never by the
        // database. A test server whose member ids depend on insert order is one consumers cannot assert
        // against, so the four integer keys that EF would otherwise turn into identity columns are pinned
        // here. The Guid keys are caller-assigned by convention already.
        model.Entity<Member>().Property(m => m.Id).ValueGeneratedNever();
        model.Entity<Branch>().Property(b => b.Id).ValueGeneratedNever();
        model.Entity<Bookmobile>().Property(b => b.Id).ValueGeneratedNever();
        model.Entity<PublisherRegistry.Publisher>().Property(p => p.Id).ValueGeneratedNever();
        model.Entity<PublisherRegistry.Branch>().Property(b => b.Id).ValueGeneratedNever();
        model.Entity<AudiobookChapter>().Property(c => c.Id).ValueGeneratedNever();
        model.Entity<Copy>().Property(c => c.InventoryNumber).ValueGeneratedNever();
    }

    private static void ConfigureCatalog(ModelBuilder model)
    {
        // Table-per-hierarchy across all three levels: Medium -> PrintMedium -> Magazine -> TradeJournal
        // as well as Medium -> AudioMedium -> Audiobook. One table, one discriminator, which is what makes
        // the `/Media/Library.Catalog.Book` type-cast segments translate to a plain WHERE.
        var medium = model.Entity<Medium>();
        medium.ToTable("Media");
        medium.HasKey(m => m.Id);

        // The two abstract intermediate levels have to be registered even though no row is ever one of
        // them. EF would otherwise hang Book and Magazine straight off Medium, and `OfType<PrintMedium>()`
        // - which is what the alternate-key route and the `/Media/Library.Catalog.PrintMedium` type cast
        // compile to - would name a type the model does not know and fail to translate.
        model.Entity<PrintMedium>();
        model.Entity<AudioMedium>().Property(a => a.Duration).HasConversion(ValueConversions.DurationTicks());

        medium.HasDiscriminator<string>("MediumKind")
            .HasValue<Book>(nameof(Book))
            .HasValue<Magazine>(nameof(Magazine))
            .HasValue<TradeJournal>(nameof(TradeJournal))
            .HasValue<Audiobook>(nameof(Audiobook))
            .HasValue<DVD>(nameof(DVD))
            .HasValue<EBook>(nameof(EBook))
            .HasValue<CollectorsItem>(nameof(CollectorsItem));

        // Primitive collection: EF stores it as a JSON array in one column, and `Keywords/any(...)` still
        // translates - SQLite reaches into it with json_each.
        medium.PrimitiveCollection(m => m.Keywords);

        // The `Sample` stream property is a link in the payload, never a value. Its bytes live in
        // StoredContent; the CLR property is only there so the EDM can declare Edm.Stream.
        model.Entity<Audiobook>().Ignore(a => a.Sample);

        // Contained entities. Addressable only through their audiobook, so the parent's key is half of
        // theirs - `Id` is unique per audiobook, not globally.
        model.Entity<AudiobookChapter>(chapter =>
        {
            chapter.ToTable("AudiobookChapters");
            chapter.Property<Guid>("AudiobookId");
            chapter.HasKey("AudiobookId", nameof(AudiobookChapter.Id));
        });
        model.Entity<Audiobook>()
            .HasMany(a => a.Chapters)
            .WithOne()
            .HasForeignKey("AudiobookId")
            .OnDelete(DeleteBehavior.Cascade);

        // Open type and Edm.Untyped: JSON, because a relational column has no other shape to offer. Both
        // are consequently invisible to $filter and $orderby - see ValueConversions.
        var collectorsItem = model.Entity<CollectorsItem>();
        collectorsItem.Property(c => c.DynamicProperties)
            .HasColumnName("DynamicProperties")
            .HasConversion(ValueConversions.DynamicProperties(), ValueConversions.DynamicPropertiesComparer());
        collectorsItem.Property(c => c.ExtraData)
            .HasColumnName("ExtraData")
            .HasConversion(ValueConversions.Untyped(), ValueConversions.UntypedComparer());
        collectorsItem.HasOne(c => c.StorageLocation).WithMany().OnDelete(DeleteBehavior.SetNull);

        model.Entity<Book>().HasOne(b => b.Publisher).WithMany(p => p.Books).OnDelete(DeleteBehavior.SetNull);
    }

    private static void ConfigureCirculation(ModelBuilder model)
    {
        model.Entity<Member>(member =>
        {
            member.HasKey(m => m.Id);

            // Complex type as an owned reference: its properties become columns on Members. EF has no
            // notion of an abstract complex base, but the CLR property is typed as the derived
            // PostalAddress, so nothing is lost - Address exists only in the EDM.
            member.OwnsOne(m => m.Address);

            // Collection of complex type as a JSON column. A side table would need a synthetic key the
            // reference model does not have.
            member.OwnsMany(m => m.PreviousAddresses).ToJson();

            member.Property(m => m.Balance).HasConversion(ValueConversions.ScaledDecimal(2));
            member.Property(m => m.ActiveSince).HasConversion(ValueConversions.UtcTicks());

            member.HasOne(m => m.IdDocument).WithOne().HasForeignKey<Member>("IdDocumentId")
                .OnDelete(DeleteBehavior.SetNull);

            // Cascading delete, as the reference model declares it. The foreign key stays optional even so:
            // `DELETE /Members(1)/Loans/$ref?$id=...` unlinks a loan without deleting it, and a required
            // key would turn that into an error.
            member.HasMany(m => m.Loans).WithOne(l => l.Member!).HasForeignKey("MemberId")
                .IsRequired(false)
                .OnDelete(DeleteBehavior.Cascade);

            member.HasMany(m => m.Reservations).WithOne().HasForeignKey("MemberId")
                .IsRequired(false)
                .OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Copy>(copy =>
        {
            copy.HasKey(c => new { c.MediumId, c.InventoryNumber });

            // [ConcurrencyCheck] on Condition is picked up by convention and stops being decoration here:
            // EF puts the original value into the WHERE clause of every UPDATE.
            copy.HasOne(c => c.Medium!).WithMany(m => m.Copies)
                .HasForeignKey(c => c.MediumId)
                .OnDelete(DeleteBehavior.Cascade);

            copy.HasOne(c => c.Location).WithMany().OnDelete(DeleteBehavior.SetNull);
        });

        model.Entity<Loan>(loan =>
        {
            loan.HasKey(l => l.Id);
            loan.Property(l => l.LateFee).HasConversion(ValueConversions.ScaledDecimal(2));
            loan.Property(l => l.LoanedAt).HasConversion(ValueConversions.UtcTicks());
            loan.Property(l => l.ReturnedAt).HasConversion(ValueConversions.UtcTicks());
            loan.HasOne(l => l.Copy).WithMany().OnDelete(DeleteBehavior.SetNull);
        });

        model.Entity<Reservation>().HasKey(r => r.Id);
        model.Entity<Reservation>().Property(r => r.ReservedAt).HasConversion(ValueConversions.UtcTicks());
        model.Entity<IdDocument>().HasKey(d => d.Id);
        model.Entity<IdDocument>().Property(d => d.UploadedAt).HasConversion(ValueConversions.UtcTicks());

        model.Entity<Branch>(branch =>
        {
            branch.HasKey(b => b.Id);
            branch.OwnsOne(b => b.Address);
            Spatial(branch.Property(b => b.Location));
            Spatial(branch.Property(b => b.CatchmentArea));
            Spatial(branch.Property(b => b.FloorPlanOrigin));
            Spatial(branch.Property(b => b.FloorPlanShapes));
        });

        model.Entity<Bookmobile>(bookmobile =>
        {
            bookmobile.HasKey(b => b.Id);
            Spatial(bookmobile.Property(b => b.Route));
            Spatial(bookmobile.Property(b => b.CurrentPosition));
        });
    }

    /// <summary>
    /// Stores one spatial property as WKT text. The spatial type is inferred from the property, so a new
    /// one needs nothing but this call.
    ///
    /// Handed to the non-generic <c>HasConversion</c>: the generic overload wants its converter typed
    /// <c>ValueConverter&lt;T?, string&gt;</c> to match the nullable property, while a converter that
    /// declared a nullable source would have to handle a null EF never passes it. Widening here keeps the
    /// converter honest about what it actually converts.
    /// </summary>
    private static void Spatial<T>(PropertyBuilder<T?> property)
        where T : class, ISpatial =>
        property.HasConversion(
            (Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter)ValueConversions.Spatial<T>(),
            (Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer)ValueConversions.SpatialComparer<T>());

    private static void ConfigurePublisherRegistry(ModelBuilder model)
    {
        model.Entity<PublisherRegistry.Publisher>().HasKey(p => p.Id);

        // Same short name as Library.Circulation.Branch, different namespace - and in a relational store
        // also a second table that must not collide with the first.
        model.Entity<PublisherRegistry.Branch>(branch =>
        {
            branch.ToTable("PublisherBranches");
            branch.HasKey(b => b.Id);
        });
    }

    private static void ConfigureContent(ModelBuilder model) =>
        model.Entity<StoredContent>().HasKey(c => new { c.OwnerId, c.Slot, c.Part });

    // --- stream content ----------------------------------------------------------------------------

    public MediaContent? FindContent(ContentSlot slot, Guid ownerId, int part = 0) =>
        Contents.Find(ownerId, slot, part) is { } stored ? new MediaContent(stored.ContentType, stored.Bytes) : null;

    public void SetContent(ContentSlot slot, Guid ownerId, MediaContent content, int part = 0)
    {
        if (Contents.Find(ownerId, slot, part) is { } existing)
        {
            existing.ContentType = content.ContentType;
            existing.Bytes = content.Bytes;
        }
        else
        {
            Contents.Add(
                new StoredContent
                {
                    OwnerId = ownerId,
                    Slot = slot,
                    Part = part,
                    ContentType = content.ContentType,
                    Bytes = content.Bytes,
                });
        }

        SaveChanges();
    }

    public void RemoveContent(ContentSlot slot, Guid ownerId, int part = 0)
    {
        if (Contents.Find(ownerId, slot, part) is { } existing)
        {
            Contents.Remove(existing);
            SaveChanges();
        }
    }
}

/// <summary>Which of the three <c>Edm.Stream</c> positions a row of <see cref="StoredContent" /> belongs to.</summary>
public enum ContentSlot
{
    /// <summary>The content of a media entity, addressed as <c>Media({id})/$value</c>.</summary>
    Entity = 0,

    /// <summary>The <c>Audiobook.Sample</c> stream property.</summary>
    Sample = 1,

    /// <summary>The content of a contained <c>AudiobookChapter</c>.</summary>
    Chapter = 2,
}

/// <summary>A stream's bytes, keyed by the entity it belongs to. Persistence only - not part of the EDM.</summary>
public sealed class StoredContent
{
    public Guid OwnerId { get; set; }
    public ContentSlot Slot { get; set; }

    /// <summary>The chapter id for <see cref="ContentSlot.Chapter" />, otherwise 0.</summary>
    public int Part { get; set; }

    public string ContentType { get; set; } = "";
    public byte[] Bytes { get; set; } = [];
}

/// <summary>A stream's bytes together with the media type they were stored with.</summary>
public sealed record MediaContent(string ContentType, byte[] Bytes);
