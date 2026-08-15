using System.Runtime.CompilerServices;
using Microsoft.OData.Edm;
using Microsoft.OData.ModelBuilder;

namespace LibraryService.Annotations;

/// <summary>
/// The fluent half of the mechanism, for everything that has no CLR declaration to hang an attribute on:
/// entity sets, singletons, operations, their parameters and the entity container. Those are all built by
/// name in <see cref="LibraryService.EdmModelBuilder" />, so their annotations are written where they are
/// declared - which keeps the set name out of a string that could go stale.
///
/// The annotations are parked against the configuration object itself and collected by
/// <see cref="AnnotationEmitter" /> after the model is built. The attributes are the same ones the model
/// classes use, so there is one vocabulary surface and one translation, not two.
/// </summary>
public static class AnnotationExtensions
{
    /// <summary>
    /// Keyed by configuration instance and weak, so nothing outlives the builder it was written against -
    /// a plain static dictionary would keep every model ever built alive.
    /// </summary>
    private static readonly ConditionalWeakTable<object, List<VocabularyAnnotationAttribute>> Attached = new();

    /// <summary>
    /// Annotates an entity set, a singleton, an operation, an operation parameter, or - for a caller that
    /// has no CLR declaration to put an attribute on, such as <see cref="EfCoreTranslation" /> - a type or
    /// a property.
    /// </summary>
    /// <remarks>
    /// One generic method rather than one overload per configuration type, because overloads that differ
    /// only in their generic constraint are not overloads at all in C# - and a non-generic signature would
    /// return the base configuration type and break the fluent chain. What the type system cannot state
    /// here is checked at the first call instead.
    /// </remarks>
    public static TConfiguration Annotate<TConfiguration>(
        this TConfiguration configuration,
        params VocabularyAnnotationAttribute[] annotations)
        where TConfiguration : class
    {
        if (configuration is not (NavigationSourceConfiguration
            or OperationConfiguration
            or ParameterConfiguration
            or StructuralTypeConfiguration
            or PropertyConfiguration))
        {
            throw new ArgumentException(
                "Annotate() applies to an entity set, a singleton, an operation, a parameter, a type or a "
                    + $"property, not to a {configuration.GetType().Name}. The entity container has "
                    + "AnnotateContainer().",
                nameof(configuration));
        }

        Attach(configuration, annotations);
        return configuration;
    }

    /// <summary>
    /// Declares an entity set and hands back the configuration <see cref="Annotate{T}" /> attaches to.
    /// </summary>
    /// <remarks>
    /// A library trap, not a preference: <c>builder.EntitySet&lt;T&gt;(name)</c> returns the *generic*
    /// <c>EntitySetConfiguration&lt;T&gt;</c>, a wrapper that keeps the real configuration behind an
    /// internal property - so an annotation attached to the wrapper could never be matched to the entity
    /// set the builder actually holds. <c>AddEntitySet</c> is the same declaration one level down and
    /// returns the configuration itself. It is idempotent, so a set may still be declared with
    /// <c>EntitySet&lt;T&gt;()</c> elsewhere; both calls end up at the same object.
    /// </remarks>
    public static EntitySetConfiguration AnnotatableEntitySet<TEntity>(this ODataModelBuilder builder, string name)
        where TEntity : class =>
        builder.AddEntitySet(name, builder.AddEntityType(typeof(TEntity)));

    /// <summary>Declares a singleton and hands back its configuration; see
    /// <see cref="AnnotatableEntitySet{TEntity}" /> for why the generic overload will not do.</summary>
    public static SingletonConfiguration AnnotatableSingleton<TEntity>(this ODataModelBuilder builder, string name)
        where TEntity : class =>
        builder.AddSingleton(name, builder.AddEntityType(typeof(TEntity)));

    /// <summary>
    /// Annotates the entity container - the target of the service-wide capability terms such as
    /// <c>Capabilities.ConformanceLevel</c> or <c>Capabilities.BatchSupported</c>.
    /// </summary>
    public static TBuilder AnnotateContainer<TBuilder>(
        this TBuilder builder,
        params VocabularyAnnotationAttribute[] annotations)
        where TBuilder : ODataModelBuilder
    {
        Attach(builder, annotations);
        return builder;
    }

    /// <summary>Everything written against <paramref name="configuration" />, in declaration order.</summary>
    internal static IEnumerable<VocabularyAnnotationAttribute> AttachedTo(object configuration) =>
        Attached.TryGetValue(configuration, out var annotations) ? annotations : [];

    private static void Attach(object configuration, VocabularyAnnotationAttribute[] annotations) =>
        Attached.GetOrCreateValue(configuration).AddRange(annotations);
}

/// <summary>
/// Applies the declared annotations to a model the builder has just produced. Kept as an extension so the
/// call reads as the last step of building.
/// </summary>
public static class AnnotatedModelExtensions
{
    /// <summary>
    /// Builds the model and writes every declared vocabulary annotation onto it. Replaces a plain
    /// <c>GetEdmModel()</c>; calling that directly yields a model without any of the annotations.
    /// </summary>
    public static IEdmModel GetAnnotatedEdmModel(this ODataModelBuilder builder) =>
        AnnotationEmitter.Apply(builder.GetEdmModel(), builder);
}
