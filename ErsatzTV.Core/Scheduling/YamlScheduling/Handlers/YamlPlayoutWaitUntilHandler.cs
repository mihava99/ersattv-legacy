#nullable enable
using ErsatzTV.Core.Scheduling.YamlScheduling.Models;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Core.Scheduling.YamlScheduling.Handlers;

public class YamlPlayoutWaitUntilHandler : IYamlPlayoutHandler
{
    public bool Reset => true;

    public Task<bool> Handle(
        YamlPlayoutContext context,
        YamlPlayoutInstruction instruction,
        PlayoutBuildMode mode,
        Func<string, Task> executeSequence,
        ILogger<SequentialPlayoutBuilder> logger,
        CancellationToken cancellationToken)
    {
        if (instruction is not YamlPlayoutWaitUntilInstruction waitUntil)
        {
            return Task.FromResult(false);
        }

        DateTimeOffset currentTime = context.CurrentTime;

        // ── 1. Plain time string e.g. "06:00", "6:00 AM" ────────────────────────
        if (TimeOnly.TryParse(waitUntil.WaitUntil, out TimeOnly result))
        {
            var dayOnly  = DateOnly.FromDateTime(currentTime.LocalDateTime);
            var timeOnly = TimeOnly.FromDateTime(currentTime.LocalDateTime);

            if (timeOnly > result)
            {
                if (waitUntil.Tomorrow)
                {
                    dayOnly     = dayOnly.AddDays(1);
                    currentTime = new DateTimeOffset(dayOnly, result, currentTime.Offset);
                }
                else if (waitUntil.RewindOnReset && mode == PlayoutBuildMode.Reset)
                {
                    currentTime = new DateTimeOffset(dayOnly, result, currentTime.Offset);
                }
                // else: time passed, tomorrow:false, not rewind → leave unchanged
            }
            else
            {
                // If we just crossed midnight naturally (within 60 min) and the
                // wait_until target is well into the day (after 6:00 AM), skip it
                // to avoid jumping currentTime forward causing an UNSCHEDULED gap.
                if (timeOnly.Hour == 0 && timeOnly.Minute < 60 && result.Hour >= 6)
                {
                    context.CurrentTime = currentTime;
                    return Task.FromResult(true);
                }

                currentTime = new DateTimeOffset(dayOnly, result, currentTime.Offset);
            }
        }
        // ── 2. Day-name anchor e.g. "Monday 00:00", "Friday 06:30" ──────────────
        else if (TryParseDayTime(waitUntil.WaitUntil, out DayOfWeek targetDay, out TimeOnly targetTime))
        {
            var currentLocal     = currentTime.ToLocalTime().DateTime;
            var currentDay       = currentLocal.DayOfWeek;
            var currentTimeOfDay = TimeOnly.FromDateTime(currentLocal);

            int daysUntil = ((int)targetDay - (int)currentDay + 7) % 7;

            // skip_if_passed: used on Tue-Sun anchors to fall through without
            // jumping 7 days when repeat: true loops back mid-week
            if (waitUntil.SkipIfPassed && daysUntil > 0)
            {
                context.CurrentTime = currentTime;
                return Task.FromResult(true);
            }

            // Same day and time already passed
            if (daysUntil == 0 && currentTimeOfDay > targetTime)
            {
                // Content just crossed midnight naturally (within 60 min of anchor).
                // 60 min safely covers any single music video/concert segment overrun
                // without treating genuine mid-day resets as midnight crossings.
                if (currentTimeOfDay.Hour == 0 && currentTimeOfDay.Minute < 60)
                {
                    context.CurrentTime = currentTime;
                    return Task.FromResult(true);
                }

                if (waitUntil.SkipIfPassed)
                {
                    context.CurrentTime = currentTime;
                    return Task.FromResult(true);
                }

                // Mid-day past anchor → jump to next week
                daysUntil = 7;
            }

            var targetDate   = DateOnly.FromDateTime(currentLocal).AddDays(daysUntil);
            var targetOffset = new DateTimeOffset(targetDate, targetTime, currentTime.Offset);
            currentTime = targetOffset;
        }
        // ── 3. Unrecognised format ────────────────────────────────────────────────
        else
        {
            logger.LogWarning(
                "wait_until value '{Value}' could not be parsed as a time or day+time",
                waitUntil.WaitUntil);
        }

        context.CurrentTime = currentTime;
        return Task.FromResult(true);
    }

    public static bool TryParseDayTime(
        string? input,
        out DayOfWeek day,
        out TimeOnly time)
    {
        day  = default;
        time = default;

        if (string.IsNullOrWhiteSpace(input))
            return false;

        int spaceIndex = input.IndexOf(' ');
        if (spaceIndex <= 0)
            return false;

        string dayPart  = input[..spaceIndex].Trim();
        string timePart = input[spaceIndex..].Trim();

        if (!Enum.TryParse<DayOfWeek>(dayPart, ignoreCase: true, out DayOfWeek parsedDay))
            return false;

        if (!TimeOnly.TryParse(timePart, out TimeOnly parsedTime))
            return false;

        day  = parsedDay;
        time = parsedTime;
        return true;
    }
}
