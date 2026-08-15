using System.Linq.Expressions;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Query.Expressions;
using Microsoft.OData.Edm;
using Microsoft.OData.UriParser;

namespace LibraryService.Query;

/// <summary>
/// Compares <c>Edm.Date</c> and <c>Edm.TimeOfDay</c> as values instead of as arithmetic on their parts.
///
/// By default the binder does not compare either type directly. It takes both operands apart and rebuilds
/// them as a single number - <c>year * 10000 + month * 100 + day</c> for a date, a sum of tick multiples
/// for a time of day - and compares those. Against <c>List&lt;T&gt;</c> that is merely roundabout. Against
/// a database, one of the two stops working altogether:
///
/// <code>
/// $filter=PublicationDate gt 2000-01-01     ->  WHERE EXTRACT(YEAR FROM "PublicationDate") * 10000
///                                                   + EXTRACT(MONTH FROM …) * 100
///                                                   + EXTRACT(DAY FROM …) > @p
///
/// $filter=OpensAt gt 09:30:00               ->  no translation at all: EF cannot turn
///                                               `OpensAt.Hour * 36000000000 + …` into SQL, and answers 500
/// </code>
///
/// The date form is correct but can never use an index, since no column appears on its own. The time form
/// has no translation in any representation - it failed against SQLite and, as verified after the move, it
/// fails against Postgres too, so it is the arithmetic itself and not the column type that has no SQL.
///
/// So both are turned back into what the request actually said: one comparison between two values of the
/// property's own CLR type. Postgres compares a <c>date</c> and a <c>time</c> directly and can use an
/// index for either.
///
/// The approach is the one worked out in
/// https://github.com/OData/AspNetCoreOData/issues/1473, where the same arithmetic shows up as
/// <c>DATEPART</c> against SQL Server. <c>ExpressionBinderHelper.CreateDateBinaryExpression</c> and its
/// time counterpart are internal and not configurable, so overriding the binder is the only way in.
/// </summary>
public class DateComparisonBinder : FilterBinder
{
    /// <summary>
    /// The comparisons worth intercepting. Everything else - <c>and</c>, <c>or</c>, arithmetic - goes to
    /// the base implementation untouched.
    /// </summary>
    private static readonly Dictionary<BinaryOperatorKind, ExpressionType> Comparisons = new()
    {
        [BinaryOperatorKind.Equal] = ExpressionType.Equal,
        [BinaryOperatorKind.NotEqual] = ExpressionType.NotEqual,
        [BinaryOperatorKind.GreaterThan] = ExpressionType.GreaterThan,
        [BinaryOperatorKind.GreaterThanOrEqual] = ExpressionType.GreaterThanOrEqual,
        [BinaryOperatorKind.LessThan] = ExpressionType.LessThan,
        [BinaryOperatorKind.LessThanOrEqual] = ExpressionType.LessThanOrEqual,
    };

    public override Expression BindBinaryOperatorNode(BinaryOperatorNode binaryOperatorNode, QueryBinderContext context)
    {
        // Decided from the query nodes, before either side is bound: binding twice would be wasteful on
        // every comparison in every request, and this runs for all of them.
        //
        // Only in the mode this service actually runs in. With null propagation switched on - which is
        // what a LINQ-to-Objects source gets - the base binder produces a three-valued `bool?` that the
        // surrounding expression is built to consume, and a plain comparison would not compose with it.
        // Against a query provider the setting is False and the store decides what a null compares to,
        // which is the case rewritten below.
        if (context.QuerySettings.HandleNullPropagation == HandleNullPropagationOption.True
            || !Comparisons.TryGetValue(binaryOperatorNode.OperatorKind, out var comparison)
            || !IsDateOrTimeOfDay(binaryOperatorNode.Left)
            || !IsDateOrTimeOfDay(binaryOperatorNode.Right))
        {
            return base.BindBinaryOperatorNode(binaryOperatorNode, context);
        }

        var left = Bind(binaryOperatorNode.Left, context);
        var right = Bind(binaryOperatorNode.Right, context);

        // Whichever side is not the OData literal carries the type to compare in - the property's own,
        // e.g. DateOnly? or TimeOnly?.
        var targetType = IsODataLiteral(left.Type) ? right.Type : left.Type;
        if (IsODataLiteral(targetType))
        {
            // Both sides are literals, or the property really is declared as one of the OData structs.
            // Nothing to gain here, and nothing that would be safe to assume.
            return base.BindBinaryOperatorNode(binaryOperatorNode, context);
        }

        if (AsComparable(left, targetType) is not { } comparableLeft
            || AsComparable(right, targetType) is not { } comparableRight)
        {
            return base.BindBinaryOperatorNode(binaryOperatorNode, context);
        }

        // liftToNull: false gives a plain bool, which is the shape the rest of the bound filter expects.
        //
        // It also settles what a null operand does, and .NET's rules for a nullable comparison are exactly
        // the ones OData specifies: `null ne x` is true, `null gt x` is false, so a row whose date is null
        // is kept by `ne` and by a negated comparison and dropped by a plain one. That is also what the
        // stock binder produces for every other nullable property once null propagation is off, so dates
        // and times answer like everything else rather than becoming the exception.
        return Expression.MakeBinary(comparison, comparableLeft, comparableRight, liftToNull: false, method: null);
    }

    /// <summary>
    /// Whether an operand is an <c>Edm.Date</c> or <c>Edm.TimeOfDay</c> - or a null literal, which carries
    /// no type of its own and has to be allowed through so that <c>PublicationDate eq null</c> still lands
    /// here rather than being split off from its property.
    /// </summary>
    private static bool IsDateOrTimeOfDay(SingleValueNode node) =>
        node.TypeReference is null
        || node.TypeReference.PrimitiveKind() is EdmPrimitiveTypeKind.Date or EdmPrimitiveTypeKind.TimeOfDay;

    private static bool IsODataLiteral(Type type) =>
        type == typeof(Date) || type == typeof(Date?)
        || type == typeof(TimeOfDay) || type == typeof(TimeOfDay?);

    /// <summary>
    /// Restates one operand in <paramref name="targetType" />, or returns null if it cannot be - in which
    /// case the caller leaves the whole comparison to the base implementation rather than half-rewriting it.
    /// </summary>
    private static Expression? AsComparable(Expression operand, Type targetType)
    {
        if (operand.Type == targetType)
        {
            return operand;
        }

        if (!IsODataLiteral(operand.Type))
        {
            // A property or a null literal typed as the target already - nullability aside, which
            // MakeBinary lifts by itself.
            return operand.Type == Nullable.GetUnderlyingType(targetType)
                ? Expression.Convert(operand, targetType)
                : null;
        }

        return ValueOf(operand) switch
        {
            Date date => Constant(date, targetType),
            TimeOfDay time => Constant(time, targetType),
            _ => null,
        };
    }

    /// <summary>
    /// Reads the literal back out of the bound expression.
    ///
    /// A constant does not arrive as a <see cref="ConstantExpression" />: the binder wraps it in a field of
    /// a container object so that the provider parameterises it instead of inlining it. Evaluating the
    /// expression is what gets the value out - and unlike reaching into that container by name, it does
    /// not depend on a type the library keeps internal.
    ///
    /// The literal is inlined into the query afterwards rather than parameterised, which for a service
    /// whose database is seven rows in memory costs nothing.
    /// </summary>
    private static object? ValueOf(Expression expression) =>
        expression switch
        {
            ConstantExpression constant => constant.Value,
            MemberExpression { Expression: ConstantExpression } member =>
                Expression.Lambda(member).Compile().DynamicInvoke(),
            _ => null,
        };

    /// <summary>
    /// Restates an <c>Edm.Date</c> literal as a <see cref="DateOnly" />, and refuses anything else.
    ///
    /// Deliberately not <see cref="DateTime" /> or <see cref="DateTimeOffset" />, even though both can hold
    /// a date: the other operand then is not a date column but a *timestamp*, which is what
    /// <c>$filter=date(LoanedAt) eq 2026-06-01</c> produces - the library's <c>date()</c> does not truncate,
    /// it hands the whole timestamp through (visible in <c>$compute=date(LoanedAt)</c>, which returns
    /// <c>2026-06-01T10:00:00Z</c>). Converting the literal here would compare 10:00 against midnight and
    /// answer "no match" for a loan that is plainly on that date - a wrong answer rather than an error.
    ///
    /// Returning null instead leaves that comparison to the base implementation, whose part-by-part
    /// arithmetic is roundabout but correct, and which Postgres translates. So this binder now only takes
    /// over what it can state more directly, and stays out of everything else.
    /// </summary>
    private static Expression? Constant(Date date, Type targetType) =>
        (Nullable.GetUnderlyingType(targetType) ?? targetType) == typeof(DateOnly)
            ? Expression.Constant((DateOnly)date, targetType)
            : null;

    /// <summary>
    /// The same for <c>Edm.TimeOfDay</c>: only a genuine <see cref="TimeOnly" /> property. A
    /// <see cref="TimeSpan" /> is <c>Edm.Duration</c> in this model, and <c>time()</c> over a timestamp has
    /// the truncation problem its date counterpart has.
    /// </summary>
    private static Expression? Constant(TimeOfDay time, Type targetType) =>
        (Nullable.GetUnderlyingType(targetType) ?? targetType) == typeof(TimeOnly)
            ? Expression.Constant((TimeOnly)time, targetType)
            : null;
}
