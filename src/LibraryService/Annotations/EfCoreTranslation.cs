using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.OData.ModelBuilder;

namespace LibraryService.Annotations;

/// <summary>
/// Carries over what the EF Core model already states and OData has a word for.
///
/// The two layers overlap: EF says a column is a concurrency token, has a precision, cascades on delete or
/// carries a comment, and OData has <c>Core.OptimisticConcurrency</c>, the <c>Precision</c>/<c>Scale</c>
/// facets, <c>OnDelete</c> and <c>Core.Description</c> for exactly those facts. Restating them by hand
/// means two sources that drift - which is what the note beside <c>Member.Balance</c> in
/// <see cref="Data.LibraryContext" /> used to warn about. Here the persistence model is the source and the
/// EDM follows.
///
/// The overlap is *narrower than it looks*, and what is deliberately not translated matters as much as
/// what is: EF's <c>ValueGenerated</c> and <c>GetDefaultValue()</c> look like
/// <c>Core.ComputedDefaultValue</c> and <c>DefaultValue</c> and are neither. See IMPLEMENTATION.md.
///
/// Runs from <c>ODataConventionModelBuilder.OnModelCreating</c> - after the conventions, before the model
/// is built. Both halves of that matter: earlier, and the convention builder has not discovered the
/// properties yet, so there is nothing to configure; later, and <c>Precision</c> and
/// <c>IsConcurrencyToken</c> can no longer be set at all.
/// </summary>
internal static class EfCoreTranslation
{
    /// <summary>Applies every translated fact of <paramref name="efModel" /> to <paramref name="builder" />.</summary>
    public static void Apply(ODataModelBuilder builder, IModel efModel)
    {
        foreach (var type in builder.StructuralTypes)
        {
            if (efModel.FindEntityType(type.ClrType) is not { } efType)
            {
                continue;
            }

            // A table comment describes the entity, which is what Core.Description is for.
            if (efType.GetComment() is { Length: > 0 } typeComment)
            {
                type.Annotate(new Core.Description(typeComment));
            }

            foreach (var property in type.Properties.Where(p => p.PropertyInfo is not null))
            {
                if (efType.FindProperty(property.PropertyInfo.Name) is { } efProperty)
                {
                    Translate(property, efProperty);
                }
            }

            foreach (var navigation in type.NavigationProperties)
            {
                Translate(navigation, efType.FindNavigation(navigation.Name));
            }
        }
    }

    private static void Translate(PropertyConfiguration property, IProperty efProperty)
    {
        // One HasComment produces both the COMMENT ON COLUMN in db/01-schema.sql and this.
        if (efProperty.GetComment() is { Length: > 0 } comment)
        {
            property.Annotate(new Core.Description(comment));
        }

        // Only where EF was *told* a precision: the design-time model answers null unless HasPrecision or
        // [Precision] was used, so a column that merely happens to be numeric acquires no facet.
        if (property is PrecisionPropertyConfiguration precision && efProperty.GetPrecision() is { } digits)
        {
            precision.Precision = digits;
        }

        if (property is DecimalPropertyConfiguration @decimal && efProperty.GetScale() is { } scale)
        {
            @decimal.Scale = scale;
        }

        // Both stacks read [ConcurrencyCheck], so this changes nothing for a property using the attribute.
        // What it adds is EF's fluent IsConcurrencyToken(), which the OData conventions cannot see - and
        // setting it here rather than writing the annotation afterwards is what keeps @odata.etag working.
        if (efProperty.IsConcurrencyToken && property is PrimitivePropertyConfiguration primitive)
        {
            primitive.IsConcurrencyToken();
        }
    }

    /// <summary>
    /// Turns EF's <see cref="DeleteBehavior.Cascade" /> into the CSDL <c>OnDelete</c>.
    /// </summary>
    /// <remarks>
    /// Only on the principal side. A foreign key's delete behaviour belongs to the key, so *both* of its
    /// navigations report it, while <c>OnDelete</c> on a navigation property means "deleting the entity
    /// that declares this navigation deletes what it points at". Applied to the dependent's reference back
    /// to its principal it would say that deleting a <c>Copy</c> deletes the <c>Medium</c> - which is the
    /// opposite of what the database does.
    ///
    /// <c>Cascade</c> is also the only behaviour there is an API for. EF's <c>SetNull</c> - which this
    /// model uses five times - has a CSDL action of its own that
    /// <c>NavigationPropertyConfiguration</c> cannot express.
    /// </remarks>
    private static void Translate(NavigationPropertyConfiguration navigation, INavigation? efNavigation)
    {
        if (efNavigation is { IsOnDependent: false, ForeignKey.DeleteBehavior: DeleteBehavior.Cascade })
        {
            navigation.CascadeOnDelete();
        }
    }
}
