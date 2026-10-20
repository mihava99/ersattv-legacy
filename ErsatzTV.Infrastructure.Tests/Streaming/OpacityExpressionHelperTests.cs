using ErsatzTV.Infrastructure.Streaming.Graphics;
using NCalc;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Infrastructure.Tests.Streaming;

[TestFixture]
public class OpacityExpressionHelperTests
{
    [TestCase("TAG", 1.0f)]
    [TestCase("tag", 1.0f)]
    [TestCase("OTHER", 0.0f)]
    public void Has_tag_should_match_media_item_tags(string tag, float expected)
    {
        var expression = new Expression($"has_tag(\"{tag}\")");

        float opacity = GetOpacity(expression, ["TAG"]);

        opacity.ShouldBe(expected);
    }

    [Test]
    public void Has_tag_should_use_the_latest_tags_passed_for_the_same_expression()
    {
        var expression = new Expression("has_tag(\"TAG\")");

        GetOpacity(expression, ["TAG"]).ShouldBe(1.0f);
        GetOpacity(expression, ["OTHER"]).ShouldBe(0.0f);
    }

    [Test]
    public void Repeated_evaluation_should_not_accumulate_function_handlers()
    {
        var expression = new Expression("has_tag(\"TAG\") * LinearFadePoints(content_seconds, 0, 1, 2, 3)");
        expression.EvaluateFunction += OpacityExpressionHelper.EvaluateFunction;

        // one evaluation per frame; a handler added on every frame makes each frame slower and allocates more
        for (var i = 0; i < 5000; i++)
        {
            GetOpacity(expression, ["TAG"]);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        GetOpacity(expression, ["TAG"]);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.ShouldBeLessThan(32 * 1024);
    }

    private static float GetOpacity(Expression expression, IReadOnlyList<string> tags) =>
        OpacityExpressionHelper.GetOpacity(
            expression,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1.5),
            TimeSpan.FromMinutes(10),
            TimeSpan.Zero,
            tags: tags);
}
