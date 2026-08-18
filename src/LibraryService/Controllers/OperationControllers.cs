using Library.Catalog;
using Library.Circulation;
using LibraryService.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Formatter;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Results;
using Microsoft.AspNetCore.OData.Routing.Attributes;
using Microsoft.AspNetCore.OData.Routing.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.OData.Edm;

namespace LibraryService.Controllers;

/// <summary>
/// Reading a declared parameter out of an action's request body.
///
/// A body that carries none of the declared parameters - <c>{}</c> - binds to a <b>null</b>
/// <c>ODataActionParameters</c>, not to an empty dictionary. The parameter therefore has to be declared
/// nullable and every read has to survive that null, otherwise the dereference happens before the
/// controller's own check and answers 500. A required parameter that is missing is a malformed request:
/// 400.
/// </summary>
internal static class ActionParameters
{
    /// <summary>The parameter's value, or null if the body did not carry it - or carried nothing at all.</summary>
    internal static object? Get(this ODataActionParameters? parameters, string name) =>
        parameters is not null && parameters.TryGetValue(name, out var value) ? value : null;

    /// <summary>
    /// <c>BadRequestODataResult</c>, not <c>ControllerBase.BadRequest</c>: only the OData result renders an
    /// error payload. A plain <c>BadRequestObjectResult</c> holding a string comes back as an
    /// <c>Edm.String</c> value with a 400 attached.
    /// </summary>
    internal static BadRequestODataResult Missing(string name) => new($"Parameter '{name}' is required.");
}

/// <summary>
/// The unbound operations, i.e. everything reachable directly from the service root. Routed by
/// convention: an action named like the function import is matched to it.
///
/// The implementations are deliberately thin. Their job is to prove that each operation of the reference
/// model is callable and answers with a payload of the declared shape - not to be a library backend.
/// </summary>
public class LibraryOperationsController(LibraryContext db) : ODataController
{
    [HttpGet("odata/v4/library/TotalMediaCount()")]
    public long TotalMediaCount() => db.Media.Count();

    [HttpGet("odata/v4/library/AllLanguages()")]
    public IEnumerable<string> AllLanguages() =>
        db.Media.Select(m => m.Language).OfType<string>().Distinct().Order();

    [HttpGet("odata/v4/library/LoanStatistics(Period={period})")]
    public LoanStats LoanStatistics([FromODataUri] DateRange? period)
    {
        // Materialised first on purpose: the period is compared as a DateOnly against the date part of a
        // DateTimeOffset, and that comparison is done in memory rather than pushed into SQL. This is an
        // operation, not a query option - nothing about the reference model is being probed by translating
        // it, and over a seed of one loan there is nothing to gain either.
        var loans = db.Loans.AsEnumerable();
        if (period?.From is { } from)
        {
            loans = loans.Where(l => DateOnly.FromDateTime(l.LoanedAt.Date) >= from);
        }

        if (period?.To is { } to)
        {
            loans = loans.Where(l => DateOnly.FromDateTime(l.LoanedAt.Date) <= to);
        }

        var list = loans.ToList();
        return new LoanStats
        {
            TotalLoans = list.Count,
            AverageLoanDuration = list.Count == 0 ? TimeSpan.Zero : TimeSpan.FromDays(14),
        };
    }

    [HttpGet("odata/v4/library/StatsPerBranch()")]
    public IEnumerable<BranchStats> StatsPerBranch() =>
        db.Branches.Select(b => new BranchStats { BranchId = b.Id, LoanCount = b.Id });

    [HttpGet("odata/v4/library/MostReadMedium()")]
    public ActionResult<Medium> MostReadMedium() =>
        db.Media.OrderByDescending(m => m.PopularityScore ?? 0).FirstOrDefault() is { } medium
            ? medium
            : NotFound();

    /// <summary>Composable: the result may carry further path segments and query options.</summary>
    [HttpGet("odata/v4/library/NewReleases()")]
    [EnableQuery]
    public IQueryable<Medium> NewReleases() =>
        db.Media.AsNoTracking().Where(m => m.PublicationDate >= new DateOnly(2020, 1, 1));

    /// <summary>
    /// Both overloads of <c>Search</c> - with and without <c>MaxResults</c> - in one action.
    ///
    /// They cannot be two actions: OData routing resolves the function by name and picks the
    /// single-parameter route for both URLs, so a request carrying <c>MaxResults</c> silently landed on
    /// the overload that ignores it and returned the full result. The metadata still declares both
    /// overloads; only the dispatch is shared.
    /// </summary>
    [HttpGet("odata/v4/library/Search(Term={term})")]
    [HttpGet("odata/v4/library/Search(Term={term},MaxResults={maxResults})")]
    [EnableQuery]
    public IQueryable<Medium> Search([FromODataUri] string term)
    {
        var matches = Matching(term);

        // Read MaxResults off the URL rather than from a bound parameter: OData registers only one
        // endpoint for the function name, so the second route template never binds and the parameter
        // would silently stay null - the exact failure this fixes.
        var limit = MaxResultsFromUrl();
        return (limit is { } max ? matches.Take(max) : matches).AsQueryable();
    }

    private int? MaxResultsFromUrl()
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            Request.Path.Value ?? "",
            @"MaxResults=(\d+)");

        return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : null;
    }

    [HttpPost("odata/v4/library/ClosureDay")]
    public IActionResult ClosureDay([FromBody] ODataActionParameters? parameters) =>
        parameters.Get("Date") is null ? ActionParameters.Missing("Date") : NoContent();

    [HttpPost("odata/v4/library/NextInventoryNumber")]
    public int NextInventoryNumber() => db.Copies.Any() ? db.Copies.Max(c => c.InventoryNumber) + 1 : 1;

    /// <summary>
    /// The one action here whose parameter is nullable in <c>$metadata</c>: a body without
    /// <c>Obsolete</c> is a legal call, and it is exactly that body which arrives as a null
    /// <c>ODataActionParameters</c>. Nothing is filtered out then.
    /// </summary>
    [HttpPost("odata/v4/library/CleanUpKeywords")]
    public IEnumerable<string> CleanUpKeywords([FromBody] ODataActionParameters? parameters)
    {
        var obsolete = parameters.Get("Obsolete") is IEnumerable<string> list
            ? list.ToHashSet()
            : [];

        return db.Media.AsEnumerable().SelectMany(m => m.Keywords).Distinct().Where(k => !obsolete.Contains(k)).Order();
    }

    [HttpPost("odata/v4/library/YearEndClosing")]
    public ActionResult<AnnualReport> YearEndClosing([FromBody] ODataActionParameters? parameters)
    {
        if (parameters.Get("Year") is not { } year)
        {
            return ActionParameters.Missing("Year");
        }

        return new AnnualReport
        {
            Year = Convert.ToInt32(year),
            TotalLoans = db.Loans.Count(),
            TotalLateFees = db.Loans.Sum(l => l.LateFee) ?? 0m,
        };
    }

    [HttpPost("odata/v4/library/RunOverdueNotices")]
    public IEnumerable<OverdueNotice> RunOverdueNotices() => db.Loans.AsEnumerable().Select(Notice);

    [HttpPost("odata/v4/library/AcquireCollectorsItem")]
    public ActionResult<Medium> AcquireCollectorsItem([FromBody] ODataActionParameters parameters)
    {
        if (!parameters.TryGetValue("Title", out var title) || title is not string name)
        {
            return BadRequest("Parameter 'Title' is required.");
        }

        var item = new CollectorsItem
        {
            Id = Guid.NewGuid(),
            Title = name,
            ExtraData = parameters.TryGetValue("Description", out var description) ? description : null,
        };
        db.Media.Add(item);
        db.SaveChanges();
        return Created(item);
    }

    [HttpPost("odata/v4/library/RunStockCheck")]
    [EnableQuery]
    public IQueryable<Medium> RunStockCheck() =>
        db.Media.AsNoTracking().Where(m => m.Copies.Count == 0);

    internal static OverdueNotice Notice(Loan loan) =>
        new()
        {
            Reason = loan.ReturnedAt is null ? "Not returned" : "Returned late",
            Amount = loan.LateFee ?? 0m,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    /// <summary>
    /// Case-insensitive title match. <c>StringComparison.OrdinalIgnoreCase</c> has no SQL translation, so
    /// the comparison is spelled out in a form the provider does translate: lowering both sides is
    /// explicit about the intent and leaves nothing to the column's collation.
    /// </summary>
    private IQueryable<Medium> Matching(string term) =>
        db.Media.AsNoTracking().Where(m => m.Title.ToLower().Contains(term.ToLower()));
}

/// <summary>Operations bound to <c>Library.Catalog.Medium</c>, single instance and collection.</summary>
public class MediaOperationsController(LibraryContext db) : ODataController
{
    [HttpGet("odata/v4/library/Media({key})/Library.Circulation.LoanMetrics()")]
    public ActionResult<MediumStats> LoanMetrics([FromRoute] Guid key)
    {
        if (db.Media.All(m => m.Id != key))
        {
            return NotFound();
        }

        var loans = db.Loans.Count(l => l.Copy != null && l.Copy.MediumId == key);
        return new MediumStats
        {
            TotalLoanCount = loans,
            AverageLoanDuration = TimeSpan.FromDays(21),
        };
    }

    [HttpGet("odata/v4/library/Media({key})/Library.Circulation.AvailableCopy()")]
    public ActionResult<Copy> AvailableCopy([FromRoute] Guid key) =>
        db.Copies.FirstOrDefault(c => c.MediumId == key && c.Status == AvailabilityStatus.Available) is { } copy
            ? copy
            : NotFound();

    [HttpGet("odata/v4/library/Media({key})/Library.Circulation.AvailableCopies()")]
    [EnableQuery]
    public IQueryable<Copy> AvailableCopies([FromRoute] Guid key) =>
        db.Copies.AsNoTracking().Where(c => c.MediumId == key && c.Status == AvailabilityStatus.Available);

    /// <summary>Second overload of the pair - bound to the collection rather than to one instance.</summary>
    [HttpGet("odata/v4/library/Media/Library.Circulation.AvailableCopies()")]
    [EnableQuery]
    public IQueryable<Copy> AvailableCopiesForAll() =>
        db.Copies.AsNoTracking().Where(c => c.Status == AvailabilityStatus.Available);

    [HttpGet("odata/v4/library/Media/Library.Circulation.AvailableLanguages()")]
    public IEnumerable<string> AvailableLanguages() =>
        db.Media.Select(m => m.Language).OfType<string>().Distinct().Order();

    [HttpPost("odata/v4/library/Media({key})/Library.Circulation.Reserve")]
    public ActionResult<int> Reserve([FromRoute] Guid key, [FromBody] ODataActionParameters? parameters)
    {
        if (db.Media.All(m => m.Id != key))
        {
            return NotFound();
        }

        if (parameters.Get("MemberId") is not { } memberId)
        {
            return ActionParameters.Missing("MemberId");
        }

        var reservation = new Reservation { Id = Guid.NewGuid(), ReservedAt = DateTimeOffset.UtcNow };

        // Added to the set first, and only then linked to the member. Reaching a new entity solely through
        // a tracked entity's navigation property is not enough: its key is caller-assigned, so the change
        // tracker cannot tell a new row from an existing one and settles on "existing" - which turns the
        // insert into an UPDATE of a row that does not exist yet, and fails the whole request.
        db.Reservations.Add(reservation);

        if (db.Members.Include(m => m.Reservations)
                .FirstOrDefault(m => m.Id == Convert.ToInt32(memberId)) is { } member)
        {
            member.Reservations.Add(reservation);
        }

        db.SaveChanges();
        return db.Reservations.Count();
    }
}

/// <summary>Operations bound to <c>Library.Circulation.Member</c>.</summary>
public class MemberOperationsController(LibraryContext db) : ODataController
{
    [HttpGet("odata/v4/library/Members({key})/Library.Circulation.OutstandingBalance()")]
    public ActionResult<decimal> OutstandingBalance([FromRoute] int key) =>
        db.Members.FirstOrDefault(m => m.Id == key) is { } member ? member.Balance : NotFound();

    [HttpGet("odata/v4/library/Members({key})/Library.Circulation.NoticeHistory()")]
    public ActionResult<IEnumerable<OverdueNotice>> NoticeHistory([FromRoute] int key) =>
        db.Members.Include(m => m.Loans).FirstOrDefault(m => m.Id == key) is { } member
            ? member.Loans.Select(LibraryOperationsController.Notice).ToList()
            : NotFound();

    [HttpPost("odata/v4/library/Members({key})/Library.Circulation.RunReminders")]
    public ActionResult<IEnumerable<OverdueNotice>> RunReminders([FromRoute] int key) =>
        db.Members.Include(m => m.Loans).FirstOrDefault(m => m.Id == key) is { } member
            ? member.Loans.Select(LibraryOperationsController.Notice).ToList()
            : NotFound();
}

/// <summary>Operations bound to <c>Library.Circulation.Copy</c>, which has a composite key.</summary>
public class CopyOperationsController(LibraryContext db) : ODataController
{
    [HttpPost("odata/v4/library/Copies(MediumId={keyMediumId},InventoryNumber={keyInventoryNumber})/Library.Circulation.CheckOut")]
    public IActionResult CheckOut(
        [FromRoute] Guid keyMediumId,
        [FromRoute] int keyInventoryNumber,
        [FromBody] ODataActionParameters? parameters)
    {
        var copy = CopiesController.Find(db, keyMediumId, keyInventoryNumber);
        if (copy is null)
        {
            return NotFound();
        }

        if (parameters.Get("MemberId") is not { } memberId)
        {
            return ActionParameters.Missing("MemberId");
        }

        if (db.Members.FirstOrDefault(m => m.Id == Convert.ToInt32(memberId)) is not { } member)
        {
            return BadRequest("Unknown MemberId.");
        }

        copy.Status = AvailabilityStatus.OnLoan;
        var loan = new Loan
        {
            Id = Guid.NewGuid(),
            LoanedAt = DateTimeOffset.UtcNow,
            DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(28)),
            Member = member,
            Copy = copy,
        };
        db.Loans.Add(loan);
        db.SaveChanges();
        return NoContent();
    }

    [HttpPost("odata/v4/library/Copies(MediumId={keyMediumId},InventoryNumber={keyInventoryNumber})/Library.Circulation.AssessCondition")]
    public ActionResult<ConditionReport> AssessCondition(
        [FromRoute] Guid keyMediumId,
        [FromRoute] int keyInventoryNumber,
        [FromBody] ODataActionParameters? parameters)
    {
        var copy = CopiesController.Find(db, keyMediumId, keyInventoryNumber);
        if (copy is null)
        {
            return NotFound();
        }

        if (parameters.Get("NewCondition") is not { } newCondition)
        {
            return ActionParameters.Missing("NewCondition");
        }

        var before = copy.Condition;
        copy.Condition = Convert.ToByte(newCondition);

        db.SaveChanges();
        return new ConditionReport
        {
            ConditionBefore = before,
            ConditionAfter = copy.Condition,
            Remark = parameters.Get("Remark") as string,
        };
    }
}

/// <summary>Operations bound to <c>Library.Circulation.Loan</c>, single instance and collection.</summary>
public class LoanOperationsController(LibraryContext db) : ODataController
{
    [HttpPost("odata/v4/library/Loans({key})/Library.Circulation.Renew")]
    public ActionResult<Loan> Renew([FromRoute] Guid key)
    {
        var loan = db.Loans.FirstOrDefault(l => l.Id == key);
        if (loan is null)
        {
            return NotFound();
        }

        loan.DueDate = loan.DueDate.AddDays(28);
        db.SaveChanges();
        return loan;
    }

    [HttpPost("odata/v4/library/Loans/Library.Circulation.RenewAll")]
    [EnableQuery]
    public IQueryable<Loan> RenewAll()
    {
        foreach (var loan in db.Loans)
        {
            loan.DueDate = loan.DueDate.AddDays(28);
        }

        db.SaveChanges();
        return db.Loans;
    }

    [HttpPost("odata/v4/library/Loans/Library.Circulation.BulkRenew")]
    public IEnumerable<string> BulkRenew() =>
        db.Loans.AsEnumerable().Select(l => $"{l.Id} renewed until {l.DueDate.AddDays(28):yyyy-MM-dd}");
}
