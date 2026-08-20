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
    public IQueryable<EBook> GetFromEBook() => db.Media.AsNoTracking().OfType<EBook>();

    [EnableQuery]
    public IQueryable<Audiobook> GetFromAudiobook() => db.Media.AsNoTracking().OfType<Audiobook>();

    [EnableQuery]
    public IQueryable<CollectorsItem> GetFromCollectorsItem() => db.Media.AsNoTracking().OfType<CollectorsItem>();

    [EnableQuery]
    public IQueryable<Copy> GetCopies([FromRoute] Guid key) =>
        db.Copies.AsNoTracking().Where(c => c.MediumId == key);

    [EnableQuery]
    public ActionResult<PublisherRegistry.Publisher> GetPublisherFromBook([FromRoute] Guid key) =>
        db.Media.OfType<Book>().Include(b => b.Publisher).FirstOrDefault(b => b.Id == key)?.Publisher is { } publisher
            ? publisher
            : NotFound();

    /// <summary>Contained entities, reachable only through their audiobook.</summary>
    [EnableQuery]
    public IQueryable<AudiobookChapter> GetChaptersFromAudiobook([FromRoute] Guid key) =>
        (db.Media.OfType<Audiobook>()
            .Include(a => a.Chapters)
            .FirstOrDefault(a => a.Id == key)?.Chapters ?? []).AsQueryable();

    public IActionResult Post([FromBody] Medium medium)
    {
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
            return BadRequest("A navigation binding in the request body names an entity that does not exist.");
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

        db.SaveChanges();
        return Created(medium);
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

        var boundBranchId = NavigationBinding.Read(Request, nameof(Copy.Location), NavigationBinding.AsInt);
        var clearsBranch = NavigationBinding.ClearsLink(Request, nameof(Copy.Location));

        // A body the deserializer refused - binding a navigation to null is one such case - arrives as a
        // null delta. That is only recoverable because the binding was read from the raw body: with no
        // binding either, nothing in the request can be applied, and saying so beats reporting the 204
        // that a silently skipped patch would have produced.
        if (delta is null && boundBranchId is null && !clearsBranch)
        {
            return BadRequest("The request body could not be read as a Copy.");
        }

        // Keep the current link out of Patch's reach: a bound stub would be written into it.
        var currentLocation = existing.Location;
        existing.Location = null;

        delta?.Patch(existing);

        existing.Location = boundBranchId is { } branchId
            ? db.Branches.FirstOrDefault(b => b.Id == branchId)
            : clearsBranch ? null : currentLocation;

        db.SaveChanges();
        return Updated(existing);
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

    /// <summary>Deletes a copy. Routed explicitly for the same reason as the other composite-key routes.</summary>
    [HttpDelete("odata/v4/library/Copies(MediumId={keyMediumId},InventoryNumber={keyInventoryNumber})")]
    public IActionResult Delete([FromRoute] Guid keyMediumId, [FromRoute] int keyInventoryNumber)
    {
        if (Find(db, keyMediumId, keyInventoryNumber) is not { } existing)
        {
            return NotFound();
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
    /// Delta payload on the collection (OData 4.01): a mixed batch of upserts and removals in one
    /// request. Entries carrying <c>@removed</c> arrive as <see cref="DeltaDeletedResource{T}" />.
    /// </summary>
    [HttpPatch("odata/v4/library/Members")]
    public IActionResult PatchCollection([FromBody] DeltaSet<Member> deltaSet)
    {
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

public class LoansController(LibraryContext db) : ODataController
{
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
