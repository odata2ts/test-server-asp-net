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
/// a database it is the difference between a query that works and one that does not:
///
/// <code>
/// $filter=PublicationDate gt 2000-01-01     ->  WHERE CAST(strftime('%Y', "PublicationDate") AS INTEGER) * 10000
///                                                   + CAST(strftime('%m', …) AS INTEGER) * 100
///                                                   + CAST(strftime('%d', …) AS INTEGER) > @p
///
/// $filter=OpensAt gt 09:30:00               ->  no translation at all, because there is no SQL for
///                                               TimeOnly.Hour on a SQLite column - the request failed
/// </code>
///
/// The date form answers correctly but can never use an index, since no column appears on its own. The
/// time form has no translation in any representation, which is why storing the column differently does
/// not help - that was tried before this binder was written.
///
/// So both are turned back into what the request actually said: one comparison between two values of the
/// property's own CLR type.
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

    private static Expression? Constant(Date date, Type targetType) =>
        (Nullable.GetUnderlyingType(targetType) ?? targetType) switch
        {
            var t when t == typeof(DateOnly) => Expression.Constant((DateOnly)date, targetType),
            var t when t == typeof(DateTime) => Expression.Constant((DateTime)date, targetType),
            var t when t == typeof(DateTimeOffset) =>
                Expression.Constant(new DateTimeOffset((DateTime)date, TimeSpan.Zero), targetType),
            _ => null,
        };

    private static Expression? Constant(TimeOfDay time, Type targetType) =>
        (Nullable.GetUnderlyingType(targetType) ?? targetType) switch
        {
            var t when t == typeof(TimeOnly) => Expression.Constant((TimeOnly)time, targetType),
            var t when t == typeof(TimeSpan) => Expression.Constant((TimeSpan)time, targetType),
            _ => null,
        };
}
