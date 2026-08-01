using Library.Catalog;
using Library.Circulation;
using LibraryService.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Formatter;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Routing.Attributes;
using Microsoft.AspNetCore.OData.Routing.Controllers;
using Microsoft.OData.Edm;

namespace LibraryService.Controllers;

/// <summary>
/// The unbound operations, i.e. everything reachable directly from the service root. Routed by
/// convention: an action named like the function import is matched to it.
///
/// The implementations are deliberately thin. Their job is to prove that each operation of the reference
/// model is callable and answers with a payload of the declared shape - not to be a library backend.
/// </summary>
public class LibraryOperationsController(LibraryData data) : ODataController
{
    [HttpGet("odata/v4/library/TotalMediaCount()")]
    public long TotalMediaCount() => data.Media.Count;

    [HttpGet("odata/v4/library/AllLanguages()")]
    public IEnumerable<string> AllLanguages() =>
        data.Media.Select(m => m.Language).OfType<string>().Distinct().Order();

    [HttpGet("odata/v4/library/LoanStatistics(Period={period})")]
    public LoanStats LoanStatistics([FromODataUri] DateRange? period)
    {
        var loans = data.Loans.AsEnumerable();
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
        data.Branches.Select(b => new BranchStats { BranchId = b.Id, LoanCount = b.Id });

    [HttpGet("odata/v4/library/MostReadMedium()")]
    public ActionResult<Medium> MostReadMedium() =>
        data.Media.OrderByDescending(m => m.PopularityScore ?? 0).FirstOrDefault() is { } medium
            ? medium
            : NotFound();

    /// <summary>Composable: the result may carry further path segments and query options.</summary>
    [HttpGet("odata/v4/library/NewReleases()")]
    [EnableQuery]
    public IQueryable<Medium> NewReleases() =>
        data.Media.Where(m => m.PublicationDate >= new DateOnly(2020, 1, 1)).AsQueryable();

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
    public IActionResult ClosureDay([FromBody] ODataActionParameters parameters) =>
        parameters.ContainsKey("Date") ? NoContent() : BadRequest("Parameter 'Date' is required.");

    [HttpPost("odata/v4/library/NextInventoryNumber")]
    public int NextInventoryNumber() => data.Copies.Count == 0 ? 1 : data.Copies.Max(c => c.InventoryNumber) + 1;

    [HttpPost("odata/v4/library/CleanUpKeywords")]
    public IEnumerable<string> CleanUpKeywords([FromBody] ODataActionParameters parameters)
    {
        var obsolete = parameters.TryGetValue("Obsolete", out var value) && value is IEnumerable<string> list
            ? list.ToHashSet()
            : [];

        return data.Media.SelectMany(m => m.Keywords).Distinct().Where(k => !obsolete.Contains(k)).Order();
    }

    [HttpPost("odata/v4/library/YearEndClosing")]
    public AnnualReport YearEndClosing([FromBody] ODataActionParameters parameters) =>
        new()
        {
            Year = parameters.TryGetValue("Year", out var year) ? Convert.ToInt32(year) : DateTime.UtcNow.Year,
            TotalLoans = data.Loans.Count,
            TotalLateFees = data.Loans.Sum(l => l.LateFee ?? 0m),
        };

    [HttpPost("odata/v4/library/RunOverdueNotices")]
    public IEnumerable<OverdueNotice> RunOverdueNotices() => data.Loans.Select(Notice);

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
        data.Media.Add(item);
        return Created(item);
    }

    [HttpPost("odata/v4/library/RunStockCheck")]
    [EnableQuery]
    public IQueryable<Medium> RunStockCheck() =>
        data.Media.Where(m => m.Copies.Count == 0).AsQueryable();

    internal static OverdueNotice Notice(Loan loan) =>
        new()
        {
            Reason = loan.ReturnedAt is null ? "Not returned" : "Returned late",
            Amount = loan.LateFee ?? 0m,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private IEnumerable<Medium> Matching(string term) =>
        data.Media.Where(m => m.Title.Contains(term, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Operations bound to <c>Library.Catalog.Medium</c>, single instance and collection.</summary>
public class MediaOperationsController(LibraryData data) : ODataController
{
    [HttpGet("odata/v4/library/Media({key})/Library.Circulation.LoanMetrics()")]
    public ActionResult<MediumStats> LoanMetrics([FromRoute] Guid key)
    {
        if (data.Media.All(m => m.Id != key))
        {
            return NotFound();
        }

        var loans = data.Loans.Count(l => l.Copy?.MediumId == key);
        return new MediumStats
        {
            TotalLoanCount = loans,
            AverageLoanDuration = TimeSpan.FromDays(21),
        };
    }

    [HttpGet("odata/v4/library/Media({key})/Library.Circulation.AvailableCopy()")]
    public ActionResult<Copy> AvailableCopy([FromRoute] Guid key) =>
        data.Copies.FirstOrDefault(c => c.MediumId == key && c.Status == AvailabilityStatus.Available) is { } copy
            ? copy
            : NotFound();

    [HttpGet("odata/v4/library/Media({key})/Library.Circulation.AvailableCopies()")]
    [EnableQuery]
    public IQueryable<Copy> AvailableCopies([FromRoute] Guid key) =>
        data.Copies.Where(c => c.MediumId == key && c.Status == AvailabilityStatus.Available).AsQueryable();

    /// <summary>Second overload of the pair - bound to the collection rather than to one instance.</summary>
    [HttpGet("odata/v4/library/Media/Library.Circulation.AvailableCopies()")]
    [EnableQuery]
    public IQueryable<Copy> AvailableCopiesForAll() =>
        data.Copies.Where(c => c.Status == AvailabilityStatus.Available).AsQueryable();

    [HttpGet("odata/v4/library/Media/Library.Circulation.AvailableLanguages()")]
    public IEnumerable<string> AvailableLanguages() =>
        data.Media.Select(m => m.Language).OfType<string>().Distinct().Order();

    [HttpPost("odata/v4/library/Media({key})/Library.Circulation.Reserve")]
    public ActionResult<int> Reserve([FromRoute] Guid key, [FromBody] ODataActionParameters parameters)
    {
        if (data.Media.All(m => m.Id != key))
        {
            return NotFound();
        }

        var reservation = new Reservation { Id = Guid.NewGuid(), ReservedAt = DateTimeOffset.UtcNow };
        data.Reservations.Add(reservation);

        if (parameters.TryGetValue("MemberId", out var memberId)
            && data.Members.FirstOrDefault(m => m.Id == Convert.ToInt32(memberId)) is { } member)
        {
            member.Reservations.Add(reservation);
        }

        return data.Reservations.Count;
    }
}

/// <summary>Operations bound to <c>Library.Circulation.Member</c>.</summary>
public class MemberOperationsController(LibraryData data) : ODataController
{
    [HttpGet("odata/v4/library/Members({key})/Library.Circulation.OutstandingBalance()")]
    public ActionResult<decimal> OutstandingBalance([FromRoute] int key) =>
        data.Members.FirstOrDefault(m => m.Id == key) is { } member ? member.Balance : NotFound();

    [HttpGet("odata/v4/library/Members({key})/Library.Circulation.NoticeHistory()")]
    public ActionResult<IEnumerable<OverdueNotice>> NoticeHistory([FromRoute] int key) =>
        data.Members.FirstOrDefault(m => m.Id == key) is { } member
            ? member.Loans.Select(LibraryOperationsController.Notice).ToList()
            : NotFound();

    [HttpPost("odata/v4/library/Members({key})/Library.Circulation.RunReminders")]
    public ActionResult<IEnumerable<OverdueNotice>> RunReminders([FromRoute] int key) =>
        data.Members.FirstOrDefault(m => m.Id == key) is { } member
            ? member.Loans.Select(LibraryOperationsController.Notice).ToList()
            : NotFound();
}

/// <summary>Operations bound to <c>Library.Circulation.Copy</c>, which has a composite key.</summary>
public class CopyOperationsController(LibraryData data) : ODataController
{
    [HttpPost("odata/v4/library/Copies(MediumId={keyMediumId},InventoryNumber={keyInventoryNumber})/Library.Circulation.CheckOut")]
    public IActionResult CheckOut(
        [FromRoute] Guid keyMediumId,
        [FromRoute] int keyInventoryNumber,
        [FromBody] ODataActionParameters parameters)
    {
        var copy = CopiesController.Find(data, keyMediumId, keyInventoryNumber);
        if (copy is null)
        {
            return NotFound();
        }

        if (!parameters.TryGetValue("MemberId", out var memberId)
            || data.Members.FirstOrDefault(m => m.Id == Convert.ToInt32(memberId)) is not { } member)
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
        data.Loans.Add(loan);
        member.Loans.Add(loan);
        return NoContent();
    }

    [HttpPost("odata/v4/library/Copies(MediumId={keyMediumId},InventoryNumber={keyInventoryNumber})/Library.Circulation.AssessCondition")]
    public ActionResult<ConditionReport> AssessCondition(
        [FromRoute] Guid keyMediumId,
        [FromRoute] int keyInventoryNumber,
        [FromBody] ODataActionParameters parameters)
    {
        var copy = CopiesController.Find(data, keyMediumId, keyInventoryNumber);
        if (copy is null)
        {
            return NotFound();
        }

        var before = copy.Condition;
        if (parameters.TryGetValue("NewCondition", out var newCondition))
        {
            copy.Condition = Convert.ToByte(newCondition);
        }

        return new ConditionReport
        {
            ConditionBefore = before,
            ConditionAfter = copy.Condition,
            Remark = parameters.TryGetValue("Remark", out var remark) ? remark as string : null,
        };
    }
}

/// <summary>Operations bound to <c>Library.Circulation.Loan</c>, single instance and collection.</summary>
public class LoanOperationsController(LibraryData data) : ODataController
{
    [HttpPost("odata/v4/library/Loans({key})/Library.Circulation.Renew")]
    public ActionResult<Loan> Renew([FromRoute] Guid key)
    {
        var loan = data.Loans.FirstOrDefault(l => l.Id == key);
        if (loan is null)
        {
            return NotFound();
        }

        loan.DueDate = loan.DueDate.AddDays(28);
        return loan;
    }

    [HttpPost("odata/v4/library/Loans/Library.Circulation.RenewAll")]
    [EnableQuery]
    public IQueryable<Loan> RenewAll()
    {
        foreach (var loan in data.Loans)
        {
            loan.DueDate = loan.DueDate.AddDays(28);
        }

        return data.Loans.AsQueryable();
    }

    [HttpPost("odata/v4/library/Loans/Library.Circulation.BulkRenew")]
    public IEnumerable<string> BulkRenew() =>
        data.Loans.Select(l => $"{l.Id} renewed until {l.DueDate.AddDays(28):yyyy-MM-dd}");
}
