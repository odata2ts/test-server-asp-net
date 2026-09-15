using Library.Catalog;
using Library.Circulation;
using LibraryService.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Deltas;
using Microsoft.AspNetCore.OData.Extensions;
using Microsoft.AspNetCore.OData.Formatter;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Results;
using Microsoft.AspNetCore.OData.Routing.Controllers;
using Microsoft.EntityFrameworkCore;
using LibraryService.Annotations;

namespace LibraryService.Controllers;

/// <summary>
/// The plain entity sets. `[EnableQuery]` hands `$select`, `$filter`, `$orderby`, `$top`, `$skip`,
/// `$count` and `$expand` to the OData layer, which applies them to the returned <see cref="IQueryable{T}" />.
///
/// A queryable the database still has to answer goes out <c>AsNoTracking</c> throughout: the change
/// tracker exists for the write paths, and a read that fills it buys nothing. It is not only tidiness -
/// <c>$select</c> on a complex property makes OData project the owned type on its own
/// (<c>Address($select=*)</c>), and EF refuses to track an owned entity apart from its owner, so the
/// request failed outright. The write paths query separately and stay tracked.
/// </summary>
public class MediaController(LibraryContext db) : ODataController
{
    [EnableQuery(MaxExpansionDepth = 4)]
    public IQueryable<Medium> Get() => db.Media.AsNoTracking();

    [EnableQuery]
    public SingleResult<Medium> Get([FromRoute] Guid key) =>
        SingleResult.Create(db.Media.AsNoTracking().Where(m => m.Id == key));

    /// <summary>
    /// Addressing by the <c>Core.AlternateKeys</c> key on <c>PrintMedium/ISBN</c>. The route template has
    /// to be spelled out: a conventional <c>Get(string keyISBN)</c> action is not matched, the request
    /// then runs off the end of the middleware pipeline.
    /// </summary>
    [HttpGet("odata/v4/library/Media/Library.Catalog.PrintMedium(ISBN={isbn})")]
    [EnableQuery]
    public SingleResult<PrintMedium> GetByIsbn([FromRoute] string isbn)
    {
        var value = isbn.Trim('\'');
        return SingleResult.Create(db.Media.AsNoTracking().OfType<PrintMedium>().Where(m => m.ISBN == value));
    }

    /// <summary>Type-cast segment, e.g. <c>/Media/Library.Catalog.Book</c>.</summary>
    [EnableQuery]
    public IQueryable<Book> GetFromBook() => db.Media.AsNoTracking().OfType<Book>();

    [EnableQuery]
    public IQueryable<PrintMedium> GetFromPrintMedium() => db.Media.AsNoTracking().OfType<PrintMedium>();

    [EnableQuery]
    public IQueryable<Magazine> GetFromMagazine() => db.Media.AsNoTracking().OfType<Magazine>();

    [EnableQuery]
    public IQueryable<TradeJournal> GetFromTradeJournal() => db.Media.AsNoTracking().OfType<TradeJournal>();

    [EnableQuery]
    public IQueryable<AudioMedium> GetFromAudioMedium() => db.Media.AsNoTracking().OfType<AudioMedium>();

    [EnableQuery]
    public IQueryable<DVD> GetFromDVD() => db.Media.AsNoTracking().OfType<DVD>();

    [EnableQuery]
    public IQueryable<EBook> GetFromEBook() => db.Media.AsNoTracking().OfType<EBook>();

    [EnableQuery]
    public IQueryable<Audiobook> GetFromAudiobook() => db.Media.AsNoTracking().OfType<Audiobook>();

    [EnableQuery]
    public IQueryable<CollectorsItem> GetFromCollectorsItem() => db.Media.AsNoTracking().OfType<CollectorsItem>();

    [EnableQuery]
    public IQueryable<Copy> GetCopies([FromRoute] Guid key) =>
        db.Copies.AsNoTracking().Where(c => c.MediumId == key);

    /// <summary>
    /// A single copy reached through its medium, by its own composite key - routed explicitly for the same
    /// reason as <see cref="CopiesController"/>'s own composite-key route.
    /// </summary>
    [HttpGet("odata/v4/library/Media({key})/Copies(MediumId={copyMediumId},InventoryNumber={copyInventoryNumber})")]
    [EnableQuery]
    public SingleResult<Copy> GetCopy([FromRoute] Guid key, [FromRoute] Guid copyMediumId, [FromRoute] int copyInventoryNumber) =>
        SingleResult.Create(
            db.Copies.AsNoTracking()
                .Where(c => c.MediumId == key && c.MediumId == copyMediumId && c.InventoryNumber == copyInventoryNumber));

    /// <summary>
    /// Patches a copy reached through its medium - the same resource <see cref="CopiesController.Patch"/>
    /// addresses directly, so it shares that method's concurrency check and patch application rather than
    /// risking the two routes drifting apart.
    /// </summary>
    [HttpPatch("odata/v4/library/Media({key})/Copies(MediumId={copyMediumId},InventoryNumber={copyInventoryNumber})")]
    public IActionResult PatchCopy(
        [FromRoute] Guid key,
        [FromRoute] Guid copyMediumId,
        [FromRoute] int copyInventoryNumber,
        Delta<Copy>? delta)
    {
        if (key != copyMediumId)
        {
            return NotFound();
        }

        var existing = CopiesController.Find(db, copyMediumId, copyInventoryNumber, q => q.Include(c => c.Location));
        if (existing is null)
        {
            return NotFound();
        }

        if (CopiesController.CheckConcurrency(Request, existing) is { } precondition)
        {
            return precondition;
        }

        return CopiesController.ApplyPatch(db, Request, existing, delta) is { } badRequest ? badRequest : Updated(existing);
    }

    [EnableQuery]
    public ActionResult<PublisherRegistry.Publisher> GetPublisherFromBook([FromRoute] Guid key) =>
        db.Media.OfType<Book>().Include(b => b.Publisher).FirstOrDefault(b => b.Id == key)?.Publisher is { } publisher
            ? publisher
            : NotFound();

    /// <summary>
    /// Contained entities, reachable only through their audiobook. A 404, not an empty collection, where
    /// the casted entity does not exist or is not of the cast type - the cast segment is part of the
    /// resource path, and a path that does not resolve is an error (OData V4.01 Part 2, §4.11).
    /// </summary>
    [EnableQuery]
    public ActionResult<IQueryable<AudiobookChapter>> GetChaptersFromAudiobook([FromRoute] Guid key) =>
        db.Media
            .OfType<Audiobook>()
            .Include(a => a.Chapters)
            .FirstOrDefault(a => a.Id == key)?
            .Chapters?
            .AsQueryable() is { } chapters
            ? Queried(chapters)
            : NotFound();

    // --- single entity through a type cast --------------------------------------------------------
    //
    // The conventional routes `~/Media({key})/Library.Catalog.{Type}` bind to actions named
    // `{Method}{CastType}` - GetBook, PutBook, … - exactly as `~/Media/Library.Catalog.Book` binds to
    // GetFromBook above. The body of every verb is one shared line: the entity is looked up as the cast
    // type, so a medium that is not of it 404s rather than answering under the wrong type, and the verb
    // then does what the same verb does on the uncast route.

    [EnableQuery]
    public SingleResult<Book> GetBook([FromRoute] Guid key) => GetCast<Book>(key);

    [EnableQuery]
    public SingleResult<PrintMedium> GetPrintMedium([FromRoute] Guid key) => GetCast<PrintMedium>(key);

    [EnableQuery]
    public SingleResult<Magazine> GetMagazine([FromRoute] Guid key) => GetCast<Magazine>(key);

    [EnableQuery]
    public SingleResult<TradeJournal> GetTradeJournal([FromRoute] Guid key) => GetCast<TradeJournal>(key);

    [EnableQuery]
    public SingleResult<AudioMedium> GetAudioMedium([FromRoute] Guid key) => GetCast<AudioMedium>(key);

    [EnableQuery]
    public SingleResult<Audiobook> GetAudiobook([FromRoute] Guid key) => GetCast<Audiobook>(key);

    [EnableQuery]
    public SingleResult<DVD> GetDVD([FromRoute] Guid key) => GetCast<DVD>(key);

    [EnableQuery]
    public SingleResult<EBook> GetEBook([FromRoute] Guid key) => GetCast<EBook>(key);

    [EnableQuery]
    public SingleResult<CollectorsItem> GetCollectorsItem([FromRoute] Guid key) => GetCast<CollectorsItem>(key);

    /// <summary>
    /// Replaces the entity's own state through the cast. The route already narrows to the concrete type,
    /// so the payload needs no <c>@odata.type</c> discriminator - the same reason
    /// <see cref="PublishersController.PatchBook"/> needs none.
    /// </summary>
    public IActionResult PutBook([FromRoute] Guid key, [FromBody] Book book) => PutCast<Book>(key, book);

    public IActionResult PutPrintMedium([FromRoute] Guid key, [FromBody] PrintMedium printMedium) =>
        PutCast<PrintMedium>(key, printMedium);

    public IActionResult PutMagazine([FromRoute] Guid key, [FromBody] Magazine magazine) =>
        PutCast<Magazine>(key, magazine);

    public IActionResult PutTradeJournal([FromRoute] Guid key, [FromBody] TradeJournal tradeJournal) =>
        PutCast<TradeJournal>(key, tradeJournal);

    public IActionResult PutAudioMedium([FromRoute] Guid key, [FromBody] AudioMedium audioMedium) =>
        PutCast<AudioMedium>(key, audioMedium);

    public IActionResult PutAudiobook([FromRoute] Guid key, [FromBody] Audiobook audiobook) =>
        PutCast<Audiobook>(key, audiobook);

    public IActionResult PutDVD([FromRoute] Guid key, [FromBody] DVD dvd) => PutCast<DVD>(key, dvd);

    public IActionResult PutEBook([FromRoute] Guid key, [FromBody] EBook eBook) => PutCast<EBook>(key, eBook);

    public IActionResult PutCollectorsItem([FromRoute] Guid key, [FromBody] CollectorsItem collectorsItem) =>
        PutCast<CollectorsItem>(key, collectorsItem);

    public IActionResult PatchBook([FromRoute] Guid key, Delta<Book>? delta) => PatchCast<Book>(key, delta);

    public IActionResult PatchPrintMedium([FromRoute] Guid key, Delta<PrintMedium>? delta) =>
        PatchCast<PrintMedium>(key, delta);

    public IActionResult PatchMagazine([FromRoute] Guid key, Delta<Magazine>? delta) =>
        PatchCast<Magazine>(key, delta);

    public IActionResult PatchTradeJournal([FromRoute] Guid key, Delta<TradeJournal>? delta) =>
        PatchCast<TradeJournal>(key, delta);

    public IActionResult PatchAudioMedium([FromRoute] Guid key, Delta<AudioMedium>? delta) =>
        PatchCast<AudioMedium>(key, delta);

    public IActionResult PatchAudiobook([FromRoute] Guid key, Delta<Audiobook>? delta) =>
        PatchCast<Audiobook>(key, delta);

    public IActionResult PatchDVD([FromRoute] Guid key, Delta<DVD>? delta) => PatchCast<DVD>(key, delta);

    public IActionResult PatchEBook([FromRoute] Guid key, Delta<EBook>? delta) => PatchCast<EBook>(key, delta);

    public IActionResult PatchCollectorsItem([FromRoute] Guid key, Delta<CollectorsItem>? delta) =>
        PatchCast<CollectorsItem>(key, delta);

    public IActionResult DeleteBook([FromRoute] Guid key) => DeleteCast<Book>(key);

    public IActionResult DeletePrintMedium([FromRoute] Guid key) => DeleteCast<PrintMedium>(key);

    public IActionResult DeleteMagazine([FromRoute] Guid key) => DeleteCast<Magazine>(key);

    public IActionResult DeleteTradeJournal([FromRoute] Guid key) => DeleteCast<TradeJournal>(key);

    public IActionResult DeleteAudioMedium([FromRoute] Guid key) => DeleteCast<AudioMedium>(key);

    public IActionResult DeleteAudiobook([FromRoute] Guid key) => DeleteCast<Audiobook>(key);

    public IActionResult DeleteDVD([FromRoute] Guid key) => DeleteCast<DVD>(key);

    public IActionResult DeleteEBook([FromRoute] Guid key) => DeleteCast<EBook>(key);

    public IActionResult DeleteCollectorsItem([FromRoute] Guid key) => DeleteCast<CollectorsItem>(key);

    /// <summary>
    /// The copies through the cast - the same collection <see cref="GetCopies"/> serves over the base
    /// route, narrowed to the cast type. <c>Copies</c> is declared on the base <see cref="Medium"/> and
    /// inherited by every subtype, so the cast narrows the source, not the navigation.
    /// </summary>
    [EnableQuery]
    public ActionResult<IQueryable<Copy>> GetCopiesFromBook([FromRoute] Guid key) => GetCopiesFromCast<Book>(key);

    public ActionResult<IQueryable<Copy>> GetCopiesFromPrintMedium([FromRoute] Guid key) =>
        GetCopiesFromCast<PrintMedium>(key);

    public ActionResult<IQueryable<Copy>> GetCopiesFromMagazine([FromRoute] Guid key) =>
        GetCopiesFromCast<Magazine>(key);

    public ActionResult<IQueryable<Copy>> GetCopiesFromTradeJournal([FromRoute] Guid key) =>
        GetCopiesFromCast<TradeJournal>(key);

    public ActionResult<IQueryable<Copy>> GetCopiesFromAudioMedium([FromRoute] Guid key) =>
        GetCopiesFromCast<AudioMedium>(key);

    public ActionResult<IQueryable<Copy>> GetCopiesFromAudiobook([FromRoute] Guid key) =>
        GetCopiesFromCast<Audiobook>(key);

    public ActionResult<IQueryable<Copy>> GetCopiesFromDVD([FromRoute] Guid key) => GetCopiesFromCast<DVD>(key);

    public ActionResult<IQueryable<Copy>> GetCopiesFromEBook([FromRoute] Guid key) =>
        GetCopiesFromCast<EBook>(key);

    public ActionResult<IQueryable<Copy>> GetCopiesFromCollectorsItem([FromRoute] Guid key) =>
        GetCopiesFromCast<CollectorsItem>(key);

    private SingleResult<T> GetCast<T>(Guid key)
        where T : Medium =>
        SingleResult.Create(db.Media.AsNoTracking().OfType<T>().Where(m => m.Id == key));

    public IActionResult Post([FromBody] Medium medium)
    {
        // As in <see cref="Patch"/>: without @odata.type the deserializer has no type to construct, and
        // model binding hands the action a null instead of an error - a malformed request, answered 400
        // rather than dereferenced into a 500.
        if (medium is null)
        {
            return BadRequest(
                "The request body could not be read as a Medium. The Media entity set is declared as the "
                + "abstract type Library.Catalog.Medium, so the payload has to name the concrete type it "
                + "creates, e.g. \"@odata.type\": \"#Library.Catalog.Book\".");
        }

        if (!TryCreate(medium, out var created))
        {
            return BadRequest("A navigation binding in the request body names an entity that does not exist.");
        }

        db.SaveChanges();
        return Created(created);
    }

    /// <summary>
    /// The create's own rules, shared with the delta upsert on a cast collection - see
    /// <see cref="PatchFromBook"/>: the managed properties are the server's on insert, the key is
    /// assigned where the client has no say, the binding is resolved before the graph is tracked, and a
    /// nested copy becomes addressable as <c>/Copies</c>. Nothing is saved here: a delta set saves its
    /// entries together.
    /// </summary>
    private bool TryCreate(Medium medium, out Medium created)
    {
        created = null!;

        // A computed property is the server's on insert as much as on update, so a value the client sent
        // goes no further than here - the delta filter does the same for PATCH, which binds no entity.
        medium.IgnoreManagedOnInsert(HttpContext.ODataFeature().Model);

        if (medium.Id == Guid.Empty)
        {
            medium.Id = Guid.NewGuid();
        }

        // Before the graph is tracked: a navigation the payload bound - the publisher of a book, the
        // branch a nested copy is shelved at - has to be linked, and Add would insert it.
        if (!NavigationBinding.Resolve(db, Request, medium))
        {
            return false;
        }

        db.Media.Add(medium);

        // Deep insert: copies that arrived nested must also become addressable as /Copies. Adding the
        // medium already puts them into the change tracker through the navigation property, so only the
        // foreign key still has to be filled in - the previous `db.Copies.Contains(copy)` guard would now
        // query the database for entities that are not in it yet.
        foreach (var copy in medium.Copies)
        {
            copy.MediumId = medium.Id;
            copy.Medium = medium;
        }

        created = medium;
        return true;
    }

    /// <summary>
    /// Patches a medium.
    ///
    /// The delta is nullable because it genuinely arrives null: <c>Media</c> is declared as the abstract
    /// <c>Library.Catalog.Medium</c>, so an entity in it is always of a derived type, and OData JSON
    /// requires <c>@odata.type</c> whenever the instance's type is derived from the declared one. Without
    /// it the deserializer cannot decide what to construct and model binding yields null - which used to
    /// be dereferenced, answering 500 to what is really a malformed request.
    /// </summary>
    public IActionResult Patch([FromRoute] Guid key, Delta<Medium>? delta)
    {
        var existing = db.Media.FirstOrDefault(m => m.Id == key);
        if (existing is null)
        {
            return NotFound();
        }

        if (delta is null)
        {
            return BadRequest(
                "The request body could not be read as a Medium. The Media entity set is declared as the "
                + "abstract type Library.Catalog.Medium, so the payload has to name the concrete type it "
                + "is patching, e.g. \"@odata.type\": \"#Library.Catalog.Book\".");
        }

        delta.Patch(existing);
        db.SaveChanges();
        return Updated(existing);
    }

    /// <summary>
    /// Replaces the medium's own state, as <see cref="MembersController.Put"/> does for a member: the
    /// scalar and complex properties come from the payload, the relationships and the properties the
    /// client may not change keep what is stored. The payload names the concrete type, as every payload
    /// on this abstract-typed set does - and a body the deserializer cannot construct arrives null, the
    /// same malformed request <see cref="Patch"/> answers 400 to.
    /// </summary>
    public IActionResult Put([FromRoute] Guid key, [FromBody] Medium medium)
    {
        var existing = db.Media.FirstOrDefault(m => m.Id == key);
        if (existing is null)
        {
            return NotFound();
        }

        if (medium is null)
        {
            return BadRequest(
                "The request body could not be read as a Medium. The Media entity set is declared as the "
                + "abstract type Library.Catalog.Medium, so the payload has to name the concrete type it "
                + "updates, e.g. \"@odata.type\": \"#Library.Catalog.Book\".");
        }

        medium.Id = key;
        // A PUT replaces the entity, but not the properties the client may not change: the spec exempts
        // them from the reset an omission otherwise causes, so they keep the value that is stored.
        medium.IgnoreManagedOnUpdate(existing, HttpContext.ODataFeature().Model);
        db.Entry(existing).CurrentValues.SetValues(medium);

        db.SaveChanges();
        return Updated(existing);
    }

    /// <summary>
    /// Deletes a medium. Its copies go with it: the reference model's cascade is declared on the relational
    /// side too, so the database enforces it rather than the controller walking the graph.
    /// </summary>
    public IActionResult Delete([FromRoute] Guid key)
    {
        var existing = db.Media.FirstOrDefault(m => m.Id == key);
        if (existing is null)
        {
            return NotFound();
        }

        db.Media.Remove(existing);
        db.SaveChanges();
        return NoContent();
    }

    /// <summary>
    /// The delta set on a cast collection (OData 4.01) - the cast version of
    /// <see cref="MembersController.PatchCollection"/>: a mixed batch of upserts and deletions against the
    /// derived-type view of the set in one request. An entry for an entity that is being removed arrives as
    /// a <see cref="DeltaDeletedResource{T}"/>.
    /// </summary>
    public IActionResult PatchFromBook([FromBody] DeltaSet<Book> deltaSet) => PatchFromCast<Book>(deltaSet);

    public IActionResult PatchFromPrintMedium([FromBody] DeltaSet<PrintMedium> deltaSet) =>
        PatchFromCast<PrintMedium>(deltaSet);

    public IActionResult PatchFromMagazine([FromBody] DeltaSet<Magazine> deltaSet) => PatchFromCast<Magazine>(deltaSet);

    public IActionResult PatchFromTradeJournal([FromBody] DeltaSet<TradeJournal> deltaSet) =>
        PatchFromCast<TradeJournal>(deltaSet);

    public IActionResult PatchFromAudioMedium([FromBody] DeltaSet<AudioMedium> deltaSet) =>
        PatchFromCast<AudioMedium>(deltaSet);

    public IActionResult PatchFromAudiobook([FromBody] DeltaSet<Audiobook> deltaSet) =>
        PatchFromCast<Audiobook>(deltaSet);

    public IActionResult PatchFromDVD([FromBody] DeltaSet<DVD> deltaSet) => PatchFromCast<DVD>(deltaSet);

    public IActionResult PatchFromEBook([FromBody] DeltaSet<EBook> deltaSet) => PatchFromCast<EBook>(deltaSet);

    public IActionResult PatchFromCollectorsItem([FromBody] DeltaSet<CollectorsItem> deltaSet) =>
        PatchFromCast<CollectorsItem>(deltaSet);

    private IActionResult PutCast<T>(Guid key, T incoming)
        where T : Medium
    {
        var existing = db.Media.OfType<T>().FirstOrDefault(m => m.Id == key);
        if (existing is null)
        {
            return NotFound();
        }

        // As in <see cref="Put"/>: a body the deserializer cannot construct - on the abstract casts a
        // payload that names no concrete subtype, on a concrete cast one that names a type the entity is
        // not of - arrives null, and a malformed request is answered 400, never dereferenced into a 500.
        if (incoming is null)
        {
            return BadRequest(
                $"The request body could not be read as a {typeof(T).Name}. The payload has to name the "
                + "concrete type the entity is, e.g. \"@odata.type\": \"#Library.Catalog.Book\".");
        }

        incoming.Id = key;
        incoming.IgnoreManagedOnUpdate(existing, HttpContext.ODataFeature().Model);
        db.Entry(existing).CurrentValues.SetValues(incoming);

        db.SaveChanges();
        return Updated(existing);
    }

    private IActionResult PatchCast<T>(Guid key, Delta<T>? delta)
        where T : Medium
    {
        var existing = db.Media.OfType<T>().FirstOrDefault(m => m.Id == key);
        if (existing is null)
        {
            return NotFound();
        }

        // As in <see cref="Patch"/>: a null delta means the deserializer could not decide what to build -
        // certain when the cast type is abstract and the payload does not name a concrete subtype.
        if (delta is null)
        {
            return BadRequest(
                $"The request body could not be read as a {typeof(T).Name}. The Media entity set is declared "
                + "as the abstract type Library.Catalog.Medium, so the payload has to name the concrete type "
                + "it is patching, e.g. \"@odata.type\": \"#Library.Catalog.Book\".");
        }

        delta.Patch(existing);
        db.SaveChanges();
        return Updated(existing);
    }

    private IActionResult DeleteCast<T>(Guid key)
        where T : Medium
    {
        var existing = db.Media.OfType<T>().FirstOrDefault(m => m.Id == key);
        if (existing is null)
        {
            return NotFound();
        }

        db.Media.Remove(existing);
        db.SaveChanges();
        return NoContent();
    }

    /// <summary>
    /// The copies through the cast: the same collection <see cref="GetCopies"/> serves over the base route,
    /// narrowed to the cast type. A 404, not an empty collection, where the entity is not of the cast type -
    /// the cast segment is part of the resource path (OData V4.01 Part 2, §4.11).
    /// </summary>
    private ActionResult<IQueryable<Copy>> GetCopiesFromCast<T>(Guid key)
        where T : Medium =>
        db.Media.OfType<T>().FirstOrDefault(m => m.Id == key) is { } medium
            ? Queried(db.Copies.AsNoTracking().Where(c => c.MediumId == medium.Id))
            : NotFound();

    /// <summary>
    /// Wraps a queryable in the <see cref="ActionResult{T}"/> the collection-cast routes return, so that a cast the entity
    /// is not of can answer <c>NotFound()</c> instead of an empty collection - while <c>[EnableQuery]</c> still applies the
    /// query options to the queryable on the way out. The <c>ActionResult</c> constructor has to be called explicitly: C# never
    /// applies a user-defined conversion whose source type is an interface, and the queryable is one.
    /// </summary>
    private static ActionResult<IQueryable<T>> Queried<T>(IQueryable<T> queryable) => new(queryable);

    private IActionResult PatchFromCast<T>(DeltaSet<T>? deltaSet)
        where T : Medium
    {
        // A delta set the deserializer could not build arrives null - as a single delta does on the base
        // route - the body was empty, or an entry names a type the set cannot hold: on the abstract casts
        // that is an entry without any concrete subtype at all.
        if (deltaSet is null)
        {
            return UnreadableDeltaSet<T>();
        }

        foreach (var item in deltaSet)
        {
            switch (item)
            {
                case DeltaDeletedResource<T> removed:
                    if (removed.GetInstance() is null)
                    {
                        return UnreadableDeltaEntry<T>();
                    }

                    if (KeyOf(removed) is { } removedId
                        && db.Media.OfType<T>().FirstOrDefault(m => m.Id == removedId) is { } toRemove)
                    {
                        db.Media.Remove(toRemove);
                    }

                    break;

                case Delta<T> delta:
                    if (delta.GetInstance() is not T instance)
                    {
                        return UnreadableDeltaEntry<T>();
                    }

                    var id = KeyOf(delta);
                    if (id is not null && db.Media.OfType<T>().FirstOrDefault(m => m.Id == id) is { } existing)
                    {
                        delta.Patch(existing);
                    }
                    else
                    {
                        // Upsert: an entry whose key is unknown creates the entity, and a create is a create
                        // even through the cast - it follows the same rules as POST /Media.
                        if (!TryCreate(instance, out _))
                        {
                            return BadRequest("A navigation binding in the request body names an entity that does not exist.");
                        }
                    }

                    break;
            }
        }

        db.SaveChanges();
        return Ok(deltaSet);
    }

    /// <summary>
    /// The 400 a delta set the deserializer could not build at all gets: it arrives null, as a single delta
    /// arrives null on the base route - the body was empty, or an entry names a type the set cannot hold,
    /// on the abstract casts a concrete subtype. Dereferencing it used to answer 500 to what is a
    /// malformed request.
    /// </summary>
    private IActionResult UnreadableDeltaSet<T>()
        where T : Medium =>
        BadRequest(
            $"The request body could not be read as a delta set of type {typeof(T).Name}. A delta set "
            + "carries its entries under the 'value' property, and where the cast type is abstract, every "
            + "entry has to name a concrete subtype, e.g. \"@odata.type\": \"#Library.Catalog.Book\".");

    /// <summary>
    /// The 400 a delta entry that the deserializer could not materialize as the cast type gets. The
    /// deserializer tends to refuse the whole set instead - which
    /// <see cref="UnreadableDeltaSet{T}"/> answers - but an entry it kept and could not read as the cast
    /// type must not be dereferenced into a 500.
    /// </summary>
    private IActionResult UnreadableDeltaEntry<T>()
        where T : Medium =>
        BadRequest(
            $"An entry of the delta set could not be read as an entity of type {typeof(T).Name}. Its "
            + "\"@odata.type\" has to name that type or a concrete subtype of it, e.g. "
            + "\"@odata.type\": \"#Library.Catalog.Book\".");

    /// <summary>
    /// The key of a delta entry, or <c>null</c> where the entry carries no readable property at all. An
    /// entry that simply omits the key still materializes it, with the CLR default - so a keyless entry
    /// reads as <c>Guid.Empty</c>, which no stored entity matches, and the upsert decision is the database
    /// lookup's to make.
    /// </summary>
    private static Guid? KeyOf<T>(Delta<T> delta)
        where T : Medium =>
        delta.TryGetPropertyValue(nameof(Medium.Id), out var value) ? (Guid)value : default;
}

public class CopiesController(LibraryContext db) : ODataController
{
    [EnableQuery]
    public IQueryable<Copy> Get() => db.Copies.AsNoTracking();

    /// <summary>
    /// Composite key. Routed explicitly: the convention builds `keyMediumId` / `keyInventoryNumber`
    /// route values, but does not match the two-part key template on its own.
    /// </summary>
    [HttpGet("odata/v4/library/Copies(MediumId={keyMediumId},InventoryNumber={keyInventoryNumber})")]
    [EnableQuery]
    public SingleResult<Copy> Get([FromRoute] Guid keyMediumId, [FromRoute] int keyInventoryNumber) =>
        SingleResult.Create(
            db.Copies.AsNoTracking().Where(c => c.MediumId == keyMediumId && c.InventoryNumber == keyInventoryNumber));

    [HttpGet("odata/v4/library/Copies(MediumId={keyMediumId},InventoryNumber={keyInventoryNumber})/Medium")]
    [EnableQuery]
    public ActionResult<Medium> GetMedium([FromRoute] Guid keyMediumId, [FromRoute] int keyInventoryNumber) =>
        Find(db, keyMediumId, keyInventoryNumber, q => q.Include(c => c.Medium))?.Medium is { } medium
            ? medium
            : NotFound();

    [HttpPatch("odata/v4/library/Copies(MediumId={keyMediumId},InventoryNumber={keyInventoryNumber})")]
    public IActionResult Patch([FromRoute] Guid keyMediumId, [FromRoute] int keyInventoryNumber, Delta<Copy>? delta)
    {
        var existing = Find(db, keyMediumId, keyInventoryNumber, q => q.Include(c => c.Location));
        if (existing is null)
        {
            return NotFound();
        }

        if (CheckConcurrency(Request, existing) is { } precondition)
        {
            return precondition;
        }

        return ApplyPatch(db, Request, existing, delta) is { } badRequest ? badRequest : Updated(existing);
    }

    /// <summary>
    /// Applies a Copy patch's delta and <c>Location</c> binding - shared with <see cref="MediaController"/>'s
    /// nested <c>Media({key})/Copies(...)</c> route, which reaches this same resource by a different path
    /// and must behave identically. Returns a <c>400</c> where neither the delta nor a binding could be
    /// read, <c>null</c> on success (the caller already knows what "updated" looks like for its own route).
    /// </summary>
    internal static IActionResult? ApplyPatch(LibraryContext db, HttpRequest request, Copy existing, Delta<Copy>? delta)
    {
        var boundBranchId = NavigationBinding.Read(request, nameof(Copy.Location), NavigationBinding.AsInt);
        var clearsBranch = NavigationBinding.ClearsLink(request, nameof(Copy.Location));

        // A body the deserializer refused - binding a navigation to null is one such case - arrives as a
        // null delta. That is only recoverable because the binding was read from the raw body: with no
        // binding either, nothing in the request can be applied, and saying so beats reporting the 204
        // that a silently skipped patch would have produced.
        if (delta is null && boundBranchId is null && !clearsBranch)
        {
            return new BadRequestObjectResult("The request body could not be read as a Copy.");
        }

        // Keep the current link out of Patch's reach: a bound stub would be written into it.
        var currentLocation = existing.Location;
        existing.Location = null;

        delta?.Patch(existing);

        existing.Location = boundBranchId is { } branchId
            ? db.Branches.FirstOrDefault(b => b.Id == branchId)
            : clearsBranch ? null : currentLocation;

        db.SaveChanges();
        return null;
    }

    /// <summary>Navigation to the branch the copy is shelved at.</summary>
    [HttpGet("odata/v4/library/Copies(MediumId={keyMediumId},InventoryNumber={keyInventoryNumber})/Location")]
    [EnableQuery]
    public ActionResult<Branch> GetLocation([FromRoute] Guid keyMediumId, [FromRoute] int keyInventoryNumber) =>
        Find(db, keyMediumId, keyInventoryNumber, q => q.Include(c => c.Location))?.Location is { } branch
            ? branch
            : NotFound();

    /// <summary>
    /// Creates a copy. Accepts the navigation property either inline (deep insert) or as a reference -
    /// `Medium@odata.bind` in 4.0, or a nested `{"@id": …}` in 4.01.
    /// </summary>
    public async Task<IActionResult> Post()
    {
        // Read the whole payload here rather than through `[FromBody] Delta<Copy>`. The OData
        // deserializer refuses a body that binds a navigation property backed by a referential
        // constraint - `Medium@odata.bind` on a Copy - and rejects it wholesale with 400. Parsing the
        // body directly is the only way to accept both binding notations on such a property.
        var copy = await ReadCopyFromBody();
        if (copy is null)
        {
            return BadRequest("The request body could not be read as a Copy.");
        }

        if (db.Media.FirstOrDefault(m => m.Id == copy.MediumId) is not { } medium)
        {
            return BadRequest("The referenced medium does not exist.");
        }

        // A second copy with the same composite key used to be accepted, after which a keyed read failed
        // with "SingleResult must have zero or one elements" - a store that cannot be read from any more.
        if (Find(db, copy.MediumId, copy.InventoryNumber) is not null)
        {
            return Conflict($"A copy with inventory number {copy.InventoryNumber} exists for this medium.");
        }

        copy.Medium = medium;
        db.Copies.Add(copy);
        db.SaveChanges();
        return Created(copy);
    }

    /// <summary>
    /// Enforces the optimistic concurrency this entity set announces.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Core.OptimisticConcurrency</c> in the EDM is a promise, and declaring the token to EF Core is not
    /// enough to keep it: the entity is loaded fresh before every write, so the original value EF puts into
    /// the <c>WHERE</c> clause is always the current one and the update can never lose. Without this check
    /// a stale <c>If-Match</c> was accepted, which is worse than not announcing the feature - a client that
    /// probes for the announcement concludes it is protected when it is not.
    /// </para>
    /// <para>
    /// Returns 428 where the client sent no precondition at all and 412 where it sent one that is no longer
    /// current; <c>If-Match: *</c> matches anything by definition (OData V4.01 Part 1, §8.2.1). The current
    /// token is built through the framework's own ETag handler, so it is compared against exactly the value
    /// the same handler emitted on the read.
    /// </para>
    /// </remarks>
    /// <summary>
    /// <c>internal static</c>, not an instance method: <see cref="MediaController"/>'s own nested
    /// <c>Media({key})/Copies(...)</c> routes enforce the same promise and have no <see cref="CopiesController"/>
    /// instance of their own to call this on.
    /// </summary>
    internal static IActionResult? CheckConcurrency(HttpRequest request, Copy existing)
    {
        var ifMatch = request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            return new StatusCodeResult(StatusCodes.Status428PreconditionRequired);
        }
        if (ifMatch.Trim() == "*")
        {
            return null;
        }

        var current = request.GetETagHandler()
            .CreateETag(new Dictionary<string, object?> { [nameof(Copy.Condition)] = existing.Condition })
            .ToString();

        // a client may offer several tokens; the request succeeds if any of them is current
        return ifMatch.Split(',').Any(candidate => candidate.Trim() == current)
            ? null
            : new StatusCodeResult(StatusCodes.Status412PreconditionFailed);
    }

    /// <summary>Deletes a copy. Routed explicitly for the same reason as the other composite-key routes.</summary>
    [HttpDelete("odata/v4/library/Copies(MediumId={keyMediumId},InventoryNumber={keyInventoryNumber})")]
    public IActionResult Delete([FromRoute] Guid keyMediumId, [FromRoute] int keyInventoryNumber)
    {
        if (Find(db, keyMediumId, keyInventoryNumber) is not { } existing)
        {
            return NotFound();
        }

        if (CheckConcurrency(Request, existing) is { } precondition)
        {
            return precondition;
        }

        db.Copies.Remove(existing);
        db.SaveChanges();
        return NoContent();
    }

    private async Task<Copy?> ReadCopyFromBody()
    {
        Request.Body.Position = 0;
        using var document = await System.Text.Json.JsonDocument.ParseAsync(Request.Body);
        var root = document.RootElement;

        var copy = new Copy();

        // The medium may arrive as a plain foreign key or through either binding notation.
        copy.MediumId = NavigationBinding.Read(Request, nameof(Copy.Medium), NavigationBinding.AsGuid)
            ?? (root.TryGetProperty(nameof(Copy.MediumId), out var fk) && fk.ValueKind == System.Text.Json.JsonValueKind.String
                && Guid.TryParse(fk.GetString(), out var parsed) ? parsed : Guid.Empty);

        if (copy.MediumId == Guid.Empty)
        {
            return null;
        }

        if (root.TryGetProperty(nameof(Copy.InventoryNumber), out var inventory))
        {
            copy.InventoryNumber = inventory.GetInt32();
        }

        if (root.TryGetProperty(nameof(Copy.Condition), out var condition))
        {
            copy.Condition = condition.GetByte();
        }

        if (root.TryGetProperty(nameof(Copy.IsLoanable), out var loanable))
        {
            copy.IsLoanable = loanable.GetBoolean();
        }

        if (root.TryGetProperty(nameof(Copy.Location_), out var shelf))
        {
            copy.Location_ = shelf.GetString();
        }

        // Read by hand like everything else here, and easy to forget: these three were silently dropped on
        // create - stored as their defaults - while `PATCH` kept them, since that one goes through Delta<T>.
        if (root.TryGetProperty(nameof(Copy.WeightKg), out var weight))
        {
            copy.WeightKg = weight.GetSingle();
        }

        if (root.TryGetProperty(nameof(Copy.Status), out var status)
            && Enum.TryParse<AvailabilityStatus>(status.GetString(), out var parsedStatus))
        {
            copy.Status = parsedStatus;
        }

        if (root.TryGetProperty(nameof(Copy.AcquisitionDate), out var acquired)
            && DateOnly.TryParse(acquired.GetString(), out var parsedDate))
        {
            copy.AcquisitionDate = parsedDate;
        }

        if (NavigationBinding.Read(Request, nameof(Copy.Location), NavigationBinding.AsInt) is { } branchId)
        {
            copy.Location = db.Branches.FirstOrDefault(b => b.Id == branchId);
        }

        return copy;
    }

    /// <summary>
    /// Loads one copy by its composite key. The caller says which navigation properties it needs: none are
    /// populated by default, so reaching through one that was not included silently looks like a null link
    /// rather than an unloaded one.
    /// </summary>
    internal static Copy? Find(
        LibraryContext db,
        Guid mediumId,
        int inventoryNumber,
        Func<IQueryable<Copy>, IQueryable<Copy>>? include = null)
    {
        var copies = include is null ? db.Copies : include(db.Copies);
        return copies.FirstOrDefault(c => c.MediumId == mediumId && c.InventoryNumber == inventoryNumber);
    }
}

public class MembersController(LibraryContext db) : ODataController
{
    [EnableQuery(MaxExpansionDepth = 4)]
    public IQueryable<Member> Get() => db.Members.AsNoTracking();

    [EnableQuery]
    public SingleResult<Member> Get([FromRoute] int key) =>
        SingleResult.Create(db.Members.AsNoTracking().Where(m => m.Id == key));

    /// <summary>
    /// The member's loans. Queried straight off the loans set rather than through the member's navigation
    /// property, so that <c>$filter</c> and <c>$orderby</c> on this collection still reach the database
    /// instead of being applied to an already-materialised list.
    /// </summary>
    [EnableQuery]
    public IQueryable<Loan> GetLoans([FromRoute] int key) =>
        db.Loans.AsNoTracking().Where(l => l.Member != null && l.Member.Id == key);

    [EnableQuery]
    public IQueryable<Reservation> GetReservations([FromRoute] int key) =>
        (db.Members.AsNoTracking().Include(m => m.Reservations).FirstOrDefault(m => m.Id == key)?.Reservations ?? [])
            .AsQueryable();

    [EnableQuery]
    public ActionResult<IdDocument> GetIdDocument([FromRoute] int key) =>
        db.Members.AsNoTracking().Include(m => m.IdDocument).FirstOrDefault(m => m.Id == key)?.IdDocument is { } document
            ? document
            : NotFound();

    public IActionResult Post([FromBody] Member member)
    {
        member.IgnoreManagedOnInsert(HttpContext.ODataFeature().Model);

        // Same as on Media: an id document or a nested loan's copy may be bound rather than nested, and
        // that is only decidable from the body - see NavigationBinding.Resolve.
        if (!NavigationBinding.Resolve(db, Request, member))
        {
            return BadRequest("A navigation binding in the request body names an entity that does not exist.");
        }

        member.Id = NextMemberId();
        db.Members.Add(member);
        RegisterNested(member);
        db.SaveChanges();
        return Created(member);
    }

    /// <summary>
    /// Bound navigation: a new loan for the member the route names. Where a resolved $batch URL
    /// reference lands - <c>POST $1/Loans</c> in a batch rewrites to this route once request 1 has
    /// created the member. The action name is the convention's for a write on a bound navigation,
    /// <c>PostTo{NavigationPropertyName}</c>, not <c>Post{NavigationPropertyName}</c>.
    /// </summary>
    public IActionResult PostToLoans([FromRoute] int key, [FromBody] Loan loan)
    {
        if (db.Members.FirstOrDefault(m => m.Id == key) is not { } member)
        {
            return NotFound();
        }

        return LoanPost.Create(db, HttpContext, loan, member) is { } created
            ? Created(created)
            : BadRequest("A navigation binding in the request body names an entity that does not exist.");
    }

    /// <summary>
    /// Delta payload on the collection (OData 4.01): a mixed batch of upserts and removals in one
    /// request. Entries carrying <c>@removed</c> arrive as <see cref="DeltaDeletedResource{T}" />.
    /// </summary>
    [HttpPatch("odata/v4/library/Members")]
    public IActionResult PatchCollection([FromBody] DeltaSet<Member>? deltaSet)
    {
        // A delta set the deserializer could not build - an empty body is one such case - arrives null, as a
        // single delta arrives null on an abstract-typed set. Dereferencing it used to answer 500 to what
        // is a malformed request.
        if (deltaSet is null)
        {
            return BadRequest("The request body could not be read as a delta set of type Member.");
        }

        foreach (var item in deltaSet)
        {
            switch (item)
            {
                case DeltaDeletedResource<Member> removed:
                    if (KeyOf(removed) is { } removedId
                        && db.Members.FirstOrDefault(m => m.Id == removedId) is { } toRemove)
                    {
                        db.Members.Remove(toRemove);
                    }

                    break;

                case Delta<Member> delta:
                    var id = KeyOf(delta);
                    if (id is not null && db.Members.FirstOrDefault(m => m.Id == id) is { } existing)
                    {
                        delta.Patch(existing);
                    }
                    else
                    {
                        // Upsert: an entry whose key is unknown creates the entity.
                        var created = delta.GetInstance();
                        created.Id = id ?? NextMemberId();
                        db.Members.Add(created);
                        RegisterNested(created);
                    }

                    break;
            }
        }

        db.SaveChanges();
        return Ok(deltaSet);
    }

    /// <summary>
    /// The next member id, assigned here rather than by the database. A test server whose keys depend on
    /// insert order is one consumers cannot assert against, so <c>Member.Id</c> stays caller-assigned.
    /// </summary>
    private int NextMemberId() => db.Members.Any() ? db.Members.Max(m => m.Id) + 1 : 1;

    private static int? KeyOf(IDeltaSetItem item) =>
        item is Delta<Member> delta && delta.TryGetPropertyValue(nameof(Member.Id), out var value)
            ? Convert.ToInt32(value)
            : null;

    /// <summary>
    /// Gives the entities that arrived nested inside the payload (deep insert) their keys.
    ///
    /// Adding the member already tracks the whole graph, so nothing has to be inserted into the other sets
    /// by hand any more - that is what a change tracker is for. What is still this method's job is the
    /// keys: they are caller-assigned throughout this service, so an entity that arrived without one would
    /// otherwise be stored under <c>Guid.Empty</c> and collide with the next such entity.
    /// </summary>
    private static void RegisterNested(Member member)
    {
        foreach (var loan in member.Loans.Where(l => l.Id == Guid.Empty))
        {
            loan.Id = Guid.NewGuid();
            loan.Member = member;
        }

        foreach (var reservation in member.Reservations.Where(r => r.Id == Guid.Empty))
        {
            reservation.Id = Guid.NewGuid();
        }

        if (member.IdDocument is { Id: var documentId } document && documentId == Guid.Empty)
        {
            document.Id = Guid.NewGuid();
        }
    }

    public IActionResult Patch([FromRoute] int key, Delta<Member>? delta)
    {
        var existing = db.Members.Include(m => m.IdDocument).FirstOrDefault(m => m.Id == key);
        if (existing is null)
        {
            return NotFound();
        }

        var boundDocumentId = NavigationBinding.Read(Request, nameof(Member.IdDocument), NavigationBinding.AsGuid);
        var clearsDocument = NavigationBinding.ClearsLink(Request, nameof(Member.IdDocument));

        // Same as on Copy: a null delta is only usable because a binding came out of the raw body.
        if (delta is null && boundDocumentId is null && !clearsDocument)
        {
            return BadRequest("The request body could not be read as a Member.");
        }

        var currentDocument = existing.IdDocument;
        existing.IdDocument = null;
        delta?.Patch(existing);

        existing.IdDocument = boundDocumentId is { } documentId
            ? db.IdDocuments.FirstOrDefault(d => d.Id == documentId)
            : clearsDocument ? null : currentDocument;

        db.SaveChanges();
        return Updated(existing);
    }

    /// <summary>
    /// Replaces the member's own state.
    ///
    /// This used to remove the entity and add the incoming one under the same key. A change tracker will
    /// not have that - the two are one row - and going through with it would have meant a cascading delete
    /// of the member's loans on the way. Overwriting the scalar and complex properties in place is both
    /// what EF permits and what the spec asks for: <c>PUT</c> replaces the entity, it does not touch its
    /// relationships.
    /// </summary>
    public IActionResult Put([FromRoute] int key, [FromBody] Member member)
    {
        var existing = db.Members.FirstOrDefault(m => m.Id == key);
        if (existing is null)
        {
            return NotFound();
        }

        member.Id = key;
        // A PUT replaces the entity, but not the properties the client may not change: the spec exempts
        // them from the reset an omission otherwise causes, so they keep the value that is stored.
        member.IgnoreManagedOnUpdate(existing, HttpContext.ODataFeature().Model);
        db.Entry(existing).CurrentValues.SetValues(member);
        existing.Address = member.Address;
        existing.PreviousAddresses = member.PreviousAddresses;

        db.SaveChanges();
        return Updated(existing);
    }

    /// <summary>
    /// Deletes a member. The loans go too - the reference model declares the cascade, and here the
    /// database is the one that carries it out.
    /// </summary>
    public IActionResult Delete([FromRoute] int key)
    {
        var existing = db.Members.FirstOrDefault(m => m.Id == key);
        if (existing is null)
        {
            return NotFound();
        }

        db.Members.Remove(existing);
        db.SaveChanges();
        return NoContent();
    }
}

/// <summary>
/// The write path for a new loan, shared by the collection <c>POST</c> and the bound navigation one on
/// <see cref="MembersController" />, where the member comes from the route instead of the payload and
/// wins over any binding the body brought.
///
/// The computed key is the server's on insert as much as on update, so a value the client sent goes no
/// further than here. The member and the copy are existing entities, so a payload for <c>Loans</c>
/// binds them in the 4.0 or the 4.01 notation rather than nesting them - there is no other way the
/// references get filled.
/// </summary>
internal static class LoanPost
{
    /// <summary>Persists the loan and returns it, or <c>null</c> if a binding names an entity that does not exist.</summary>
    public static Loan? Create(LibraryContext db, HttpContext context, Loan loan, Member? member = null)
    {
        loan.IgnoreManagedOnInsert(context.ODataFeature().Model);

        if (loan.Id == Guid.Empty)
        {
            loan.Id = Guid.NewGuid();
        }

        if (member is { })
        {
            loan.Member = member;
        }

        if (!NavigationBinding.Resolve(db, context.Request, loan))
        {
            return null;
        }

        db.Loans.Add(loan);
        db.SaveChanges();
        return loan;
    }
}

public class LoansController(LibraryContext db) : ODataController
{
    /// <summary>Creates a loan whose member and copy the payload binds to existing entities.</summary>
    public IActionResult Post([FromBody] Loan loan)
    {
        return LoanPost.Create(db, HttpContext, loan) is { } created
            ? Created(created)
            : BadRequest("A navigation binding in the request body names an entity that does not exist.");
    }

    [EnableQuery]
    public IQueryable<Loan> Get() => db.Loans.AsNoTracking();

    [EnableQuery]
    public SingleResult<Loan> Get([FromRoute] Guid key) =>
        SingleResult.Create(db.Loans.AsNoTracking().Where(l => l.Id == key));

    [EnableQuery]
    public ActionResult<Member> GetMember([FromRoute] Guid key) =>
        db.Loans.Include(l => l.Member).FirstOrDefault(l => l.Id == key)?.Member is { } member
            ? member
            : NotFound();

    [EnableQuery]
    public ActionResult<Copy> GetCopy([FromRoute] Guid key) =>
        db.Loans.Include(l => l.Copy).FirstOrDefault(l => l.Id == key)?.Copy is { } copy ? copy : NotFound();

    /// <summary>
    /// Exists so that <c>Core.Immutable</c> on <see cref="Loan.LoanedAt" /> is observable at all: the
    /// term only says anything about an update, and without this the set was read-only. Nothing here
    /// treats the annotated property specially - see FEATURE-COVERAGE.md on what Delta&lt;T&gt; does
    /// with it.
    /// </summary>
    public IActionResult Patch([FromRoute] Guid key, Delta<Loan>? delta)
    {
        var existing = db.Loans.FirstOrDefault(l => l.Id == key);
        if (existing is null)
        {
            return NotFound();
        }

        // Nothing here reads a navigation binding out of the raw body, so unlike on Copy and Member a
        // null delta leaves nothing to apply. It used to be skipped silently and answered 204, which told
        // the caller its update had been stored.
        if (delta is null)
        {
            return BadRequest("The request body could not be read as a Loan.");
        }

        delta.Patch(existing);
        db.SaveChanges();
        return Updated(existing);
    }
}

public class ReservationsController(LibraryContext db) : ODataController
{
    [EnableQuery]
    public IQueryable<Reservation> Get() => db.Reservations.AsNoTracking();

    [EnableQuery]
    public SingleResult<Reservation> Get([FromRoute] Guid key) =>
        SingleResult.Create(db.Reservations.AsNoTracking().Where(r => r.Id == key));
}

public class IdDocumentsController(LibraryContext db) : ODataController
{
    [EnableQuery]
    public IQueryable<IdDocument> Get() => db.IdDocuments.AsNoTracking();

    [EnableQuery]
    public SingleResult<IdDocument> Get([FromRoute] Guid key) =>
        SingleResult.Create(db.IdDocuments.AsNoTracking().Where(d => d.Id == key));
}

public class BranchesController(LibraryContext db) : ODataController
{
    [EnableQuery]
    public IQueryable<Branch> Get() => db.Branches.AsNoTracking();

    [EnableQuery]
    public SingleResult<Branch> Get([FromRoute] int key) =>
        SingleResult.Create(db.Branches.AsNoTracking().Where(b => b.Id == key));

    /// <summary>
    /// The one create in this service where the *client* supplies the key. A branch code is allocated by
    /// the organisation, so `Branch.Id` carries no managed annotation and arrives in the payload - unlike
    /// every other key here, which is generated and annotated `Core.Computed`.
    ///
    /// Which makes this the counter-example the reference model asks for: a generated client can demand
    /// the key on create for this entity and leave it out for all the others, and only a request that
    /// actually stores what was sent proves the distinction is real.
    /// </summary>
    public IActionResult Post([FromBody] Branch branch)
    {
        if (branch.Id == 0)
        {
            return BadRequest("Branch.Id is assigned by the client and must be supplied.");
        }

        if (db.Branches.Any(b => b.Id == branch.Id))
        {
            return Conflict($"A branch with id {branch.Id} already exists.");
        }

        db.Branches.Add(branch);
        db.SaveChanges();
        return Created(branch);
    }

    /// <summary>
    /// The counterpart of the create above, and not optional: a set a client can add to but never remove
    /// from leaves every consumer's store dirty for the rest of its run. The integration tests share one
    /// container across a package, so a branch created by one test was still there for the next, which is
    /// how this gap announced itself.
    /// </summary>
    public IActionResult Delete([FromRoute] int key)
    {
        var existing = db.Branches.FirstOrDefault(b => b.Id == key);
        if (existing is null)
        {
            return NotFound();
        }

        db.Branches.Remove(existing);
        db.SaveChanges();
        return NoContent();
    }
}

public class BookmobilesController(LibraryContext db) : ODataController
{
    [EnableQuery]
    public IQueryable<Bookmobile> Get() => db.Bookmobiles.AsNoTracking();

    [EnableQuery]
    public SingleResult<Bookmobile> Get([FromRoute] int key) =>
        SingleResult.Create(db.Bookmobiles.AsNoTracking().Where(b => b.Id == key));
}

public class PublishersController(LibraryContext db) : ODataController
{
    [EnableQuery]
    public IQueryable<PublisherRegistry.Publisher> Get() => db.Publishers.AsNoTracking();

    [EnableQuery]
    public SingleResult<PublisherRegistry.Publisher> Get([FromRoute] int key) =>
        SingleResult.Create(db.Publishers.AsNoTracking().Where(p => p.Id == key));

    /// <summary>
    /// Straight off the media set rather than through the publisher's navigation property, so the query
    /// options on this collection are still translated to SQL.
    /// </summary>
    [EnableQuery]
    public IQueryable<Book> GetBooks([FromRoute] int key) =>
        db.Media.AsNoTracking().OfType<Book>().Where(b => b.Publisher != null && b.Publisher.Id == key);

    /// <summary>
    /// A single book reached through its publisher - the illustrative case where the nav property's own
    /// name ("Books") diverges from its target's entity set ("Media"). Routed explicitly: the convention
    /// only matches the collection form above, never a further key segment.
    /// </summary>
    [HttpGet("odata/v4/library/Publishers({key})/Books({bookId})")]
    [EnableQuery]
    public ActionResult<Book> GetBook([FromRoute] int key, [FromRoute] Guid bookId) =>
        db.Media.AsNoTracking().OfType<Book>().FirstOrDefault(b => b.Id == bookId && b.Publisher != null && b.Publisher.Id == key) is
        { } book
            ? book
            : NotFound();

    /// <summary>
    /// Patches a book reached through its publisher - the same resource a direct <c>/Media(id)</c> patch
    /// addresses (see <see cref="MediaController.Patch"/>), just narrowed to the concrete, non-abstract
    /// <see cref="Book"/> type already, so the payload needs no <c>@odata.type</c> discriminator here.
    /// Book carries no <c>Core.OptimisticConcurrency</c> promise, so - like the direct route - there is no
    /// concurrency check to share.
    /// </summary>
    [HttpPatch("odata/v4/library/Publishers({key})/Books({bookId})")]
    public IActionResult PatchBook([FromRoute] int key, [FromRoute] Guid bookId, Delta<Book>? delta)
    {
        var existing = db.Media.OfType<Book>().Include(b => b.Publisher).FirstOrDefault(b => b.Id == bookId && b.Publisher != null && b.Publisher.Id == key);
        if (existing is null)
        {
            return NotFound();
        }

        if (delta is null)
        {
            return BadRequest("The request body could not be read as a Book.");
        }

        delta.Patch(existing);
        db.SaveChanges();
        return Updated(existing);
    }

    /// <summary>
    /// A single copy reached three hops deep - publisher, then its book (<c>Books</c> diverging from its
    /// entity set <c>Media</c>, same as <see cref="GetBook"/>), then the copy itself. Exercises the
    /// statically-keyed-hop rename's own deferred follow-up (odata2ts#558): a write here must
    /// deterministically invalidate not just the copy's direct route, but the *ancestor* book's direct
    /// <c>Media(id)</c> route too - the book's own hop diverges from its entity set exactly as the
    /// addressed resource does in <see cref="GetBook"/>/<see cref="PatchBook"/>, just one level further up.
    /// </summary>
    [HttpGet("odata/v4/library/Publishers({key})/Books({bookId})/Copies(MediumId={copyMediumId},InventoryNumber={copyInventoryNumber})")]
    [EnableQuery]
    public SingleResult<Copy> GetBookCopy(
        [FromRoute] int key,
        [FromRoute] Guid bookId,
        [FromRoute] Guid copyMediumId,
        [FromRoute] int copyInventoryNumber) =>
        SingleResult.Create(
            db.Copies.AsNoTracking()
                .Where(c => c.MediumId == bookId
                    && c.MediumId == copyMediumId
                    && c.InventoryNumber == copyInventoryNumber
                    && db.Media.OfType<Book>().Any(b => b.Id == bookId && b.Publisher != null && b.Publisher.Id == key)));

    /// <summary>
    /// Patches a copy reached three hops deep - the same resource <see cref="CopiesController.Patch"/> and
    /// <see cref="MediaController.PatchCopy"/> address by shorter routes, so it shares their concurrency
    /// check and patch application rather than risking a third implementation drifting apart.
    /// </summary>
    [HttpPatch("odata/v4/library/Publishers({key})/Books({bookId})/Copies(MediumId={copyMediumId},InventoryNumber={copyInventoryNumber})")]
    public IActionResult PatchBookCopy(
        [FromRoute] int key,
        [FromRoute] Guid bookId,
        [FromRoute] Guid copyMediumId,
        [FromRoute] int copyInventoryNumber,
        Delta<Copy>? delta)
    {
        if (bookId != copyMediumId || !db.Media.OfType<Book>().Any(b => b.Id == bookId && b.Publisher != null && b.Publisher.Id == key))
        {
            return NotFound();
        }

        var existing = CopiesController.Find(db, copyMediumId, copyInventoryNumber, q => q.Include(c => c.Location));
        if (existing is null)
        {
            return NotFound();
        }

        if (CopiesController.CheckConcurrency(Request, existing) is { } precondition)
        {
            return precondition;
        }

        return CopiesController.ApplyPatch(db, Request, existing, delta) is { } badRequest ? badRequest : Updated(existing);
    }

    /// <summary>
    /// Creates a publisher. The key is the next free number, the same fixed sequence a consumer can
    /// assert against as on <c>Members</c>; a value the client sends is discarded.
    /// </summary>
    public IActionResult Post([FromBody] PublisherRegistry.Publisher publisher)
    {
        publisher.Id = NextPublisherId();
        db.Publishers.Add(publisher);
        db.SaveChanges();
        return Created(publisher);
    }

    private int NextPublisherId() => db.Publishers.Any() ? db.Publishers.Max(p => p.Id) + 1 : 1;
}

public class PublisherBranchesController(LibraryContext db) : ODataController
{
    [EnableQuery]
    public IQueryable<PublisherRegistry.Branch> Get() => db.PublisherBranches.AsNoTracking();

    [EnableQuery]
    public SingleResult<PublisherRegistry.Branch> Get([FromRoute] int key) =>
        SingleResult.Create(db.PublisherBranches.AsNoTracking().Where(b => b.Id == key));
}

/// <summary>The <c>MainBranch</c> singleton.</summary>
public class MainBranchController(LibraryContext db) : ODataController
{
    [EnableQuery]
    public ActionResult<Branch> Get() => db.MainBranch;

    public IActionResult Patch(Delta<Branch>? delta)
    {
        if (delta is null)
        {
            return BadRequest("The request body could not be read as a Branch.");
        }

        var branch = db.MainBranch;
        delta.Patch(branch);
        db.SaveChanges();
        return Updated(branch);
    }
}
