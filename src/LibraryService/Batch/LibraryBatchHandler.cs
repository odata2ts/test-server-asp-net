using Microsoft.AspNetCore.OData.Batch;
using Microsoft.OData;

namespace LibraryService.Batch;

/// <summary>
/// Answers a <c>$batch</c> the parser refuses with a 400 instead of the 500 its <see cref="ODataException" />
/// would become.
///
/// The parser reads the whole document before any sub-request is answered - the MIME or JSON structure, and a
/// <c>$&lt;id&gt;</c> reference validated against the referring request's <c>dependsOn</c> - and every refusal
/// it makes is the client's doing: no server could answer a document it cannot read. The parser reports them
/// as <see cref="ODataException" />, the library's catch-all, and the batch writer answers with the same type
/// for state violations of its own - on a batch the parser already accepted. The refusal is translated here,
/// in the only phase that can produce one, so the 400 stays exact: nothing downstream of the parse can answer
/// <see cref="BatchRequestParseException" />, and a <see cref="ODataException" /> that escapes this method is
/// left to become a 500.
/// </summary>
public sealed class LibraryBatchHandler : DefaultODataBatchHandler
{
    public override async Task<IList<ODataBatchRequestItem>> ParseBatchRequestsAsync(HttpContext context)
    {
        try
        {
            return await base.ParseBatchRequestsAsync(context);
        }
        catch (ODataException parser)
        {
            throw new BatchRequestParseException(parser);
        }
    }
}
