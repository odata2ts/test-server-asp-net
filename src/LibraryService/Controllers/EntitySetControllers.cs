using Library.Catalog;
using Library.Circulation;
using LibraryService.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Deltas;
using Microsoft.AspNetCore.OData.Formatter;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Results;
using Microsoft.AspNetCore.OData.Routing.Controllers;

namespace LibraryService.Controllers;

/// <summary>
/// The plain entity sets. `[EnableQuery]` hands `$select`, `$filter`, `$orderby`, `$top`, `$skip`,
/// `$count` and `$expand` to the OData layer, which applies them to the returned <see cref="IQueryable{T}" />.
/// </summary>
public class MediaController(LibraryData data) : ODataController
{
    [EnableQuery(MaxExpansionDepth = 4)]
    public IQueryable<Medium> Get() => data.Media.AsQueryable();

    [EnableQuery]
    public SingleResult<Medium> Get([FromRoute] Guid key) =>
        SingleResult.Create(data.Media.Where(m => m.Id == key).AsQueryable());

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
        return SingleResult.Create(data.Media.OfType<PrintMedium>().Where(m => m.ISBN == value).AsQueryable());
    }

    /// <summary>Type-cast segment, e.g. <c>/Media/Library.Catalog.Book</c>.</summary>
    [EnableQuery]
    public IQueryable<Book> GetFromBook() => data.Media.OfType<Book>().AsQueryable();

    [EnableQuery]
    public IQueryable<EBook> GetFromEBook() => data.Media.OfType<EBook>().AsQueryable();

    [EnableQuery]
    public IQueryable<Audiobook> GetFromAudiobook() => data.Media.OfType<Audiobook>().AsQueryable();

    [EnableQuery]
    public IQueryable<CollectorsItem> GetFromCollectorsItem() => data.Media.OfType<CollectorsItem>().AsQueryable();

    [EnableQuery]
    public IQueryable<Copy> GetCopies([FromRoute] Guid key) =>
        data.Copies.Where(c => c.MediumId == key).AsQueryable();

    [EnableQuery]
    public ActionResult<PublisherRegistry.Publisher> GetPublisherFromBook([FromRoute] Guid key) =>
        data.Media.OfType<Book>().FirstOrDefault(b => b.Id == key)?.Publisher is { } publisher
            ? publisher
            : NotFound();

    /// <summary>Contained entities, reachable only through their audiobook.</summary>
    [EnableQuery]
    public IQueryable<AudiobookChapter> GetChaptersFromAudiobook([FromRoute] Guid key) =>
        (data.Media.OfType<Audiobook>().FirstOrDefault(a => a.Id == key)?.Chapters ?? []).AsQueryable();

    public IActionResult Post([FromBody] Medium medium)
    {
        if (medium.Id == Guid.Empty)
        {
            medium.Id = Guid.NewGuid();
        }

        data.Media.Add(medium);

        // Deep insert: copies that arrived nested must also become addressable as /Copies.
        foreach (var copy in medium.Copies.Where(c => !data.Copies.Contains(c)))
        {
            copy.MediumId = medium.Id;
            copy.Medium = medium;
            data.Copies.Add(copy);
        }

        return Created(medium);
    }

    public IActionResult Patch([FromRoute] Guid key, Delta<Medium> delta)
    {
        var existing = data.Media.FirstOrDefault(m => m.Id == key);
        if (existing is null)
        {
            return NotFound();
        }

        delta.Patch(existing);
        return Updated(existing);
    }

    public IActionResult Delete([FromRoute] Guid key)
    {
        var existing = data.Media.FirstOrDefault(m => m.Id == key);
        if (existing is null)
        {
            return NotFound();
        }

        data.Media.Remove(existing);
        return NoContent();
    }
}

public class CopiesController(LibraryData data) : ODataController
{
    [EnableQuery]
    public IQueryable<Copy> Get() => data.Copies.AsQueryable();

    /// <summary>
    /// Composite key. Routed explicitly: the convention builds `keyMediumId` / `keyInventoryNumber`
    /// route values, but does not match the two-part key template on its own.
    /// </summary>
    [HttpGet("odata/v4/library/Copies(MediumId={keyMediumId},InventoryNumber={keyInventoryNumber})")]
    [EnableQuery]
    public SingleResult<Copy> Get([FromRoute] Guid keyMediumId, [FromRoute] int keyInventoryNumber) =>
        SingleResult.Create(
            data.Copies.Where(c => c.MediumId == keyMediumId && c.InventoryNumber == keyInventoryNumber).AsQueryable());

    [HttpGet("odata/v4/library/Copies(MediumId={keyMediumId},InventoryNumber={keyInventoryNumber})/Medium")]
    [EnableQuery]
    public ActionResult<Medium> GetMedium([FromRoute] Guid keyMediumId, [FromRoute] int keyInventoryNumber) =>
        Find(data, keyMediumId, keyInventoryNumber)?.Medium is { } medium ? medium : NotFound();

    [HttpPatch("odata/v4/library/Copies(MediumId={keyMediumId},InventoryNumber={keyInventoryNumber})")]
    public IActionResult Patch([FromRoute] Guid keyMediumId, [FromRoute] int keyInventoryNumber, Delta<Copy> delta)
    {
        var existing = Find(data, keyMediumId, keyInventoryNumber);
        if (existing is null)
        {
            return NotFound();
        }

        var boundBranchId = NavigationBinding.Read(Request, nameof(Copy.Location), NavigationBinding.AsInt);
        var clearsBranch = NavigationBinding.ClearsLink(Request, nameof(Copy.Location));

        // Keep the current link out of Patch's reach: a bound stub would be written into it.
        var currentLocation = existing.Location;
        existing.Location = null;

        // A body the deserializer refused - binding a navigation to null is one such case - arrives as a
        // null delta. The binding itself was read from the raw body, so it can still be applied.
        delta?.Patch(existing);

        existing.Location = boundBranchId is { } branchId
            ? data.Branches.FirstOrDefault(b => b.Id == branchId)
            : clearsBranch ? null : currentLocation;

        return Updated(existing);
    }

    /// <summary>Navigation to the branch the copy is shelved at.</summary>
    [HttpGet("odata/v4/library/Copies(MediumId={keyMediumId},InventoryNumber={keyInventoryNumber})/Location")]
    [EnableQuery]
    public ActionResult<Branch> GetLocation([FromRoute] Guid keyMediumId, [FromRoute] int keyInventoryNumber) =>
        Find(data, keyMediumId, keyInventoryNumber)?.Location is { } branch ? branch : NotFound();

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

        if (data.Media.FirstOrDefault(m => m.Id == copy.MediumId) is not { } medium)
        {
            return BadRequest("The referenced medium does not exist.");
        }

        // A second copy with the same composite key used to be accepted, after which a keyed read failed
        // with "SingleResult must have zero or one elements" - a store that cannot be read from any more.
        if (Find(data, copy.MediumId, copy.InventoryNumber) is not null)
        {
            return Conflict($"A copy with inventory number {copy.InventoryNumber} exists for this medium.");
        }

        copy.Medium = medium;
        medium.Copies.Add(copy);
        data.Copies.Add(copy);
        return Created(copy);
    }

    /// <summary>Deletes a copy. Routed explicitly for the same reason as the other composite-key routes.</summary>
    [HttpDelete("odata/v4/library/Copies(MediumId={keyMediumId},InventoryNumber={keyInventoryNumber})")]
    public IActionResult Delete([FromRoute] Guid keyMediumId, [FromRoute] int keyInventoryNumber)
    {
        if (Find(data, keyMediumId, keyInventoryNumber) is not { } existing)
        {
            return NotFound();
        }

        existing.Medium?.Copies.Remove(existing);
        data.Copies.Remove(existing);
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
            copy.Location = data.Branches.FirstOrDefault(b => b.Id == branchId);
        }

        return copy;
    }

    internal static Copy? Find(LibraryData data, Guid mediumId, int inventoryNumber) =>
        data.Copies.FirstOrDefault(c => c.MediumId == mediumId && c.InventoryNumber == inventoryNumber);
}

public class MembersController(LibraryData data) : ODataController
{
    [EnableQuery(MaxExpansionDepth = 4)]
    public IQueryable<Member> Get() => data.Members.AsQueryable();

    [EnableQuery]
    public SingleResult<Member> Get([FromRoute] int key) =>
        SingleResult.Create(data.Members.Where(m => m.Id == key).AsQueryable());

    [EnableQuery]
    public IQueryable<Loan> GetLoans([FromRoute] int key) =>
        (data.Members.FirstOrDefault(m => m.Id == key)?.Loans ?? []).AsQueryable();

    [EnableQuery]
    public IQueryable<Reservation> GetReservations([FromRoute] int key) =>
        (data.Members.FirstOrDefault(m => m.Id == key)?.Reservations ?? []).AsQueryable();

    [EnableQuery]
    public ActionResult<IdDocument> GetIdDocument([FromRoute] int key) =>
        data.Members.FirstOrDefault(m => m.Id == key)?.IdDocument is { } document ? document : NotFound();

    public IActionResult Post([FromBody] Member member)
    {
        member.Id = data.Members.Count == 0 ? 1 : data.Members.Max(m => m.Id) + 1;
        data.Members.Add(member);
        RegisterNested(member);
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
                        && data.Members.FirstOrDefault(m => m.Id == removedId) is { } toRemove)
                    {
                        data.Members.Remove(toRemove);
                    }

                    break;

                case Delta<Member> delta:
                    var id = KeyOf(delta);
                    if (id is not null && data.Members.FirstOrDefault(m => m.Id == id) is { } existing)
                    {
                        delta.Patch(existing);
                    }
                    else
                    {
                        // Upsert: an entry whose key is unknown creates the entity.
                        var created = delta.GetInstance();
                        created.Id = id ?? (data.Members.Count == 0 ? 1 : data.Members.Max(m => m.Id) + 1);
                        data.Members.Add(created);
                        RegisterNested(created);
                    }

                    break;
            }
        }

        return Ok(deltaSet);
    }

    private static int? KeyOf(IDeltaSetItem item) =>
        item is Delta<Member> delta && delta.TryGetPropertyValue(nameof(Member.Id), out var value)
            ? Convert.ToInt32(value)
            : null;

    /// <summary>
    /// Registers the entities that arrived nested inside the payload (deep insert) in their own sets.
    /// Without this they exist only inside the parent's navigation property: reachable through it, but
    /// keyless and absent from <c>/Loans</c> - an inconsistent state rather than a partial one.
    /// </summary>
    private void RegisterNested(Member member)
    {
        foreach (var loan in member.Loans.Where(l => !data.Loans.Contains(l)))
        {
            if (loan.Id == Guid.Empty)
            {
                loan.Id = Guid.NewGuid();
            }

            loan.Member = member;
            data.Loans.Add(loan);
        }

        foreach (var reservation in member.Reservations.Where(r => !data.Reservations.Contains(r)))
        {
            if (reservation.Id == Guid.Empty)
            {
                reservation.Id = Guid.NewGuid();
            }

            data.Reservations.Add(reservation);
        }

        if (member.IdDocument is { } document && !data.IdDocuments.Contains(document))
        {
            if (document.Id == Guid.Empty)
            {
                document.Id = Guid.NewGuid();
            }

            data.IdDocuments.Add(document);
        }
    }

    public IActionResult Patch([FromRoute] int key, Delta<Member> delta)
    {
        var existing = data.Members.FirstOrDefault(m => m.Id == key);
        if (existing is null)
        {
            return NotFound();
        }

        var boundDocumentId = NavigationBinding.Read(Request, nameof(Member.IdDocument), NavigationBinding.AsGuid);
        var clearsDocument = NavigationBinding.ClearsLink(Request, nameof(Member.IdDocument));

        var currentDocument = existing.IdDocument;
        existing.IdDocument = null;
        delta?.Patch(existing);

        existing.IdDocument = boundDocumentId is { } documentId
            ? data.IdDocuments.FirstOrDefault(d => d.Id == documentId)
            : clearsDocument ? null : currentDocument;

        return Updated(existing);
    }

    public IActionResult Put([FromRoute] int key, [FromBody] Member member)
    {
        var existing = data.Members.FirstOrDefault(m => m.Id == key);
        if (existing is null)
        {
            return NotFound();
        }

        data.Members.Remove(existing);
        member.Id = key;
        data.Members.Add(member);
        return Updated(member);
    }

    public IActionResult Delete([FromRoute] int key)
    {
        var existing = data.Members.FirstOrDefault(m => m.Id == key);
        if (existing is null)
        {
            return NotFound();
        }

        data.Members.Remove(existing);
        return NoContent();
    }
}

public class LoansController(LibraryData data) : ODataController
{
    [EnableQuery]
    public IQueryable<Loan> Get() => data.Loans.AsQueryable();

    [EnableQuery]
    public SingleResult<Loan> Get([FromRoute] Guid key) =>
        SingleResult.Create(data.Loans.Where(l => l.Id == key).AsQueryable());

    [EnableQuery]
    public ActionResult<Member> GetMember([FromRoute] Guid key) =>
        data.Loans.FirstOrDefault(l => l.Id == key)?.Member is { } member ? member : NotFound();

    [EnableQuery]
    public ActionResult<Copy> GetCopy([FromRoute] Guid key) =>
        data.Loans.FirstOrDefault(l => l.Id == key)?.Copy is { } copy ? copy : NotFound();

    /// <summary>
    /// Exists so that <c>Core.Immutable</c> on <see cref="Loan.LoanedAt" /> is observable at all: the
    /// term only says anything about an update, and without this the set was read-only. Nothing here
    /// treats the annotated property specially - see FEATURE-COVERAGE.md on what Delta&lt;T&gt; does
    /// with it.
    /// </summary>
    public IActionResult Patch([FromRoute] Guid key, Delta<Loan> delta)
    {
        var existing = data.Loans.FirstOrDefault(l => l.Id == key);
        if (existing is null)
        {
            return NotFound();
        }

        delta?.Patch(existing);
        return Updated(existing);
    }
}

public class ReservationsController(LibraryData data) : ODataController
{
    [EnableQuery]
    public IQueryable<Reservation> Get() => data.Reservations.AsQueryable();

    [EnableQuery]
    public SingleResult<Reservation> Get([FromRoute] Guid key) =>
        SingleResult.Create(data.Reservations.Where(r => r.Id == key).AsQueryable());
}

public class IdDocumentsController(LibraryData data) : ODataController
{
    [EnableQuery]
    public IQueryable<IdDocument> Get() => data.IdDocuments.AsQueryable();

    [EnableQuery]
    public SingleResult<IdDocument> Get([FromRoute] Guid key) =>
        SingleResult.Create(data.IdDocuments.Where(d => d.Id == key).AsQueryable());
}

public class BranchesController(LibraryData data) : ODataController
{
    [EnableQuery]
    public IQueryable<Branch> Get() => data.Branches.AsQueryable();

    [EnableQuery]
    public SingleResult<Branch> Get([FromRoute] int key) =>
        SingleResult.Create(data.Branches.Where(b => b.Id == key).AsQueryable());
}

public class BookmobilesController(LibraryData data) : ODataController
{
    [EnableQuery]
    public IQueryable<Bookmobile> Get() => data.Bookmobiles.AsQueryable();

    [EnableQuery]
    public SingleResult<Bookmobile> Get([FromRoute] int key) =>
        SingleResult.Create(data.Bookmobiles.Where(b => b.Id == key).AsQueryable());
}

public class PublishersController(LibraryData data) : ODataController
{
    [EnableQuery]
    public IQueryable<PublisherRegistry.Publisher> Get() => data.Publishers.AsQueryable();

    [EnableQuery]
    public SingleResult<PublisherRegistry.Publisher> Get([FromRoute] int key) =>
        SingleResult.Create(data.Publishers.Where(p => p.Id == key).AsQueryable());

    [EnableQuery]
    public IQueryable<Book> GetBooks([FromRoute] int key) =>
        (data.Publishers.FirstOrDefault(p => p.Id == key)?.Books ?? []).AsQueryable();
}

public class PublisherBranchesController(LibraryData data) : ODataController
{
    [EnableQuery]
    public IQueryable<PublisherRegistry.Branch> Get() => data.PublisherBranches.AsQueryable();

    [EnableQuery]
    public SingleResult<PublisherRegistry.Branch> Get([FromRoute] int key) =>
        SingleResult.Create(data.PublisherBranches.Where(b => b.Id == key).AsQueryable());
}

/// <summary>The <c>MainBranch</c> singleton.</summary>
public class MainBranchController(LibraryData data) : ODataController
{
    [EnableQuery]
    public ActionResult<Branch> Get() => data.MainBranch;

    public IActionResult Patch(Delta<Branch> delta)
    {
        delta.Patch(data.MainBranch);
        return Updated(data.MainBranch);
    }
}
