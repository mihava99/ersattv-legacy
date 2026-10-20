using System.Globalization;
using System.Runtime.CompilerServices;
using NCalc;
using NCalc.Handlers;

namespace ErsatzTV.Infrastructure.Streaming.Graphics;

public static class OpacityExpressionHelper
{
    private static readonly ConditionalWeakTable<Expression, HasTagHandler> HasTagHandlers = new();

    public static void EvaluateFunction(string name, FunctionEventArgs args)
    {
        switch (name)
        {
            case "LinearFadePoints":
            {
                if (args.Parameters.Count != 5)
                {
                    throw new ArgumentException("LinearFadePoints() requires 5 arguments.");
                }

                var time = Convert.ToDouble(args.Parameters.Evaluate(0), CultureInfo.CurrentCulture);
                var start = Convert.ToDouble(args.Parameters.Evaluate(1), CultureInfo.CurrentCulture);
                var peakStart = Convert.ToDouble(args.Parameters.Evaluate(2), CultureInfo.CurrentCulture);
                var peakEnd = Convert.ToDouble(args.Parameters.Evaluate(3), CultureInfo.CurrentCulture);
                var end = Convert.ToDouble(args.Parameters.Evaluate(4), CultureInfo.CurrentCulture);

                args.Result = LinearFadePoints(time, start, peakStart, peakEnd, end);
                break;
            }
            case "LinearFadeDuration":
            {
                if (args.Parameters.Count != 4)
                {
                    throw new ArgumentException("LinearFadeDuration() requires 4 arguments.");
                }

                var time = Convert.ToDouble(args.Parameters.Evaluate(0), CultureInfo.CurrentCulture);
                var start = Convert.ToDouble(args.Parameters.Evaluate(1), CultureInfo.CurrentCulture);
                var fadeSeconds = Convert.ToDouble(args.Parameters.Evaluate(2), CultureInfo.CurrentCulture);
                var peakSeconds = Convert.ToDouble(args.Parameters.Evaluate(3), CultureInfo.CurrentCulture);

                args.Result = LinearFadeDuration(time, start, fadeSeconds, peakSeconds);
                break;
            }
        }
    }

    private static double LinearFadePoints(double time, double start, double peakStart, double peakEnd, double end)
    {
        if (time < start || time >= end)
        {
            return 0;
        }

        // fade in
        if (time < peakStart)
        {
            return (time - start) / (peakStart - start);
        }

        // solid
        if (time < peakEnd)
        {
            return 1.0;
        }

        // fade out
        return (end - time) / (end - peakEnd);
    }

    private static double LinearFadeDuration(double time, double start, double fadeSeconds, double peakSeconds)
    {
        // edge case with no fade
        if (fadeSeconds <= 0)
        {
            double noFadeEnd = start + peakSeconds;
            return time >= start && time < noFadeEnd ? 1.0 : 0.0;
        }

        double peakStart = start + fadeSeconds;
        double peakEnd = peakStart + peakSeconds;
        double end = peakEnd + fadeSeconds;

        return LinearFadePoints(time, start, peakStart, peakEnd, end);
    }

    /// <summary>
    /// element_seconds: time since this element was last activated (graphics_on).
    /// Resets to 0 every time the element is turned on, independently of content
    /// or channel time. Use in opacity_expression for per-activation fade-ins:
    ///   LinearFadePoints(element_seconds, 0, 2, 999999, 1000000)
    /// </summary>
    public static float GetOpacity(
        Expression expression,
        TimeSpan timeOfDay,
        TimeSpan contentTime,
        TimeSpan contentTotalTime,
        TimeSpan channelTime,
        double elementSeconds = 0,
        double blockRemainingSeconds = 0,
        IReadOnlyList<string> tags = null)
    {
        expression.Parameters["content_seconds"] = contentTime.TotalSeconds;
        expression.Parameters["content_total_seconds"] = contentTotalTime.TotalSeconds;
        expression.Parameters["channel_seconds"] = channelTime.TotalSeconds;
        expression.Parameters["time_of_day_seconds"] = timeOfDay.TotalSeconds;
        expression.Parameters["element_seconds"] = elementSeconds;
        expression.Parameters["block_remaining_seconds"] = blockRemainingSeconds;

        // has_tag("TAG_NAME") — returns 1 if current media item has the tag, 0 if not.
        // Useful in opacity_expression to show/hide elements based on content metadata:
        //   opacity_expression: "has_tag(\"PARENTALGUIDANCE_on\")"
        // The tag list is injected from MediaItem_Tags template variable at runtime.
        // This runs for every frame, so the handler is registered once per expression.
        // A new delegate per call can't be removed with -=, so handlers would pile up.
        HasTagHandler hasTagHandler = HasTagHandlers.GetValue(
            expression,
            e =>
            {
                var handler = new HasTagHandler();
                e.EvaluateFunction += handler.Handle;
                return handler;
            });
        hasTagHandler.Tags = tags ?? [];

        object expressionResult = expression.Evaluate();
        return Convert.ToSingle(expressionResult, CultureInfo.InvariantCulture);
    }

    private sealed class HasTagHandler
    {
        public IReadOnlyList<string> Tags { get; set; } = [];

        public void Handle(string name, FunctionEventArgs args)
        {
            if (name != "has_tag")
            {
                return;
            }

            if (args.Parameters.Count != 1)
            {
                args.Result = 0;
                return;
            }

            string tagName = args.Parameters.Evaluate(0)?.ToString() ?? string.Empty;
            args.Result = Tags.Any(t => string.Equals(t, tagName, StringComparison.OrdinalIgnoreCase)) ? 1 : 0;
        }
    }
}
