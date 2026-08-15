namespace LibraryService.Data;

/// <summary>
/// A client supplied an <c>Edm.DateTimeOffset</c> that is not UTC.
///
/// Its own type so that the error middleware in Program.cs can tell it apart from everything else that can
/// go wrong while a request is being answered: this one is the client's doing and answers 400, while an
/// untranslatable query option or a provider fault is the server's and answers 500. Without the
/// distinction a rejected timestamp would arrive as an internal error, which is neither true nor
/// actionable.
///
/// Thrown from <see cref="ValueConversions.RequireUtc" /> - see there for why the service is UTC-only and
/// where that is written down.
/// </summary>
public sealed class UtcOnlyException(DateTimeOffset value)
    : Exception(
        $"This service accepts UTC timestamps only, but the request carried '{value:O}' with an offset of "
        // "O" on a UTC DateTime already ends in Z - do not add a second one.
        + $"{value.Offset:hh\\:mm}. Send the same instant as UTC ('{value.UtcDateTime:O}') instead.")
{
    public DateTimeOffset Value { get; } = value;
}
