using ErsatzTV.Core.Interfaces.Scheduling;
using ErsatzTV.Core.Scheduling.YamlScheduling.Models;
using Microsoft.Extensions.Logging;
using NCalc;

namespace ErsatzTV.Core.Scheduling.YamlScheduling.Handlers;

public class YamlPlayoutPadUntilHandler(EnumeratorCache enumeratorCache) : YamlPlayoutDurationHandler(enumeratorCache)
{
    public override async Task<bool> Handle(
        YamlPlayoutContext context,
        YamlPlayoutInstruction instruction,
        PlayoutBuildMode mode,
        Func<string, Task> executeSequence,
        ILogger<SequentialPlayoutBuilder> logger,
        CancellationToken cancellationToken)
    {
        if (instruction is not YamlPlayoutPadUntilInstruction padUntil)
        {
            return false;
        }

        DateTimeOffset targetTime = context.CurrentTime;

        if (TimeOnly.TryParse(padUntil.PadUntil, out TimeOnly result))
        {
            //logger.LogDebug("Will pad until time {Time}", result);

            // Use ToLocalTime() to get correct local time regardless of offset stored in CurrentTime
            var localDateTime = targetTime.ToLocalTime().DateTime;
            var dayOnly = DateOnly.FromDateTime(localDateTime);
            var timeOnly = TimeOnly.FromDateTime(localDateTime);

            if (timeOnly > result)
            {
                var expression = new Expression(padUntil.Tomorrow);
                expression.EvaluateParameter += (name, e) =>
                {
                    e.Result = name switch
                    {
                        "now" => timeOnly.Hour + timeOnly.Minute / 60.0 + timeOnly.Second / 3600.0,
                        _ => e.Result
                    };
                };

                if (expression.Evaluate(cancellationToken) as bool? == true)
                {
                    // this is wrong when offset changes
                    dayOnly = dayOnly.AddDays(1);
                    targetTime = new DateTimeOffset(dayOnly, result, targetTime.Offset);
                }
            }
            else
            {
                // If we just crossed midnight naturally (within 60 min of midnight)
                // and the pad target is well into the day (after 6:00 AM), this pad
                // belongs to the previous day's block — skip it to avoid filling the
                // entire next day with filler content and causing an UNSCHEDULED gap.
                if (timeOnly.Hour == 0 && timeOnly.Minute < 60 && result.Hour >= 6)
                {
                    // Crossed midnight — this pad belongs to yesterday, skip it
                    return false;
                }

                // this is wrong when offset changes
                targetTime = new DateTimeOffset(dayOnly, result, targetTime.Offset);
            }
        }

        // logger.LogDebug(
        //     "Padding from {CurrentTime} until {TargetTime}",
        //     context.CurrentTime,
        //     targetTime);

        Option<IMediaCollectionEnumerator> maybeEnumerator = await GetContentEnumerator(
            context,
            instruction.Content,
            logger,
            cancellationToken);

        Option<IMediaCollectionEnumerator> fallbackEnumerator = await GetContentEnumerator(
            context,
            padUntil.Fallback,
            logger,
            cancellationToken);

        foreach (IMediaCollectionEnumerator enumerator in maybeEnumerator)
        {
            context.CurrentTime = await Schedule(
                context,
                padUntil.Content,
                padUntil.Fallback,
                targetTime,
                padUntil.StopBeforeEnd,
                padUntil.DiscardAttempts,
                padUntil.Trim,
                padUntil.OfflineTail,
                GetFillerKind(padUntil, context),
                padUntil.CustomTitle,
                padUntil.DisableWatermarks,
                enumerator,
                fallbackEnumerator,
                executeSequence,
                logger);

            return true;
        }

        return false;
    }
}
