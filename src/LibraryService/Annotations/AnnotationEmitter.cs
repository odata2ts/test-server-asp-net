using System.Collections;
using System.Reflection;
using Microsoft.OData.Edm;
using Microsoft.OData.Edm.Csdl;
using Microsoft.OData.Edm.Vocabularies;
using Microsoft.OData.Edm.Vocabularies.Community.V1;
using Microsoft.OData.Edm.Vocabularies.Measures.V1;
using Microsoft.OData.Edm.Vocabularies.V1;
using Microsoft.OData.ModelBuilder;

namespace LibraryService.Annotations;

/// <summary>
/// Turns the <see cref="VocabularyAnnotationAttribute" />s declared on the model classes - and those
/// attached fluently to entity sets, singletons, operations, parameters and the container - into real
/// vocabulary annotations on the built <see cref="EdmModel" />.
///
/// The translation is *type-directed*: for every term the emitter looks up the declaration in the OASIS
/// vocabulary that Microsoft.OData.Edm carries as an <see cref="IEdmModel" /> of its own, and builds the
/// expression that term's declared type demands - a <c>Bool</c> for a tag, an <c>EnumMember</c> for an
/// enum-typed term, a <c>PropertyPath</c> for a path, a <c>Collection</c> of those for a collection.
/// This is the whole reason for not going through the model builder's own
/// <c>VocabularyTermConfiguration</c>, which wraps every value in a record - see IMPLEMENTATION.md.
///
/// Anything the emitter cannot make sense of throws, and throwing here means the service does not start:
/// a silently dropped annotation would be a wrong <c>$metadata</c>, and this server is read as
/// documentation of what the library emits.
/// </summary>
internal static class AnnotationEmitter
{
    /// <summary>
    /// The vocabularies terms are resolved against. All of them ship inside Microsoft.OData.Edm, so a term
    /// name is checked against the real declaration rather than against a list maintained here.
    /// </summary>
    private static readonly IEdmModel[] Vocabularies =
    [
        CoreVocabularyModel.Instance,
        CapabilitiesVocabularyModel.Instance,
        MeasuresVocabularyModel.Instance,
        ValidationVocabularyModel.Instance,
        CommunityVocabularyModel.Instance,
        AlternateKeysVocabularyModel.Instance,
        AuthorizationVocabularyModel.Instance,
    ];

    /// <summary>
    /// Applies every declared annotation to <paramref name="model" />. Call it once, directly after
    /// <c>GetEdmModel()</c>; the builder is still needed because it is the only place that knows which
    /// CLR type and which <see cref="PropertyInfo" /> a schema element came from.
    /// </summary>
    public static IEdmModel Apply(IEdmModel model, ODataModelBuilder builder)
    {
        var edm = (EdmModel)model;
        var emitted = new HashSet<(IEdmVocabularyAnnotatable Target, string Term, string? Qualifier)>();

        ApplyDeclaredOnTypes(edm, builder, emitted);
        ApplyDeclaredFluently(edm, builder, emitted);

        return edm;
    }

    // --- attributes on the model classes ---------------------------------------------------------

    private static void ApplyDeclaredOnTypes(
        EdmModel edm,
        ODataModelBuilder builder,
        HashSet<(IEdmVocabularyAnnotatable, string, string?)> emitted)
    {
        foreach (var type in builder.StructuralTypes)
        {
            if (edm.FindDeclaredType(type.FullName) is not IEdmStructuredType structured)
            {
                continue;
            }

            Emit(
                edm,
                (IEdmVocabularyAnnotatable)structured,
                Declared(type.ClrType).Concat(AnnotationExtensions.AttachedTo(type)),
                emitted,
                type.FullName);

            foreach (var property in type.Properties.Concat(type.NavigationProperties).DistinctBy(p => p.Name))
            {
                // A derived type's configuration can repeat an inherited property; the annotation belongs
                // on the type that declares it, and that type has its own configuration.
                if (structured.FindProperty(property.Name) is not { } edmProperty
                    || !ReferenceEquals(edmProperty.DeclaringType, structured))
                {
                    continue;
                }

                Emit(
                    edm,
                    edmProperty,
                    Declared(property.PropertyInfo).Concat(AnnotationExtensions.AttachedTo(property)),
                    emitted,
                    $"{type.FullName}/{property.Name}");
            }
        }

        foreach (var enumType in builder.EnumTypes)
        {
            if (edm.FindDeclaredType(enumType.FullName) is not IEdmEnumType edmEnum)
            {
                continue;
            }

            Emit(edm, edmEnum, Declared(enumType.ClrType), emitted, enumType.FullName);

            foreach (var member in edmEnum.Members)
            {
                var field = enumType.ClrType.GetField(member.Name, BindingFlags.Public | BindingFlags.Static);
                if (field is not null)
                {
                    Emit(edm, member, Declared(field), emitted, $"{enumType.FullName}/{member.Name}");
                }
            }
        }
    }

    private static IEnumerable<VocabularyAnnotationAttribute> Declared(MemberInfo? member) =>
        member?.GetCustomAttributes<VocabularyAnnotationAttribute>(inherit: false) ?? [];

    // --- fluently attached annotations -------------------------------------------------------------

    private static void ApplyDeclaredFluently(
        EdmModel edm,
        ODataModelBuilder builder,
        HashSet<(IEdmVocabularyAnnotatable, string, string?)> emitted)
    {
        var container = edm.EntityContainer
            ?? throw new InvalidOperationException("The model has no entity container.");

        Emit(edm, container, AnnotationExtensions.AttachedTo(builder), emitted, container.Name);

        foreach (var set in builder.EntitySets)
        {
            var target = container.FindEntitySet(set.Name)
                ?? throw new InvalidOperationException($"Entity set '{set.Name}' is not in the built model.");
            Emit(edm, target, AnnotationExtensions.AttachedTo(set), emitted, set.Name);
        }

        foreach (var singleton in builder.Singletons)
        {
            var target = container.FindSingleton(singleton.Name)
                ?? throw new InvalidOperationException($"Singleton '{singleton.Name}' is not in the built model.");
            Emit(edm, target, AnnotationExtensions.AttachedTo(singleton), emitted, singleton.Name);
        }

        foreach (var operation in builder.Operations)
        {
            var annotations = AnnotationExtensions.AttachedTo(operation).ToList();
            var parameters = operation.Parameters
                .Select(p => (Configuration: p, Annotations: AnnotationExtensions.AttachedTo(p).ToList()))
                .Where(p => p.Annotations.Count > 0)
                .ToList();

            if (annotations.Count == 0 && parameters.Count == 0)
            {
                continue;
            }

            var target = ResolveOperation(edm, operation);
            Emit(edm, target, annotations, emitted, operation.FullyQualifiedName);

            foreach (var (configuration, parameterAnnotations) in parameters)
            {
                var parameter = target.FindParameter(configuration.Name)
                    ?? throw new InvalidOperationException(
                        $"Parameter '{configuration.Name}' is not on operation '{operation.FullyQualifiedName}'.");
                Emit(
                    edm,
                    parameter,
                    parameterAnnotations,
                    emitted,
                    $"{operation.FullyQualifiedName}({configuration.Name})");
            }
        }
    }

    /// <summary>
    /// Finds the built operation a configuration produced. The reference model has two overload pairs
    /// (<c>Search</c>, <c>AvailableCopies</c>), so the fully qualified name alone does not identify one -
    /// the parameter names, binding parameter included, do.
    /// </summary>
    private static IEdmOperation ResolveOperation(EdmModel edm, OperationConfiguration configuration)
    {
        var candidates = edm.SchemaElements
            .OfType<IEdmOperation>()
            .Where(o => o.FullName() == configuration.FullyQualifiedName)
            .ToList();

        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        var parameterNames = configuration.Parameters.Select(p => p.Name).ToList();
        var matches = candidates
            .Where(o => o.Parameters.Select(p => p.Name).SequenceEqual(parameterNames))
            .ToList();

        return matches.Count == 1
            ? matches[0]
            : throw new InvalidOperationException(
                $"Operation '{configuration.FullyQualifiedName}' with parameters "
                    + $"({string.Join(", ", parameterNames)}) matched {matches.Count} of {candidates.Count} "
                    + "operations in the built model - annotations cannot be attached unambiguously.");
    }

    // --- emitting ----------------------------------------------------------------------------------

    private static void Emit(
        EdmModel edm,
        IEdmVocabularyAnnotatable target,
        IEnumerable<VocabularyAnnotationAttribute> annotations,
        HashSet<(IEdmVocabularyAnnotatable, string, string?)> emitted,
        string targetDescription)
    {
        foreach (var annotation in annotations)
        {
            var term = FindTerm(annotation.Term)
                ?? throw new InvalidOperationException(
                    $"Unknown vocabulary term '{annotation.Term}' on '{targetDescription}'. "
                        + "Only terms of the vocabularies shipped with Microsoft.OData.Edm can be emitted.");

            if (!AppliesTo(term, target))
            {
                throw new InvalidOperationException(
                    $"Term '{annotation.Term}' cannot be applied to '{targetDescription}': the vocabulary "
                        + $"declares AppliesTo=\"{term.AppliesTo}\", the target is {Symbol(target)}.");
            }

            if (!emitted.Add((target, annotation.Term, annotation.Qualifier)))
            {
                throw new InvalidOperationException(
                    $"Term '{annotation.Term}' is applied to '{targetDescription}' more than once with the "
                        + "same qualifier. Give the annotations different qualifiers or drop one.");
            }

            var expression = Translate(term.Type, annotation.Value, annotation.Term, targetDescription);
            var vocabularyAnnotation = new EdmVocabularyAnnotation(target, term, annotation.Qualifier, expression);
            vocabularyAnnotation.SetSerializationLocation(edm, EdmVocabularyAnnotationSerializationLocation.Inline);
            edm.AddVocabularyAnnotation(vocabularyAnnotation);
        }
    }

    private static IEdmTerm? FindTerm(string qualifiedName) =>
        Vocabularies.Select(v => v.FindTerm(qualifiedName)).FirstOrDefault(t => t is not null);

    // --- value translation -------------------------------------------------------------------------

    private static IEdmExpression Translate(
        IEdmTypeReference type,
        object? value,
        string term,
        string targetDescription)
    {
        if (value is null)
        {
            throw new InvalidOperationException($"Term '{term}' on '{targetDescription}' has no value.");
        }

        if (type.IsCollection())
        {
            var elementType = type.AsCollection().ElementType();
            if (value is not IEnumerable items || value is string)
            {
                throw new InvalidOperationException(
                    $"Term '{term}' on '{targetDescription}' is a collection, but the value is a "
                        + $"{value.GetType().Name}.");
            }

            return new EdmCollectionExpression(
                items.Cast<object>().Select(i => Translate(elementType, i, term, targetDescription)).ToArray());
        }

        return TranslateSingle(type, value, term, targetDescription);
    }

    private static IEdmExpression TranslateSingle(
        IEdmTypeReference type,
        object value,
        string term,
        string targetDescription)
    {
        switch (type.Definition)
        {
            // Core.Tag and the string type definitions (Core.QualifiedTypeName, Measures.
            // DurationGranularityType, ...) - what counts is the type they are defined over.
            case IEdmTypeDefinition definition:
                return Primitive(definition.UnderlyingType.PrimitiveKind, value, term, targetDescription);

            // The whole point of the exercise: an enum-typed term carries its value as EnumMember, not as
            // a record wrapping a property named after the term.
            case IEdmEnumType enumType:
                return new EdmEnumMemberExpression(Members(enumType, value, term, targetDescription));

            case IEdmPathType pathType:
                return Path(pathType, value, term, targetDescription);

            case IEdmPrimitiveType primitive:
                return Primitive(primitive.PrimitiveKind, value, term, targetDescription);

            default:
                throw new InvalidOperationException(
                    $"Term '{term}' on '{targetDescription}' is typed '{type.FullName()}', which this "
                        + "emitter does not translate - record-valued terms have to be built by hand.");
        }
    }

    private static IEdmExpression Primitive(
        EdmPrimitiveTypeKind kind,
        object value,
        string term,
        string targetDescription) =>
        kind switch
        {
            EdmPrimitiveTypeKind.Boolean => new EdmBooleanConstant(Convert.ToBoolean(value)),
            EdmPrimitiveTypeKind.String => new EdmStringConstant(Convert.ToString(value) ?? ""),
            EdmPrimitiveTypeKind.Byte
                or EdmPrimitiveTypeKind.SByte
                or EdmPrimitiveTypeKind.Int16
                or EdmPrimitiveTypeKind.Int32
                or EdmPrimitiveTypeKind.Int64 => new EdmIntegerConstant(Convert.ToInt64(value)),
            EdmPrimitiveTypeKind.Decimal => new EdmDecimalConstant(Convert.ToDecimal(value)),
            EdmPrimitiveTypeKind.Double or EdmPrimitiveTypeKind.Single =>
                new EdmFloatingConstant(Convert.ToDouble(value)),
            EdmPrimitiveTypeKind.Guid => new EdmGuidConstant(Guid.Parse(Convert.ToString(value) ?? "")),
            EdmPrimitiveTypeKind.Date => new EdmDateConstant(Date.Parse(Convert.ToString(value) ?? "")),
            EdmPrimitiveTypeKind.DateTimeOffset =>
                new EdmDateTimeOffsetConstant(DateTimeOffset.Parse(Convert.ToString(value) ?? "")),
            EdmPrimitiveTypeKind.Duration =>
                new EdmDurationConstant(TimeSpan.Parse(Convert.ToString(value) ?? "")),

            // Edm.PrimitiveType: the term does not fix the type, so the value's CLR type decides. Only
            // Validation.Minimum / Validation.Maximum are declared this way.
            EdmPrimitiveTypeKind.PrimitiveType => Inferred(value, term, targetDescription),

            _ => throw new InvalidOperationException(
                $"Term '{term}' on '{targetDescription}' has primitive kind {kind}, which this emitter "
                    + "does not translate."),
        };

    private static IEdmExpression Inferred(object value, string term, string targetDescription) =>
        value switch
        {
            bool b => new EdmBooleanConstant(b),
            string s => new EdmStringConstant(s),
            byte or sbyte or short or ushort or int or uint or long =>
                new EdmIntegerConstant(Convert.ToInt64(value)),
            float or double => new EdmFloatingConstant(Convert.ToDouble(value)),
            _ => throw new InvalidOperationException(
                $"Term '{term}' on '{targetDescription}' is typed Edm.PrimitiveType and the value is a "
                    + $"{value.GetType().Name}, which has no obvious constant expression."),
        };

    /// <summary>
    /// Maps a CLR enum value onto the vocabulary's own enum members by name. A flags value that C# renders
    /// as <c>"Read, Write"</c> becomes two members, which is how CSDL spells a combination.
    /// </summary>
    private static IEdmEnumMember[] Members(
        IEdmEnumType enumType,
        object value,
        string term,
        string targetDescription)
    {
        var names = (Convert.ToString(value) ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        return names
            .Select(name => enumType.Members.FirstOrDefault(m => m.Name == name)
                ?? throw new InvalidOperationException(
                    $"Term '{term}' on '{targetDescription}': '{name}' is not a member of "
                        + $"'{enumType.FullName()}'."))
            .ToArray();
    }

    private static IEdmExpression Path(
        IEdmPathType pathType,
        object value,
        string term,
        string targetDescription)
    {
        var path = Convert.ToString(value) ?? "";

        return pathType.PathKind switch
        {
            EdmPathTypeKind.PropertyPath => new EdmPropertyPathExpression(path),
            EdmPathTypeKind.NavigationPropertyPath => new EdmNavigationPropertyPathExpression(path),
            EdmPathTypeKind.AnnotationPath => new EdmAnnotationPathExpression(path),
            _ => throw new InvalidOperationException(
                $"Term '{term}' on '{targetDescription}' is a {pathType.PathKind}, which this emitter does "
                    + "not translate."),
        };
    }

    // --- AppliesTo -----------------------------------------------------------------------------------

    /// <summary>
    /// Checks the target against the term's <c>AppliesTo</c>. An empty <c>AppliesTo</c> means the term
    /// applies to anything, which is what the vocabularies say for e.g. <c>Core.Description</c>.
    /// </summary>
    private static bool AppliesTo(IEdmTerm term, IEdmVocabularyAnnotatable target)
    {
        if (string.IsNullOrWhiteSpace(term.AppliesTo))
        {
            return true;
        }

        var allowed = term.AppliesTo.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return Symbols(target).Any(allowed.Contains);
    }

    /// <summary>
    /// The CSDL <c>AppliesTo</c> symbols a target answers to. A collection-valued element answers to
    /// <c>Collection</c> as well, which is how the capability terms address an entity set.
    /// </summary>
    private static IEnumerable<string> Symbols(IEdmVocabularyAnnotatable target)
    {
        switch (target)
        {
            case IEdmEntitySet:
                yield return "EntitySet";
                yield return "Collection";
                break;
            case IEdmSingleton:
                yield return "Singleton";
                break;
            case IEdmEntityContainer:
                yield return "EntityContainer";
                break;
            case IEdmNavigationProperty navigation:
                yield return "NavigationProperty";
                if (navigation.Type.IsCollection())
                {
                    yield return "Collection";
                }

                break;
            case IEdmProperty property:
                yield return "Property";
                if (property.Type.IsCollection())
                {
                    yield return "Collection";
                }

                break;
            case IEdmOperationParameter:
                yield return "Parameter";
                break;
            case IEdmAction:
                yield return "Action";
                break;
            case IEdmFunction:
                yield return "Function";
                break;
            case IEdmEntityType:
                yield return "EntityType";
                break;
            case IEdmComplexType:
                yield return "ComplexType";
                break;
            case IEdmEnumType:
                yield return "EnumType";
                break;
            case IEdmEnumMember:
                yield return "Member";
                break;
            case IEdmTypeDefinition:
                yield return "TypeDefinition";
                break;
            default:
                yield return target.GetType().Name;
                break;
        }
    }

    private static string Symbol(IEdmVocabularyAnnotatable target) => string.Join("/", Symbols(target));
}
