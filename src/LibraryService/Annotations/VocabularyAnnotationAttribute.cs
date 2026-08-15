namespace LibraryService.Annotations;

/// <summary>
/// Base of every OData vocabulary annotation that can be declared on the data model.
///
/// An attribute carries nothing but a term name, a raw CLR value and an optional qualifier - it does
/// *not* know how the value has to look in CSDL. That is decided by <see cref="AnnotationEmitter" />,
/// which reads the term's declared type out of the OASIS vocabulary embedded in Microsoft.OData.Edm and
/// builds the expression that type demands. Keeping the shape decision in one type-directed place is what
/// makes an enum-typed term come out as <c>EnumMember</c> rather than as a record - see IMPLEMENTATION.md.
/// </summary>
/// <remarks>
/// The attribute targets are deliberately wide: which model elements a term may be applied to is stated
/// by the vocabulary itself (<c>AppliesTo</c>) and enforced by the emitter at startup, for annotations
/// written as attributes and for those written fluently alike. Repeating those lists as
/// <see cref="AttributeUsageAttribute" /> per term would duplicate the vocabulary and let the two drift.
/// </remarks>
[AttributeUsage(
    AttributeTargets.Class
        | AttributeTargets.Struct
        | AttributeTargets.Enum
        | AttributeTargets.Property
        | AttributeTargets.Field
        | AttributeTargets.Parameter,
    AllowMultiple = true)]
public abstract class VocabularyAnnotationAttribute : Attribute
{
    protected VocabularyAnnotationAttribute(string term, object? value)
    {
        Term = term;
        Value = value;
    }

    /// <summary>Fully qualified term name, e.g. <c>Org.OData.Core.V1.Computed</c>.</summary>
    public string Term { get; }

    /// <summary>
    /// The value as written in C#. <c>null</c> means the annotation has no value expression, which is
    /// legal only for a term the emitter can default (it never is, for the terms modelled here).
    /// </summary>
    public object? Value { get; }

    /// <summary>
    /// Optional qualifier. Two annotations of the same term on the same target are an error unless they
    /// differ in their qualifier - which is exactly what a qualifier is for.
    /// </summary>
    public string? Qualifier { get; set; }
}

/// <summary>
/// Escape hatch for any term without an attribute of its own - every record-valued term, and the handful
/// of terms whose target has no counterpart in this model. The value is passed through to the same
/// type-directed translation, so a term is not restricted to a shape by being written this way; what it
/// loses is the compile-time name and the argument type.
/// </summary>
/// <example><c>[Annotation("Org.OData.Core.V1.LongDescription", "The library's media")]</c></example>
public sealed class Annotation(string term, object? value) : VocabularyAnnotationAttribute(term, value);
