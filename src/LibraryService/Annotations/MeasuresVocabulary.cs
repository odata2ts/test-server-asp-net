namespace LibraryService.Annotations;

/// <summary>
/// <c>Org.OData.Measures.V1</c>. All five terms of the vocabulary have a simple value, so it is covered
/// completely.
/// </summary>
public static class Measures
{
    private const string Ns = "Org.OData.Measures.V1.";

    /// <summary>
    /// Values of <c>Measures.DurationGranularityType</c>. The term is a type definition over
    /// <c>Edm.String</c> with a fixed set of allowed values, not an enum - hence the lower-cased name.
    /// </summary>
    public enum DurationGranularityType
    {
        Days,
        Hours,
        Minutes,
    }

    /// <summary>ISO 4217 currency code of a monetary amount, e.g. <c>"EUR"</c>.</summary>
    public sealed class ISOCurrency(string currencyCode)
        : VocabularyAnnotationAttribute(Ns + "ISOCurrency", currencyCode);

    /// <summary>Number of significant decimals of a monetary or measured amount.</summary>
    public sealed class Scale(byte scale) : VocabularyAnnotationAttribute(Ns + "Scale", scale);

    /// <summary>Unit of measure of the annotated value, e.g. <c>"cm"</c>.</summary>
    public sealed class Unit(string unit) : VocabularyAnnotationAttribute(Ns + "Unit", unit);

    /// <summary>Unit of measure as a UN/ECE Recommendation 20 code, e.g. <c>"CMT"</c>.</summary>
    public sealed class UNECEUnit(string unitCode)
        : VocabularyAnnotationAttribute(Ns + "UNECEUnit", unitCode);

    /// <summary>The granularity an <c>Edm.Duration</c> value is meaningful at.</summary>
    public sealed class DurationGranularity(DurationGranularityType granularity)
        : VocabularyAnnotationAttribute(Ns + "DurationGranularity", granularity.ToString().ToLowerInvariant());
}
