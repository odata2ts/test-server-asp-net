namespace LibraryService.Annotations;

/// <summary>
/// <c>Org.OData.Core.V1</c>. One attribute per term whose value is a primitive, an enum, a tag or a
/// collection of primitives *and* whose target exists in this model; record-valued terms of this
/// vocabulary (<c>Revisions</c>, <c>Links</c>, <c>Example</c>, <c>Messages</c>, <c>AlternateKeys</c>, the
/// exception types) and the container-level terms (<c>ODataVersions</c>, <c>DereferenceableIDs</c>,
/// <c>ConventionalIDs</c>) are reachable through <see cref="Annotation" />.
/// </summary>
public static class Core
{
    private const string Ns = "Org.OData.Core.V1.";

    /// <summary>Mirrors the <c>Core.Permission</c> flags enum; the emitter maps it back by member name.</summary>
    [Flags]
    public enum Permission
    {
        None = 0,
        Read = 1,
        Write = 2,
        ReadWrite = Read | Write,
        Invoke = 4,
    }

    // --- documentation -----------------------------------------------------------------------------

    /// <summary>A short, human-readable description.</summary>
    public sealed class Description(string description)
        : VocabularyAnnotationAttribute(Ns + "Description", description);

    /// <summary>A longer human-readable description.</summary>
    public sealed class LongDescription(string description)
        : VocabularyAnnotationAttribute(Ns + "LongDescription", description);

    /// <summary>The service-internal name of a model element.</summary>
    public sealed class SymbolicName(string name)
        : VocabularyAnnotationAttribute(Ns + "SymbolicName", name);

    // --- managed properties ------------------------------------------------------------------------

    /// <summary>Value is computed by the server and cannot be written by a client.</summary>
    public sealed class Computed(bool computed = true)
        : VocabularyAnnotationAttribute(Ns + "Computed", computed);

    /// <summary>Default value is computed by the server; the client may still supply one on insert.</summary>
    public sealed class ComputedDefaultValue(bool computed = true)
        : VocabularyAnnotationAttribute(Ns + "ComputedDefaultValue", computed);

    /// <summary>Value may be set on insert and never changed afterwards.</summary>
    public sealed class Immutable(bool immutable = true)
        : VocabularyAnnotationAttribute(Ns + "Immutable", immutable);

    /// <summary>Permissions available to the client, e.g. <c>Permission.Read</c> for read-only.</summary>
    public sealed class Permissions(Permission permissions)
        : VocabularyAnnotationAttribute(Ns + "Permissions", permissions);

    /// <summary>Property paths whose values take part in optimistic-concurrency control.</summary>
    public sealed class OptimisticConcurrency(params string[] propertyPaths)
        : VocabularyAnnotationAttribute(Ns + "OptimisticConcurrency", propertyPaths);

    // --- media and URLs ----------------------------------------------------------------------------

    /// <summary>The value is a URL.</summary>
    public sealed class IsURL(bool isUrl = true) : VocabularyAnnotationAttribute(Ns + "IsURL", isUrl);

    /// <summary>The media type of a stream or binary value, e.g. <c>application/pdf</c>.</summary>
    public sealed class MediaType(string mediaType)
        : VocabularyAnnotationAttribute(Ns + "MediaType", mediaType);

    /// <summary>Media types acceptable for a stream or binary value.</summary>
    public sealed class AcceptableMediaTypes(params string[] mediaTypes)
        : VocabularyAnnotationAttribute(Ns + "AcceptableMediaTypes", mediaTypes);

    /// <summary>The value itself is an IANA media type.</summary>
    public sealed class IsMediaType(bool isMediaType = true)
        : VocabularyAnnotationAttribute(Ns + "IsMediaType", isMediaType);

    // --- structure ---------------------------------------------------------------------------------

    /// <summary>Instances may carry properties beyond those declared.</summary>
    public sealed class AdditionalProperties(bool additionalProperties = true)
        : VocabularyAnnotationAttribute(Ns + "AdditionalProperties", additionalProperties);

    /// <summary>Instances may carry any structure at all; only for genuinely untyped values.</summary>
    public sealed class AnyStructure(bool anyStructure = true)
        : VocabularyAnnotationAttribute(Ns + "AnyStructure", anyStructure);

    /// <summary>The type may implement the listed types, without declaring them as base types.</summary>
    public sealed class MayImplement(params string[] qualifiedTypeNames)
        : VocabularyAnnotationAttribute(Ns + "MayImplement", qualifiedTypeNames);

    /// <summary>Expanded by default, without the client asking with <c>$expand</c>.</summary>
    public sealed class AutoExpand(bool autoExpand = true)
        : VocabularyAnnotationAttribute(Ns + "AutoExpand", autoExpand);

    /// <summary>Expanded as entity references by default.</summary>
    public sealed class AutoExpandReferences(bool autoExpand = true)
        : VocabularyAnnotationAttribute(Ns + "AutoExpandReferences", autoExpand);

    /// <summary>The collection has a stable order that the client may rely on.</summary>
    public sealed class Ordered(bool ordered = true)
        : VocabularyAnnotationAttribute(Ns + "Ordered", ordered);

    /// <summary>Members may be inserted at a given position in the collection.</summary>
    public sealed class PositionalInsert(bool positionalInsert = true)
        : VocabularyAnnotationAttribute(Ns + "PositionalInsert", positionalInsert);

    /// <summary>The value depends on the language requested by the client.</summary>
    public sealed class IsLanguageDependent(bool languageDependent = true)
        : VocabularyAnnotationAttribute(Ns + "IsLanguageDependent", languageDependent);

    // --- operations --------------------------------------------------------------------------------

    /// <summary>Whether the operation can be invoked at all.</summary>
    public sealed class OperationAvailable(bool available = true)
        : VocabularyAnnotationAttribute(Ns + "OperationAvailable", available);

    /// <summary>The bound operation applies only where it is bound explicitly.</summary>
    public sealed class RequiresExplicitBinding(bool requiresExplicitBinding = true)
        : VocabularyAnnotationAttribute(Ns + "RequiresExplicitBinding", requiresExplicitBinding);

    /// <summary>Bound operations that are available on the annotated target.</summary>
    public sealed class ExplicitOperationBindings(params string[] qualifiedOperationNames)
        : VocabularyAnnotationAttribute(Ns + "ExplicitOperationBindings", qualifiedOperationNames);

    /// <summary>Parameter or return type is a delta payload.</summary>
    public sealed class IsDelta(bool isDelta = true)
        : VocabularyAnnotationAttribute(Ns + "IsDelta", isDelta);

    // --- resources ---------------------------------------------------------------------------------

    /// <summary>The path of the annotated resource, relative to the service root.</summary>
    public sealed class ResourcePath(string path)
        : VocabularyAnnotationAttribute(Ns + "ResourcePath", path);
}
