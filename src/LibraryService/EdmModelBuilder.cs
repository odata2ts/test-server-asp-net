using Library.Catalog;
using Library.Circulation;
using LibraryService.Annotations;
using LibraryService.Data;
using Microsoft.OData.Edm;
using Microsoft.OData.Edm.Vocabularies;
using Microsoft.OData.ModelBuilder;

namespace LibraryService;

/// <summary>
/// Builds the EDM model for the "Library" reference model (model/library.xml in
/// odata2ts/test-reference-model).
///
/// The model is built explicitly rather than left to conventions wherever the reference model demands
/// something a convention would not produce - namespaces, the container name, the namespace of every
/// operation, composite keys, containment. Whatever cannot be expressed at all is recorded in
/// FEATURE-COVERAGE.md, never quietly dropped.
///
/// Vocabulary annotations are *not* configured here where they belong to a type or a property: those are
/// declared as attributes on the model classes and emitted by <see cref="AnnotationEmitter" />. What stays
/// here is what has no CLR declaration - entity sets, singletons, operations, parameters, the container -
/// written with <see cref="AnnotationExtensions.Annotate{T}" />, plus the two terms the builder still
/// produces itself. See IMPLEMENTATION.md.
/// </summary>
public static class EdmModelBuilder
{
    /// <summary>Namespace of all operations in the reference model.</summary>
    private const string OperationNamespace = "Library.Circulation";

    public static IEdmModel Build()
    {
        // `Namespace` names the entity container. It also becomes the namespace of every type, which is
        // not what the reference model wants - the types live in three other schemas - so the type
        // namespaces are put back afterwards, see AlignNamespacesWithClrTypes.
        var builder = new ODataConventionModelBuilder { Namespace = "Library.Service", ContainerName = "LibraryService" };

        // What EF Core and OData both have a word for is carried over from the persistence model rather
        // than repeated here. It has to run from this hook and not inline below: the convention builder
        // discovers the properties and navigations while building, so before that there is nothing to
        // configure - which is exactly the trap, because configuring nothing throws nothing.
        builder.OnModelCreating = configured => EfCoreTranslation.Apply(configured, DatabaseInit.MappingModel());

        ConfigureTypes(builder);
        ConfigureEntitySets(builder);
        ConfigureUnboundOperations(builder);
        ConfigureBoundOperations(builder);
        AlignNamespacesWithClrTypes(builder);

        // Not GetEdmModel(): that returns the model without any of the declared vocabulary annotations.
        return builder.GetAnnotatedEdmModel();
    }

    /// <summary>
    /// Puts every type into the schema its CLR namespace names, undoing the builder's habit of dropping
    /// all types into the container's namespace. Done centrally instead of per type so that a newly added
    /// type cannot be forgotten.
    /// </summary>
    private static void AlignNamespacesWithClrTypes(ODataConventionModelBuilder builder)
    {
        foreach (var type in builder.StructuralTypes)
        {
            type.Namespace = type.ClrType.Namespace!;
        }

        foreach (var type in builder.EnumTypes)
        {
            type.Namespace = type.ClrType.Namespace!;
        }
    }

    private static void ConfigureTypes(ODataConventionModelBuilder builder)
    {
        // Registered explicitly: enums discovered only through a property are added so late that the
        // namespace alignment below would miss them, leaving them in the container's namespace.
        builder.EnumType<Amenities>();
        builder.EnumType<AvailabilityStatus>();

        // --- complex types -------------------------------------------------------------------------
        builder.ComplexType<Address>().Abstract();
        builder.ComplexType<PostalAddress>().DerivesFrom<Address>();

        // --- media hierarchy -----------------------------------------------------------------------
        var medium = builder.EntityType<Medium>();
        medium.Abstract();
        medium.HasKey(m => m.Id);

        // Core.AlternateKeys goes on the entity *type*, not on the entity set: that is where the
        // reference model puts it, and where routing looks it up (GetDeclaredAlternateKeysForType).
        // The reference model's PropertyRef carries a Name and no Alias, so none is set here.
        builder.EntityType<PrintMedium>().Abstract().DerivesFrom<Medium>()
            .HasAlternateKeys(k => k.HasKey(p => p
                .HasName(new EdmPropertyPathExpression(nameof(PrintMedium.ISBN)))
                .HasAlias(nameof(PrintMedium.ISBN))));
        builder.EntityType<Book>().DerivesFrom<PrintMedium>();
        builder.EntityType<Magazine>().DerivesFrom<PrintMedium>();
        builder.EntityType<TradeJournal>().DerivesFrom<Magazine>();
        builder.EntityType<AudioMedium>().Abstract().DerivesFrom<Medium>();
        builder.EntityType<DVD>().DerivesFrom<AudioMedium>();

        var audiobook = builder.EntityType<Audiobook>().DerivesFrom<AudioMedium>();
        // ContainsTarget="true": chapters are addressable only through their audiobook.
        audiobook.ContainsMany(a => a.Chapters);

        builder.EntityType<AudiobookChapter>().HasKey(c => c.Id).MediaType();
        builder.EntityType<EBook>().DerivesFrom<Medium>().MediaType();

        // Open type: the convention builder picks the IDictionary<string, object?> property up by
        // itself - registering it a second time is rejected as "more than one dynamic property container".
        builder.EntityType<CollectorsItem>().DerivesFrom<Medium>();

        // --- circulation ---------------------------------------------------------------------------
        builder.EntityType<Member>().HasKey(m => m.Id);
        builder.EntityType<Loan>().HasKey(l => l.Id);
        builder.EntityType<Reservation>().HasKey(r => r.Id);
        builder.EntityType<IdDocument>().HasKey(d => d.Id);
        builder.EntityType<Branch>().HasKey(b => b.Id);
        builder.EntityType<Bookmobile>().HasKey(b => b.Id);

        var copy = builder.EntityType<Copy>();
        copy.HasKey(c => new { c.MediumId, c.InventoryNumber });
        // Third argument is the partner: one call sets Partner on both sides of the association.
        copy.HasRequired(c => c.Medium!, (c, m) => c.MediumId == m.Id, m => m.Copies);

        // Facets. Precision/Scale are settable as properties; SRID and Unicode have no equivalent at all
        // in the builder - see FEATURE-COVERAGE.md.
        builder.ComplexType<OverdueNotice>().Property(o => o.Amount).Precision = 5;
        builder.ComplexType<OverdueNotice>().Property(o => o.Amount).Scale = 2;
        builder.ComplexType<OverdueNotice>().Property(o => o.CreatedAt).Precision = 7;
        builder.ComplexType<AnnualReport>().Property(a => a.TotalLateFees).Precision = 12;
        builder.ComplexType<AnnualReport>().Property(a => a.TotalLateFees).Scale = 2;

        // Member/Balance and Loan/LateFee are deliberately absent: EF declares their precision with
        // HasPrecision, and EfCoreTranslation carries it over. Stating it twice is how the column and the
        // facet drift apart. The timestamps below have no EF counterpart - Postgres keeps microseconds
        // whatever the model says - so they stay here.
        var member = builder.EntityType<Member>();
        member.Property(m => m.ActiveSince).Precision = 7;

        var loan = builder.EntityType<Loan>();
        loan.Property(l => l.LoanedAt).Precision = 7;
        loan.Property(l => l.ReturnedAt).Precision = 7;

        builder.EntityType<Reservation>().Property(r => r.ReservedAt).Precision = 7;
        builder.EntityType<IdDocument>().Property(d => d.UploadedAt).Precision = 7;
        copy.Property(c => c.IsLoanable).DefaultValueString = "true";

        // Required navigation. `NavigationPropertyConfiguration.Partner` is read-only, and neither
        // `HasMany` nor the convention builder relates the two sides - but the three-argument overloads of
        // HasRequired/HasOptional do. The referential constraint in the middle may be null, which is what
        // makes them usable here: unlike Copy/Medium, these associations have no foreign key property in
        // the reference model.
        //
        // Delete behaviour is *not* declared here: EF already states it, and EfCoreTranslation turns every
        // DeleteBehavior.Cascade into the OnDelete the CSDL wants.
        member.HasMany(m => m.Loans);
        loan.HasRequired(l => l.Member!, null, m => m.Loans);
        loan.HasRequired(l => l.Copy!);

        builder.EntityType<Book>().HasOptional(b => b.Publisher!, null, p => p.Books);

        // --- publisher registry --------------------------------------------------------------------
        builder.EntityType<PublisherRegistry.Publisher>().HasKey(p => p.Id);
        builder.EntityType<PublisherRegistry.Branch>().HasKey(b => b.Id);
    }

    private static void ConfigureEntitySets(ODataConventionModelBuilder builder)
    {
        builder.EntitySet<Medium>("Media").HasSearchRestrictions().IsSearchable(true);
        builder.EntitySet<Copy>("Copies");
        builder.EntitySet<Member>("Members");
        builder.EntitySet<Loan>("Loans");
        builder.EntitySet<Reservation>("Reservations");
        builder.EntitySet<IdDocument>("IdDocuments");
        builder.EntitySet<Branch>("Branches");
        builder.EntitySet<Bookmobile>("Bookmobiles");
        builder.EntitySet<PublisherRegistry.Publisher>("Publishers");
        builder.EntitySet<PublisherRegistry.Branch>("PublisherBranches");

        builder.Singleton<Branch>("MainBranch");

        AnnotateContainerElements(builder);
    }

    /// <summary>
    /// Descriptions and capabilities for the container and its elements. Separate from the declarations
    /// above because the generic <c>EntitySet&lt;T&gt;()</c> returns a wrapper that cannot carry an
    /// annotation - see IMPLEMENTATION.md; <c>AnnotatableEntitySet</c> finds the set just declared.
    ///
    /// Every capability stated here was checked with a request, and the requests are in
    /// test/annotations.http. A capability term that is not verified does not belong in the metadata.
    /// </summary>
    private static void AnnotateContainerElements(ODataModelBuilder builder)
    {
        builder.AnnotateContainer(
            new Core.Description("The odata2ts \"Library\" reference model, served by ASP.NET Core OData."),
            new Capabilities.SupportedFormats("application/json"),
            new Capabilities.BatchSupported(),
            new Capabilities.KeyAsSegmentSupported(),
            new Capabilities.QuerySegmentSupported(),
            // Not supported, and stating so is the point: the preference is ignored rather than honoured,
            // and $crossjoin answers 404.
            new Capabilities.AsynchronousRequestsSupported(false),
            new Capabilities.CrossJoinSupported(false));

        builder.AnnotatableEntitySet<Medium>("Media")
            .Annotate(new Core.Description("Everything the library holds, across all media types."));
        builder.AnnotatableEntitySet<Copy>("Copies")
            .Annotate(new Core.Description("The physical or licensed copies of a medium; what is borrowed."));
        builder.AnnotatableEntitySet<Member>("Members")
            .Annotate(new Core.Description("Registered members."));
        builder.AnnotatableEntitySet<Loan>("Loans")
            .Annotate(new Core.Description("Loans, open and returned alike."));
        builder.AnnotatableEntitySet<Bookmobile>("Bookmobiles")
            .Annotate(new Core.Description("Mobile branches, with their route and current position."));

        builder.AnnotatableSingleton<Branch>("MainBranch")
            .Annotate(new Core.Description("The central branch - the one that is always there."));
    }

    private static void ConfigureUnboundOperations(ODataConventionModelBuilder builder)
    {
        var totalMediaCount = builder.Function("TotalMediaCount");
        totalMediaCount.Namespace = OperationNamespace;
        totalMediaCount.Returns<long>();
        totalMediaCount.IncludeInServiceDocument = true;
        totalMediaCount.Annotate(new Core.Description("How many media the library holds, all types together."));

        var allLanguages = builder.Function("AllLanguages");
        allLanguages.Namespace = OperationNamespace;
        allLanguages.ReturnsCollection<string>();

        var loanStatistics = builder.Function("LoanStatistics");
        loanStatistics.Namespace = OperationNamespace;
        loanStatistics.Parameter<DateRange>("Period")
            .Annotate(new Core.Description("Restricts the statistics to a period; omit it for all time."))
            .Nullable = true;
        loanStatistics.Returns<LoanStats>();

        var statsPerBranch = builder.Function("StatsPerBranch");
        statsPerBranch.Namespace = OperationNamespace;
        statsPerBranch.ReturnsCollection<BranchStats>();

        var mostReadMedium = builder.Function("MostReadMedium");
        mostReadMedium.Namespace = OperationNamespace;
        mostReadMedium.ReturnsFromEntitySet<Medium>("Media");

        var newReleases = builder.Function("NewReleases");
        newReleases.Namespace = OperationNamespace;
        newReleases.ReturnsCollectionFromEntitySet<Medium>("Media");
        newReleases.IsComposable = true;
        newReleases.IncludeInServiceDocument = true;

        // Overload pair of the reference model: same name, differing number of parameters. The
        // annotations land on the right one of the two because operations are matched by their parameter
        // names, not by name alone.
        var search = builder.Function("Search");
        search.Namespace = OperationNamespace;
        search.Parameter<string>("Term").Annotate(new Core.Description("Matched against title and keywords."));
        search.ReturnsCollectionFromEntitySet<Medium>("Media");
        search.Annotate(new Core.Description("Media whose title or keywords match the term."));

        var searchLimited = builder.Function("Search");
        searchLimited.Namespace = OperationNamespace;
        searchLimited.Parameter<string>("Term").Annotate(new Core.Description("Matched against title and keywords."));
        searchLimited.Parameter<int>("MaxResults").Annotate(new Validation.Minimum(1));
        searchLimited.ReturnsCollectionFromEntitySet<Medium>("Media");
        searchLimited.Annotate(new Core.Description("As Search(Term), but returns at most MaxResults media."));

        var closureDay = builder.Action("ClosureDay");
        closureDay.Namespace = OperationNamespace;
        closureDay.Parameter<DateOnly>("Date").Annotate(new Core.Description("The day the library stays shut."));
        closureDay.Annotate(new Core.Description("Marks a day as a closure day; due dates move past it."));

        var nextInventoryNumber = builder.Action("NextInventoryNumber");
        nextInventoryNumber.Namespace = OperationNamespace;
        nextInventoryNumber.Returns<int>();

        var cleanUpKeywords = builder.Action("CleanUpKeywords");
        cleanUpKeywords.Namespace = OperationNamespace;
        cleanUpKeywords.CollectionParameter<string>("Obsolete");
        cleanUpKeywords.ReturnsCollection<string>();

        var yearEndClosing = builder.Action("YearEndClosing");
        yearEndClosing.Namespace = OperationNamespace;
        yearEndClosing.Parameter<int>("Year");
        yearEndClosing.Returns<AnnualReport>();

        var runOverdueNotices = builder.Action("RunOverdueNotices");
        runOverdueNotices.Namespace = OperationNamespace;
        runOverdueNotices.ReturnsCollection<OverdueNotice>();

        var acquire = builder.Action("AcquireCollectorsItem");
        acquire.Namespace = OperationNamespace;
        acquire.Parameter<string>("Title");
        acquire.Parameter<string>("Description").Nullable = true;
        acquire.ReturnsFromEntitySet<Medium>("Media");

        var runStockCheck = builder.Action("RunStockCheck");
        runStockCheck.Namespace = OperationNamespace;
        runStockCheck.ReturnsCollectionFromEntitySet<Medium>("Media");
    }

    /// <summary>
    /// Renames a bound operation's binding parameter. The builder calls it "bindingParameter" throughout,
    /// while the reference model names it after the bound type - which matters, because
    /// <c>EntitySetPath</c> refers to it by name (e.g. <c>medium/Copies</c>).
    /// </summary>
    private static void BindTo<T>(ODataModelBuilder builder, FunctionConfiguration function, string name) =>
        function.SetBindingParameter(name, builder.GetTypeConfigurationOrNull(typeof(T)));

    private static void BindTo<T>(ODataModelBuilder builder, ActionConfiguration action, string name) =>
        action.SetBindingParameter(name, builder.GetTypeConfigurationOrNull(typeof(T)));

    private static void BindToCollection<T>(ODataModelBuilder builder, FunctionConfiguration function, string name) =>
        function.SetBindingParameter(
            name,
            new CollectionTypeConfiguration(builder.GetTypeConfigurationOrNull(typeof(T)), typeof(IEnumerable<T>)));

    private static void BindToCollection<T>(ODataModelBuilder builder, ActionConfiguration action, string name) =>
        action.SetBindingParameter(
            name,
            new CollectionTypeConfiguration(builder.GetTypeConfigurationOrNull(typeof(T)), typeof(IEnumerable<T>)));

    private static void ConfigureBoundOperations(ODataConventionModelBuilder builder)
    {
        var memberType = builder.EntityType<Member>();
        var mediumType = builder.EntityType<Medium>();
        var copyType = builder.EntityType<Copy>();
        var loanType = builder.EntityType<Loan>();

        var outstandingBalance = memberType.Function("OutstandingBalance");
        outstandingBalance.Namespace = OperationNamespace;
        BindTo<Member>(builder, outstandingBalance, "member");
        outstandingBalance.Returns<decimal>();

        var availableLanguages = mediumType.Collection.Function("AvailableLanguages");
        availableLanguages.Namespace = OperationNamespace;
        BindToCollection<Medium>(builder, availableLanguages, "media");
        availableLanguages.ReturnsCollection<string>();

        var loanMetrics = mediumType.Function("LoanMetrics");
        loanMetrics.Namespace = OperationNamespace;
        BindTo<Medium>(builder, loanMetrics, "medium");
        loanMetrics.Returns<MediumStats>();

        var noticeHistory = memberType.Function("NoticeHistory");
        noticeHistory.Namespace = OperationNamespace;
        BindTo<Member>(builder, noticeHistory, "member");
        noticeHistory.ReturnsCollection<OverdueNotice>();

        var availableCopy = mediumType.Function("AvailableCopy");
        availableCopy.Namespace = OperationNamespace;
        BindTo<Medium>(builder, availableCopy, "medium");
        availableCopy.ReturnsFromEntitySet<Copy>("Copies");

        // Overload pair of the reference model: same name, bound once to a single instance and once to
        // a collection.
        var availableCopies = mediumType.Function("AvailableCopies");
        availableCopies.Namespace = OperationNamespace;
        BindTo<Medium>(builder, availableCopies, "medium");
        availableCopies.ReturnsCollectionFromEntitySet<Copy>("Copies");
        availableCopies.IsComposable = true;

        var availableCopiesForMany = mediumType.Collection.Function("AvailableCopies");
        availableCopiesForMany.Namespace = OperationNamespace;
        BindToCollection<Medium>(builder, availableCopiesForMany, "media");
        availableCopiesForMany.ReturnsCollectionFromEntitySet<Copy>("Copies");
        availableCopiesForMany.IsComposable = true;

        var checkOut = copyType.Action("CheckOut");
        checkOut.Namespace = OperationNamespace;
        BindTo<Copy>(builder, checkOut, "copy");
        checkOut.Parameter<int>("MemberId");

        var reserve = mediumType.Action("Reserve");
        reserve.Namespace = OperationNamespace;
        BindTo<Medium>(builder, reserve, "medium");
        reserve.Parameter<int>("MemberId");
        reserve.Returns<int?>();

        var bulkRenew = loanType.Collection.Action("BulkRenew");
        bulkRenew.Namespace = OperationNamespace;
        BindToCollection<Loan>(builder, bulkRenew, "loans");
        bulkRenew.ReturnsCollection<string>();

        var assessCondition = copyType.Action("AssessCondition");
        assessCondition.Namespace = OperationNamespace;
        BindTo<Copy>(builder, assessCondition, "copy");
        assessCondition.Parameter<byte>("NewCondition");
        assessCondition.Parameter<string>("Remark").Nullable = true;
        assessCondition.Returns<ConditionReport>();

        var runReminders = memberType.Action("RunReminders");
        runReminders.Namespace = OperationNamespace;
        BindTo<Member>(builder, runReminders, "member");
        runReminders.ReturnsCollection<OverdueNotice>();

        var renew = loanType.Action("Renew");
        renew.Namespace = OperationNamespace;
        BindTo<Loan>(builder, renew, "loan");
        renew.ReturnsFromEntitySet<Loan>("Loans");
        renew.Annotate(new Core.Description("Extends the loan's due date and returns the updated loan."));

        var renewAll = loanType.Collection.Action("RenewAll");
        renewAll.Namespace = OperationNamespace;
        BindToCollection<Loan>(builder, renewAll, "loans");
        renewAll.ReturnsCollectionFromEntitySet<Loan>("Loans");
    }
}
