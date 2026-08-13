using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Spatial;

namespace LibraryService.Data;

/// <summary>
/// The three places where the reference model asks for something SQLite cannot hold directly. Each is a
/// distortion, so each is written down here rather than spread over <see cref="LibraryContext" /> - and
/// each is reported in FEATURE-COVERAGE.md under what the persistence layer costs.
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
    /// <c>decimal</c> as an integer scaled by the declared <c>Scale</c>.
    ///
    /// SQLite has no decimal type. EF's default is to store one as TEXT, which compares and orders
    /// lexically - so <c>$orderby Balance</c> and <c>$filter Balance gt 10</c> would return wrong results
    /// rather than fail, the worst outcome for a server a test suite asserts against. Scaling to an
    /// integer keeps the value exact and makes both work, and turns the <c>Precision</c>/<c>Scale</c>
    /// facets already declared in <see cref="EdmModelBuilder" /> into this converter's contract.
    ///
    /// EF applies the converter to query constants as well, which is what makes the comparison correct
    /// and not merely storable.
    /// </summary>
    public static ValueConverter<decimal, long> ScaledDecimal(int scale)
    {
        var factor = (decimal)Math.Pow(10, scale);

        // One unit at the declared scale - 0.01m for scale 2 - reconstructed rather than written out, so
        // the scale is taken from the argument and not from a literal.
        //
        // Multiplied, not divided, on the way back: `1250m / 100m` is 12.5m, because division normalises
        // the scale away, while `1250m * 0.01m` is 12.50m - a decimal's scale is part of its value in .NET
        // and survives multiplication. That is what keeps the payload showing the Scale=2 the reference
        // model declares, instead of dropping the trailing zero.
        var unit = new decimal(1, 0, 0, false, (byte)scale);

        return new ValueConverter<decimal, long>(
            value => (long)decimal.Round(value * factor, 0, MidpointRounding.AwayFromZero),
            stored => stored * unit);
    }

    /// <summary>
    /// <c>DateTimeOffset</c> as the tick count of the instant it names, in UTC.
    ///
    /// EF's SQLite provider stores one as text and then cannot translate a comparison against it at all:
    /// <c>$filter LoanedAt gt 2020-01-01T00:00:00Z</c> and <c>$orderby LoanedAt</c> both failed to
    /// translate. What made that dangerous rather than merely missing is *how* they failed - see
    /// FEATURE-COVERAGE.md - so the fix is to store something SQLite can order: an integer.
    ///
    /// The cost is the offset. Only the instant survives, so a value written as <c>+02:00</c> reads back
    /// as the same moment in UTC. Every timestamp in the reference model is UTC, so nothing in the seed
    /// data changes shape.
    /// </summary>
    public static ValueConverter<DateTimeOffset, long> UtcTicks() =>
        new(value => value.UtcTicks, stored => new DateTimeOffset(stored, TimeSpan.Zero));

    /// <summary>
    /// <c>TimeSpan</c> as ticks, for the same reason: as text a duration compares lexically, and EF
    /// refuses to translate the comparison rather than getting it wrong.
    /// </summary>
    public static ValueConverter<TimeSpan, long> DurationTicks() =>
        new(value => value.Ticks, stored => TimeSpan.FromTicks(stored));

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
