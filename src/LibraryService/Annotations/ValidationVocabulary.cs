namespace LibraryService.Annotations;

/// <summary>
/// <c>Org.OData.Validation.V1</c>, the terms with a simple value. <c>AllowedValues</c>,
/// <c>Constraint</c> and <c>ItemsOf</c> hold records and are reachable through
/// <see cref="Annotation" />; <c>Exclusive</c>, <c>AllowedTerms</c> and <c>ApplicableTerms</c>
/// annotate an annotation or a term and have no target in this model at all.
/// </summary>
public static class Validation
{
    private const string Ns = "Org.OData.Validation.V1.";

    /// <summary>Regular expression the value has to match (ECMAScript syntax).</summary>
    public sealed class Pattern(string pattern)
        : VocabularyAnnotationAttribute(Ns + "Pattern", pattern);

    /// <summary>
    /// Smallest allowed value. The term is typed <c>Edm.PrimitiveType</c>, so the argument is the value
    /// itself and its CLR type decides the constant that is emitted.
    /// </summary>
    public sealed class Minimum(object minimum)
        : VocabularyAnnotationAttribute(Ns + "Minimum", minimum);

    /// <summary>Largest allowed value; see <see cref="Minimum" /> on the argument's type.</summary>
    public sealed class Maximum(object maximum)
        : VocabularyAnnotationAttribute(Ns + "Maximum", maximum);

    /// <summary>
    /// The value has to be a multiple of this number. Declared <c>Edm.Decimal</c>, but a C# attribute
    /// argument cannot be a <c>decimal</c> - so it is written as a <c>double</c> and converted.
    /// </summary>
    public sealed class MultipleOf(double multipleOf)
        : VocabularyAnnotationAttribute(Ns + "MultipleOf", multipleOf);

    /// <summary>Maximum number of items in the annotated collection.</summary>
    public sealed class MaxItems(long maxItems)
        : VocabularyAnnotationAttribute(Ns + "MaxItems", maxItems);

    /// <summary>Minimum number of items in the annotated collection.</summary>
    public sealed class MinItems(long minItems)
        : VocabularyAnnotationAttribute(Ns + "MinItems", minItems);

    /// <summary>The derived types actually allowed here, narrower than the declared type.</summary>
    public sealed class DerivedTypeConstraint(params string[] qualifiedTypeNames)
        : VocabularyAnnotationAttribute(Ns + "DerivedTypeConstraint", qualifiedTypeNames);

    /// <summary>The types a dynamic property of an open type may have.</summary>
    public sealed class OpenPropertyTypeConstraint(params string[] qualifiedTypeNames)
        : VocabularyAnnotationAttribute(Ns + "OpenPropertyTypeConstraint", qualifiedTypeNames);
}
