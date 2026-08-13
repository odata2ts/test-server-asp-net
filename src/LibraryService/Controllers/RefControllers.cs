using Library.Circulation;
using LibraryService.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Routing.Controllers;
using Microsoft.EntityFrameworkCore;

namespace LibraryService.Controllers;

/// <summary>
/// Relationship management through <c>$ref</c>: reading, creating and removing links without touching
/// the entities themselves.
///
/// Covers both cardinalities, since they differ in the HTTP verbs the spec assigns them:
/// a collection-valued navigation property takes <c>POST</c> to add and <c>DELETE</c> with <c>$id</c> to
/// remove, a single-valued one takes <c>PUT</c> to set and plain <c>DELETE</c> to clear.
/// </summary>
public class MemberRefController(LibraryContext db) : ODataController
{
    /// <summary>The links of a collection-valued navigation property.</summary>
    [HttpGet("odata/v4/library/Members({key})/Loans/$ref")]
    public IActionResult GetLoanRefs([FromRoute] int key)
    {
        var member = db.Members.Include(m => m.Loans).FirstOrDefault(m => m.Id == key);
        if (member is null)
        {
            return NotFound();
        }

        var baseUri = ServiceRoot();
        return Ok(
            new
            {
                value = member.Loans.Select(l => new Dictionary<string, string>
                {
                    ["@odata.id"] = $"{baseUri}Loans({l.Id})",
                }),
            });
    }

    /// <summary>Adds an existing loan to the member, by reference.</summary>
    [HttpPost("odata/v4/library/Members({key})/Loans/$ref")]
    public IActionResult AddLoanRef([FromRoute] int key, [FromBody] ODataReference reference)
    {
        var member = db.Members.Include(m => m.Loans).FirstOrDefault(m => m.Id == key);
        if (member is null)
        {
            return NotFound();
        }

        if (!TryResolveKey(reference.ODataId, out var loanId)
            || db.Loans.FirstOrDefault(l => l.Id == loanId) is not { } loan)
        {
            return BadRequest("The referenced loan does not exist.");
        }

        if (!member.Loans.Contains(loan))
        {
            member.Loans.Add(loan);
            loan.Member = member;
            db.SaveChanges();
        }

        return NoContent();
    }

    /// <summary>Removes one link of the collection; the target is named by the <c>$id</c> query option.</summary>
    [HttpDelete("odata/v4/library/Members({key})/Loans/$ref")]
    public IActionResult DeleteLoanRef([FromRoute] int key, [FromQuery(Name = "$id")] string? id)
    {
        var member = db.Members.Include(m => m.Loans).FirstOrDefault(m => m.Id == key);
        if (member is null)
        {
            return NotFound();
        }

        if (!TryResolveKey(id, out var loanId)
            || member.Loans.FirstOrDefault(l => l.Id == loanId) is not { } loan)
        {
            return NotFound();
        }

        // Unlinks the loan without deleting it, which is why the foreign key behind Member.Loans is
        // mapped as optional even though the association carries a cascading delete.
        member.Loans.Remove(loan);
        loan.Member = null;
        db.SaveChanges();
        return NoContent();
    }

    /// <summary>The link of a single-valued navigation property.</summary>
    [HttpGet("odata/v4/library/Members({key})/IdDocument/$ref")]
    public IActionResult GetIdDocumentRef([FromRoute] int key)
    {
        var member = db.Members.Include(m => m.IdDocument).FirstOrDefault(m => m.Id == key);
        if (member is null)
        {
            return NotFound();
        }

        if (member.IdDocument is null)
        {
            return NoContent();
        }

        return Ok(
            new Dictionary<string, string>
            {
                ["@odata.id"] = $"{ServiceRoot()}IdDocuments({member.IdDocument.Id})",
            });
    }

    /// <summary>Sets the single-valued reference - <c>PUT</c>, not <c>POST</c>, since it replaces.</summary>
    [HttpPut("odata/v4/library/Members({key})/IdDocument/$ref")]
    public IActionResult PutIdDocumentRef([FromRoute] int key, [FromBody] ODataReference reference)
    {
        var member = db.Members.FirstOrDefault(m => m.Id == key);
        if (member is null)
        {
            return NotFound();
        }

        if (!TryResolveKey(reference.ODataId, out var documentId)
            || db.IdDocuments.FirstOrDefault(d => d.Id == documentId) is not { } document)
        {
            return BadRequest("The referenced document does not exist.");
        }

        member.IdDocument = document;
        db.SaveChanges();
        return NoContent();
    }

    [HttpDelete("odata/v4/library/Members({key})/IdDocument/$ref")]
    public IActionResult DeleteIdDocumentRef([FromRoute] int key)
    {
        var member = db.Members.Include(m => m.IdDocument).FirstOrDefault(m => m.Id == key);
        if (member is null)
        {
            return NotFound();
        }

        member.IdDocument = null;
        db.SaveChanges();
        return NoContent();
    }

    /// <summary>Absolute service root, so the emitted entity ids are resolvable.</summary>
    private string ServiceRoot() => $"{Request.Scheme}://{Request.Host}/odata/v4/library/";

    /// <summary>
    /// Pulls the key out of an entity id. The spec allows an absolute or a relative URI, so only the last
    /// path segment is examined: <c>…/Loans(&lt;guid&gt;)</c>.
    /// </summary>
    private static bool TryResolveKey(string? odataId, out Guid key)
    {
        key = Guid.Empty;
        if (string.IsNullOrWhiteSpace(odataId))
        {
            return false;
        }

        var start = odataId.LastIndexOf('(');
        var end = odataId.LastIndexOf(')');
        return start >= 0 && end > start && Guid.TryParse(odataId[(start + 1)..end], out key);
    }
}

/// <summary>Body of a <c>$ref</c> request: a single <c>@odata.id</c>.</summary>
public class ODataReference
{
    [System.Text.Json.Serialization.JsonPropertyName("@odata.id")]
    public string? ODataId { get; set; }
}
