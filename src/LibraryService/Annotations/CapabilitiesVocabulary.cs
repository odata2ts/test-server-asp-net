namespace LibraryService.Annotations;

/// <summary>
/// <c>Org.OData.Capabilities.V1</c>, the terms with a simple value - the tags and enums that state a
/// capability outright. The vocabulary's 21 <c>*Restrictions</c> / <c>*Support</c> terms all hold records
/// and stay in <c>EdmModelBuilder</c> or go through <see cref="Annotation" />.
///
/// Almost every term here targets an entity set, a collection or the entity container, so these are
/// written fluently (<c>.Annotate(...)</c> / <c>.AnnotateContainer(...)</c>) rather than as attributes.
/// </summary>
public static class Capabilities
{
    private const string Ns = "Org.OData.Capabilities.V1.";

    /// <summary>Mirrors <c>Capabilities.ConformanceLevelType</c>.</summary>
    public enum ConformanceLevelType
    {
        Minimal,
        Intermediate,
        Advanced,
    }

    /// <summary>Mirrors <c>Capabilities.IsolationLevel</c>.</summary>
    [Flags]
    public enum IsolationLevel
    {
        Snapshot = 1,
    }

    // --- entity set / collection -------------------------------------------------------------------

    /// <summary>Entities can be addressed by their key.</summary>
    public sealed class IndexableByKey(bool indexable = true)
        : VocabularyAnnotationAttribute(Ns + "IndexableByKey", indexable);

    /// <summary><c>$top</c> is supported.</summary>
    public sealed class TopSupported(bool supported = true)
        : VocabularyAnnotationAttribute(Ns + "TopSupported", supported);

    /// <summary><c>$skip</c> is supported.</summary>
    public sealed class SkipSupported(bool supported = true)
        : VocabularyAnnotationAttribute(Ns + "SkipSupported", supported);

    /// <summary><c>$compute</c> is supported.</summary>
    public sealed class ComputeSupported(bool supported = true)
        : VocabularyAnnotationAttribute(Ns + "ComputeSupported", supported);

    /// <summary><c>$filter</c> functions supported beyond the required minimum.</summary>
    public sealed class FilterFunctions(params string[] functions)
        : VocabularyAnnotationAttribute(Ns + "FilterFunctions", functions);

    // --- entity type / property --------------------------------------------------------------------

    /// <summary>The media stream's edit URL can be updated.</summary>
    public sealed class MediaLocationUpdateSupported(bool supported = true)
        : VocabularyAnnotationAttribute(Ns + "MediaLocationUpdateSupported", supported);

    // --- entity container --------------------------------------------------------------------------

    /// <summary>The conformance level the service claims.</summary>
    public sealed class ConformanceLevel(ConformanceLevelType level)
        : VocabularyAnnotationAttribute(Ns + "ConformanceLevel", level);

    /// <summary>Media types the service accepts and returns, e.g. <c>application/json</c>.</summary>
    public sealed class SupportedFormats(params string[] formats)
        : VocabularyAnnotationAttribute(Ns + "SupportedFormats", formats);

    /// <summary>Media types <c>$metadata</c> is available in.</summary>
    public sealed class SupportedMetadataFormats(params string[] formats)
        : VocabularyAnnotationAttribute(Ns + "SupportedMetadataFormats", formats);

    /// <summary>Content encodings the service accepts, e.g. <c>gzip</c>.</summary>
    public sealed class AcceptableEncodings(params string[] encodings)
        : VocabularyAnnotationAttribute(Ns + "AcceptableEncodings", encodings);

    /// <summary>Asynchronous requests (<c>Prefer: respond-async</c>) are supported.</summary>
    public sealed class AsynchronousRequestsSupported(bool supported = true)
        : VocabularyAnnotationAttribute(Ns + "AsynchronousRequestsSupported", supported);

    /// <summary><c>continue-on-error</c> is supported inside a <c>$batch</c>.</summary>
    public sealed class BatchContinueOnErrorSupported(bool supported = true)
        : VocabularyAnnotationAttribute(Ns + "BatchContinueOnErrorSupported", supported);

    /// <summary><c>$batch</c> is supported.</summary>
    public sealed class BatchSupported(bool supported = true)
        : VocabularyAnnotationAttribute(Ns + "BatchSupported", supported);

    /// <summary>The isolation level a client may ask for with <c>Isolation: snapshot</c>.</summary>
    public sealed class IsolationSupported(IsolationLevel level)
        : VocabularyAnnotationAttribute(Ns + "IsolationSupported", level);

    /// <summary><c>$crossjoin</c> is supported.</summary>
    public sealed class CrossJoinSupported(bool supported = true)
        : VocabularyAnnotationAttribute(Ns + "CrossJoinSupported", supported);

    /// <summary>Keys may be addressed as their own path segment instead of in parentheses.</summary>
    public sealed class KeyAsSegmentSupported(bool supported = true)
        : VocabularyAnnotationAttribute(Ns + "KeyAsSegmentSupported", supported);

    /// <summary>Query options may be passed in a <c>$query</c> segment of the request body.</summary>
    public sealed class QuerySegmentSupported(bool supported = true)
        : VocabularyAnnotationAttribute(Ns + "QuerySegmentSupported", supported);

    /// <summary>Annotation values may be used inside query options.</summary>
    public sealed class AnnotationValuesInQuerySupported(bool supported = true)
        : VocabularyAnnotationAttribute(Ns + "AnnotationValuesInQuerySupported", supported);
}

/// <summary>
/// <c>Org.OData.Community.V1</c>. The vocabulary declares exactly one term, and it has a simple value.
/// </summary>
public static class Community
{
    /// <summary>The function may be used as a URL escape function, i.e. called without its name.</summary>
    public sealed class UrlEscapeFunction(bool isEscapeFunction = true)
        : VocabularyAnnotationAttribute("Org.OData.Community.V1.UrlEscapeFunction", isEscapeFunction);
}
