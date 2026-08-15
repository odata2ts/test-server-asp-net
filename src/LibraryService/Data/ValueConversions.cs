using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Spatial;

namespace LibraryService.Data;

/// <summary>
/// The two places where the reference model asks for something a relational column cannot hold directly.
/// Each is a distortion, so each is written down here rather than spread over
/// <see cref="LibraryContext" /> - and each is reported in FEATURE-COVERAGE.md under what the persistence
/// layer costs.
///
/// It used to be five. <c>decimal</c>, <c>DateTimeOffset</c> and <c>TimeSpan</c> each needed a converter
/// under SQLite, which has no type for any of them: the first was scaled to an integer, the other two were
/// stored as ticks, all three because the text form SQLite falls back to compares lexically and made
/// <c>$filter</c> and <c>$orderby</c> either wrong or untranslatable. Postgres has <c>numeric</c>,
/// <c>timestamptz</c> and <c>interval</c>, so all three properties are now stored as themselves and the
/// converters are gone rather than rewritten.
/// </summary>
public static class ValueConversions
{
    private static readonly WellKnownTextSqlFormatter SpatialFormat = WellKnownTextSqlFormatter.Create();

    /// <summary>
    /// Spatial values as WKT text.
    ///
    /// EF Core's spatial support is NetTopologySuite-only and would additionally need the SpatiaLite
    /// native extension; <c>Microsoft.Spatial</c> - which is what OData itself speaks - has no provider at
    /// all. WKT is the honest way out: the value round-trips exactly, SRID included, so <c>$metadata</c>
    /// and every payload are unchanged. The column is opaque text, so no <c>geo.*</c> function can ever
    /// translate to SQL - which costs nothing today, because those do not work over LINQ to Objects either.
    /// </summary>
    public static ValueConverter<T, string> Spatial<T>()
        where T : class, ISpatial =>
        new(value => ToWkt(value), text => FromWkt<T>(text));

    /// <summary>
    /// Compares by WKT rather than by reference: the spatial implementations are immutable, but EF still
    /// needs a snapshot to detect that a property was replaced.
    /// </summary>
    public static ValueComparer<T> SpatialComparer<T>()
        where T : class, ISpatial =>
        new((left, right) => ToWkt(left!) == ToWkt(right!), value => ToWkt(value).GetHashCode(), value => value);

    private static string ToWkt(ISpatial value)
    {
        var writer = new StringWriter();
        SpatialFormat.Write(value, writer);
        return writer.ToString();
    }

    private static T FromWkt<T>(string text)
        where T : class, ISpatial
    {
        using var reader = new StringReader(text);
        return SpatialFormat.Read<T>(reader)!;
    }

    /// <summary>
    /// Rejects any <c>DateTimeOffset</c> that is not UTC, rather than quietly converting it.
    ///
    /// This service speaks UTC: timestamps arrive as UTC, are stored as UTC and are returned as UTC. That
    /// is a deliberate deviation - <c>Edm.DateTimeOffset</c> permits any offset and a fully conformant
    /// server round-trips <c>+02:00</c> unchanged - and it is recorded as one in FEATURE-COVERAGE.md. It
    /// is also what the whole reference model already does, and UTC on the wire and at rest is the sane
    /// way to run a service.
    ///
    /// A deviation is only defensible if it is visible, so a non-UTC value is refused with a 400 naming
    /// the property. Normalising instead would be the worse deviation: the client's value would come back
    /// changed with nothing to indicate that the server had decided to reinterpret it.
    ///
    /// A guard and not a conversion, so it sits in the same place a conversion would - the value passes
    /// through untouched, which is what <c>timestamptz</c> stores anyway, to microsecond resolution.
    /// </summary>
    public static ValueConverter<DateTimeOffset, DateTimeOffset> UtcOnly() =>
        new(value => RequireUtc(value), stored => stored);

    /// <summary>
    /// Throws unless <paramref name="value" /> is UTC.
    ///
    /// Reached from both directions a client can supply a timestamp from - the body of a write and a
    /// literal in <c>$filter</c> - because EF puts value converters in front of both, which is what makes
    /// one guard enough to keep the two consistent.
    /// </summary>
    public static DateTimeOffset RequireUtc(DateTimeOffset value) =>
        value.Offset == TimeSpan.Zero
            ? value
            : throw new UtcOnlyException(value);

    /// <summary>
    /// The open type's undeclared properties as a JSON object.
    ///
    /// There is no other shape a relational column could take, and the consequence is that a dynamic
    /// property can never appear in <c>$filter</c> or <c>$orderby</c> - it is opaque to SQL.
    /// </summary>
    public static ValueConverter<IDictionary<string, object?>, string> DynamicProperties() =>
        new(value => JsonSerializer.Serialize(value, JsonOptions), json => ReadDictionary(json));

    public static ValueComparer<IDictionary<string, object?>> DynamicPropertiesComparer() =>
        new(
            (left, right) => JsonSerializer.Serialize(left, JsonOptions) == JsonSerializer.Serialize(right, JsonOptions),
            value => JsonSerializer.Serialize(value, JsonOptions).GetHashCode(),
            value => ReadDictionary(JsonSerializer.Serialize(value, JsonOptions)));

    /// <summary>The <c>Edm.Untyped</c> property, same story as the dynamic ones and same cost.</summary>
    public static ValueConverter<object?, string> Untyped() =>
        new(value => JsonSerializer.Serialize(value, JsonOptions), json => Unwrap(JsonDocument.Parse(json).RootElement));

    public static ValueComparer<object?> UntypedComparer() =>
        new(
            (left, right) => JsonSerializer.Serialize(left, JsonOptions) == JsonSerializer.Serialize(right, JsonOptions),
            value => value == null ? 0 : JsonSerializer.Serialize(value, JsonOptions).GetHashCode(),
            value => value);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private static IDictionary<string, object?> ReadDictionary(string json)
    {
        var result = new Dictionary<string, object?>();
        using var document = JsonDocument.Parse(json);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            result[property.Name] = Unwrap(property.Value);
        }

        return result;
    }

    /// <summary>
    /// Turns a <see cref="JsonElement" /> back into the CLR value it came from.
    ///
    /// Not cosmetic: OData writes an <c>@odata.type</c> annotation for a dynamic property whose type is
    /// not the default, so handing it a <see cref="JsonElement" /> - or widening every whole number to
    /// <c>long</c> - would change the payload the seed data produces. Narrowing to <c>int</c> where the
    /// value fits keeps it byte-for-byte what the in-memory store emitted.
    /// </summary>
    private static object? Unwrap(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => element.GetString(),
            // Each branch is boxed on its own. Without the casts the conditional has branches of type
            // int, long and double, so C# gives the whole expression the common type double - and every
            // whole number would come back boxed as a double, changing `12500` into `12500.0` in the
            // payload of the open type.
            JsonValueKind.Number => element.TryGetInt32(out var i) ? (object)i
                : element.TryGetInt64(out var l) ? (object)l
                : element.GetDouble(),
            JsonValueKind.Array => element.EnumerateArray().Select(Unwrap).ToList(),
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => Unwrap(p.Value)),
            _ => element.ToString(),
        };
}
