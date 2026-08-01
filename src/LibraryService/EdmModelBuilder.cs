using Library.Catalog;
using Library.Circulation;
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

        ConfigureTypes(builder);
        ConfigureEntitySets(builder);
        ConfigureUnboundOperations(builder);
        ConfigureBoundOperations(builder);
        AlignNamespacesWithClrTypes(builder);

        return builder.GetEdmModel();
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
        medium.Property(m => m.PopularityScore).HasComputed().IsComputed(true);

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

        var member = builder.EntityType<Member>();
        member.Property(m => m.ActiveSince).Precision = 7;
        member.Property(m => m.Balance).Precision = 9;
        member.Property(m => m.Balance).Scale = 2;

        var loan = builder.EntityType<Loan>();
        loan.Property(l => l.LoanedAt).Precision = 7;
        loan.Property(l => l.ReturnedAt).Precision = 7;
        loan.Property(l => l.LateFee).Precision = 5;
        loan.Property(l => l.LateFee).Scale = 2;

        builder.EntityType<Reservation>().Property(r => r.ReservedAt).Precision = 7;
        builder.EntityType<IdDocument>().Property(d => d.UploadedAt).Precision = 7;
        copy.Property(c => c.IsLoanable).DefaultValueString = "true";

        // Delete behaviour and required navigation. `NavigationPropertyConfiguration.Partner` is
        // read-only, and neither `HasMany` nor the convention builder relates the two sides - but the
        // three-argument overloads of HasRequired/HasOptional do. The referential constraint in the
        // middle may be null, which is what makes them usable here: unlike Copy/Medium, these
        // associations have no foreign key property in the reference model.
        member.HasMany(m => m.Loans).CascadeOnDelete();
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
    }

    private static void ConfigureUnboundOperations(ODataConventionModelBuilder builder)
    {
        var totalMediaCount = builder.Function("TotalMediaCount");
        totalMediaCount.Namespace = OperationNamespace;
        totalMediaCount.Returns<long>();
        totalMediaCount.IncludeInServiceDocument = true;

        var allLanguages = builder.Function("AllLanguages");
        allLanguages.Namespace = OperationNamespace;
        allLanguages.ReturnsCollection<string>();

        var loanStatistics = builder.Function("LoanStatistics");
        loanStatistics.Namespace = OperationNamespace;
        loanStatistics.Parameter<DateRange>("Period").Nullable = true;
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

        // Overload pair of the reference model: same name, differing number of parameters.
        var search = builder.Function("Search");
        search.Namespace = OperationNamespace;
        search.Parameter<string>("Term");
        search.ReturnsCollectionFromEntitySet<Medium>("Media");

        var searchLimited = builder.Function("Search");
        searchLimited.Namespace = OperationNamespace;
        searchLimited.Parameter<string>("Term");
        searchLimited.Parameter<int>("MaxResults");
        searchLimited.ReturnsCollectionFromEntitySet<Medium>("Media");

        var closureDay = builder.Action("ClosureDay");
        closureDay.Namespace = OperationNamespace;
        closureDay.Parameter<DateOnly>("Date");

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

        var renewAll = loanType.Collection.Action("RenewAll");
        renewAll.Namespace = OperationNamespace;
        BindToCollection<Loan>(builder, renewAll, "loans");
        renewAll.ReturnsCollectionFromEntitySet<Loan>("Loans");
    }
}
