using Microsoft.OData;

namespace LibraryService.Batch;

/// <summary>
/// A <c>$batch</c> document that OData's parser refused.
///
/// Its own type so that the error middleware in Program.cs can tell it apart from everything else that can
/// go wrong while a request is being answered: a document the parser refuses is the client's doing and
/// answers 400, while a fault the library raises while answering a batch it did read - or anything else -
/// is the server's and answers 500.
///
/// The parser refuses with <see cref="ODataException" />, but that is the library's catch-all and the batch
/// writer answers with the same type for state violations of its own, so the type cannot be mapped to 400
/// wholesale. <see cref="LibraryBatchHandler" /> translates the refusal at the one place it can happen -
/// the parse phase, which runs before any sub-request is answered - so a refused document is the only thing
/// this type can carry. The message is the parser's, verbatim.
/// </summary>
public sealed class BatchRequestParseException(ODataException parser)
    : Exception(parser.Message, parser)
{
}
