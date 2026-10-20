using System.Collections.Immutable;
using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Scheduling;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Scheduling;
//using ErsatzTV.Core.Scheduling.Engine;
using ErsatzTV.Core.Scheduling.YamlScheduling.Handlers;
using ErsatzTV.Core.Scheduling.YamlScheduling.Models;
using ErsatzTV.Core.Search;
using LanguageExt.UnsafeValueAccess;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ErsatzTV.Core.Scheduling.YamlScheduling;

public class SequentialPlayoutBuilder(
    //ISchedulingEngine schedulingEngine,
    IFileSystem fileSystem,
    IConfigElementRepository configElementRepository,
    IMediaCollectionRepository mediaCollectionRepository,
    IChannelRepository channelRepository,
    IGraphicsElementRepository graphicsElementRepository,
    ISequentialScheduleValidator sequentialScheduleValidator,
    ILogger<SequentialPlayoutBuilder> logger)
    : ISequentialPlayoutBuilder
{
    public async Task<Either<BaseError, PlayoutBuildResult>> Build(
        DateTimeOffset start,
        Playout playout,
        PlayoutReferenceData referenceData,
        PlayoutBuildMode mode,
        CancellationToken cancellationToken)
    {
        //schedulingEngine.WithMode(mode);
        //schedulingEngine.WithSeed(playout.Seed);

        PlayoutBuildResult result = PlayoutBuildResult.Empty;

        if (!fileSystem.File.Exists(playout.ScheduleFile))
        {
            logger.LogWarning("Sequential schedule file {File} does not exist; aborting.", playout.ScheduleFile);
            return BaseError.New($"Sequential schedule file {playout.ScheduleFile} does not exist");
        }

        Option<YamlPlayoutDefinition> maybePlayoutDefinition =
            await LoadYamlDefinition(playout.ScheduleFile, false, cancellationToken);
        if (maybePlayoutDefinition.IsNone)
        {
            logger.LogWarning("Sequential schedule file {File} is invalid; aborting.", playout.ScheduleFile);
            return BaseError.New($"Sequential schedule file {playout.ScheduleFile} is invalid");
        }

        // using ValueUnsafe to avoid nesting
        YamlPlayoutDefinition playoutDefinition = maybePlayoutDefinition.ValueUnsafe();

        foreach (string import in playoutDefinition.Import)
        {
            try
            {
                string path = import;
                if (!File.Exists(import))
                {
                    path = Path.Combine(
                        Path.GetDirectoryName(playout.ScheduleFile) ?? string.Empty,
                        import ?? string.Empty);
                    if (!File.Exists(path))
                    {
                        logger.LogError("YAML playout import {File} does not exist.", path);
                        return result;
                    }
                }

                Option<YamlPlayoutDefinition> maybeImportedDefinition =
                    await LoadYamlDefinition(path, true, cancellationToken);
                foreach (YamlPlayoutDefinition importedDefinition in maybeImportedDefinition)
                {
                    IEnumerable<YamlPlayoutContentItem> contentToAdd = importedDefinition.Content
                        .Where(c => playoutDefinition.Content.All(c2 => !string.Equals(
                            c2.Key,
                            c.Key,
                            StringComparison.OrdinalIgnoreCase)));

                    playoutDefinition.Content.AddRange(contentToAdd);

                    IEnumerable<YamlPlayoutSequenceItem> sequencesToAdd = importedDefinition.Sequence
                        .Where(s => playoutDefinition.Sequence.All(s2 => !string.Equals(
                            s2.Key,
                            s.Key,
                            StringComparison.OrdinalIgnoreCase)));

                    playoutDefinition.Sequence.AddRange(sequencesToAdd);
                }

                if (maybeImportedDefinition.IsNone)
                {
                    logger.LogWarning("YAML playout import {File} is invalid; aborting.", import);
                    return BaseError.New($"YAML playout import {import} is invalid");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected exception loading YAML playout import");
                return BaseError.New($"Unexpected exception loading YAML playout import: {ex}");
            }
        }

        int daysToBuild = await GetDaysToBuild(cancellationToken);
        DateTimeOffset finish = start.AddDays(daysToBuild);

        Dictionary<YamlPlayoutInstruction, IYamlPlayoutHandler> handlers = new();
        Dictionary<string, List<YamlPlayoutInstruction>> fillerSequences = new();
        var enumeratorCache = new EnumeratorCache(mediaCollectionRepository, logger);

        var context = new YamlPlayoutContext(playout, playoutDefinition, 1)
        {
            CurrentTime = start

            // no need to init default value and throw off visited count
            // InstructionIndex = 0
        };

        //schedulingEngine.BuildBetween(start, finish);

        // logger.LogDebug(
        //     "Default yaml context from {Start} to {Finish}, instruction {Instruction}",
        //     context.CurrentTime,
        //     finish,
        //     context.InstructionIndex);

        // remove old items
        // importantly, this should not remove their history
        result = result with { RemoveBefore = start };
        //schedulingEngine.RemoveBefore(start);

        //schedulingEngine.RestoreOrReset(Optional(playout.Anchor));

        // load saved state
        if (mode is not PlayoutBuildMode.Reset)
        {
            foreach (PlayoutAnchor prevAnchor in Optional(playout.Anchor))
            {
                context.Reset(prevAnchor, start);
            }
        }
        else
        {
            // reset (remove items and "currently active" history)

            // for testing
            // start = start.AddHours(-2);

            // erase items, not history
            result = result with { ClearItems = true };

            // remove any future or "currently active" history items
            // this prevents "walking" the playout forward by repeatedly resetting
            var toRemove = new List<PlayoutHistory>();
            toRemove.AddRange(
                referenceData.PlayoutHistory.Filter(h =>
                    h.When > start.UtcDateTime || h.When <= start.UtcDateTime && h.Finish >= start.UtcDateTime));
            foreach (PlayoutHistory history in toRemove)
            {
                result.HistoryToRemove.Add(history.Id);
            }
        }

        // logger.LogDebug(
        //     "Saved yaml context from {Start} to {Finish}, instruction {Instruction}",
        //     context.CurrentTime,
        //     finish,
        //     context.InstructionIndex);

        // apply all (filtered) history
        var filteredHistory = referenceData.PlayoutHistory.ToList();
        filteredHistory.RemoveAll(h => result.HistoryToRemove.Contains(h.Id));
        var applyHistoryHandler = new YamlPlayoutApplyHistoryHandler(enumeratorCache);
        foreach (YamlPlayoutContentItem contentItem in playoutDefinition.Content)
        {
            // if (!Enum.TryParse(contentItem.Order, true, out PlaybackOrder playbackOrder))
            // {
            //     continue;
            // }
            //
            // switch (contentItem)
            // {
            //     case YamlPlayoutContentCollectionItem collectionItem:
            //         // also applies history
            //         await schedulingEngine.AddCollection(
            //             collectionItem.Key,
            //             collectionItem.Collection,
            //             playbackOrder);
            //         break;
            // }

            await applyHistoryHandler.Handle(
                filteredHistory.ToImmutableList(),
                context,
                contentItem,
                logger,
                cancellationToken);
        }

        if (mode is PlayoutBuildMode.Reset)
        {
            // handle all on-reset instructions
            foreach (YamlPlayoutInstruction instruction in playoutDefinition.Reset)
            {
                Option<IYamlPlayoutHandler> maybeHandler = GetHandlerForInstruction(
                    handlers,
                    enumeratorCache,
                    instruction);

                foreach (IYamlPlayoutHandler handler in maybeHandler)
                {
                    if (!handler.Reset)
                    {
                        logger.LogInformation(
                            "Skipping unsupported reset instruction {Instruction}",
                            instruction.GetType().Name);
                    }
                    else
                    {
                        await handler.Handle(
                            context,
                            instruction,
                            mode,
                            _ => Task.CompletedTask,
                            logger,
                            cancellationToken);
                    }
                }
            }
        }

        if (DetectCycle(context.Definition))
        {
            logger.LogError("YAML sequence contains a cycle; unable to build playout");
            return BaseError.New("YAML sequence contains a cycle; unable to build playout");
        }

        var flattenCount = 0;
        while (context.Definition.Playout.Any(x => x is YamlPlayoutSequenceInstruction))
        {
            if (flattenCount > 100)
            {
                logger.LogError(
                    "YAML playout definition contains sequence nesting that is too deep; this introduces undefined behavior");
                break;
            }

            FlattenSequences(context, context.Definition.Playout);
            flattenCount++;
        }

        // flatten each schedule's playout the same way as the default playout
        foreach (YamlPlayoutScheduleItem schedule in context.Definition.Schedules)
        {
            var scheduleFlattenCount = 0;
            while (schedule.Playout.Any(x => x is YamlPlayoutSequenceInstruction))
            {
                if (scheduleFlattenCount > 100)
                {
                    logger.LogError(
                        "YAML schedule '{Schedule}' contains sequence nesting that is too deep; this introduces undefined behavior",
                        schedule.Name);
                    break;
                }

                FlattenSequences(context, schedule.Playout);
                scheduleFlattenCount++;
            }
        }

        context.RestoreSequenceOrders();

        // ── Day-of-week fast-forward on reset ────────────────────────────────────
        // When a fresh reset occurs and the playout contains day-name wait_until
        // instructions (e.g. "Monday 00:00"), start from the last wait_until whose
        // target day is <= today rather than always from instruction 0 (which would
        // jump to next Monday on any non-Monday restart).
        if (mode is PlayoutBuildMode.Reset)
        {
            FastForwardToCurrentDay(context, start, logger);
        }

        // handle all playout instructions
        while (context.CurrentTime < finish)
        {
            // Re-seek if we just switched back from an alternate schedule
            if (context.NeedsReseekAfterScheduleSwitch)
            {
                context.NeedsReseekAfterScheduleSwitch = false;
                // Save current index — FastForwardToCurrentDay should only move us
                // FORWARD, never backward. If we're already past the correct position
                // (e.g. switch happened mid-content), don't rewind.
                int indexBeforeReseek = context.InstructionIndex;
                FastForwardToCurrentDay(context, context.CurrentTime, logger);
                if (context.InstructionIndex < indexBeforeReseek)
                {
                    // FastForwardToCurrentDay moved us backward — revert to avoid
                    // re-executing instructions that already ran (duplicate bumper)
                    context.InstructionIndex = indexBeforeReseek;
                }
                logger.LogDebug("Re-seeked after alternate schedule switch at {Time} idx={Idx}", context.CurrentTime, context.InstructionIndex);
            }

            // select the active schedule (if any) for the current time; a null name means the default playout
            string activeScheduleName = null;
            foreach (YamlPlayoutScheduleItem activeSchedule in
                     AlternateScheduleSelector.GetScheduleForDate(context.Definition.Schedules, context.CurrentTime))
            {
                activeScheduleName = activeSchedule.Name;
            }

            if (!string.Equals(activeScheduleName, context.ActiveSchedule, StringComparison.Ordinal))
            {
                logger.LogDebug(
                    "Switching sequential playout to schedule {Schedule} at {Time}",
                    activeScheduleName ?? "(default)",
                    context.CurrentTime);

                context.SwitchToSchedule(activeScheduleName);

                if (activeScheduleName != null)
                {
                    // Switching TO alternate: seek to correct mid-day position
                    SeekToCurrentTime(context, context.CurrentTime, logger);
                }
                // Switching BACK to default is handled by NeedsReseekAfterScheduleSwitch
                // on the next iteration via FastForwardToCurrentDay — don't call
                // SeekToCurrentTime here as it positions incorrectly for midnight entry.

                // split the EPG guide group at the schedule boundary
                context.AdvanceGuideGroup();
            }

            List<YamlPlayoutInstruction> instructions = context.CurrentInstructions;

            if (context.InstructionIndex >= instructions.Count)
            {
                logger.LogInformation("Reached the end of the YAML playout definition; stopping");
                break;
            }

            YamlPlayoutInstruction instruction = instructions[context.InstructionIndex];
            //logger.LogDebug("Current playout instruction: {Instruction}", instruction.GetType().Name);

            Option<IYamlPlayoutHandler> maybeHandler = GetHandlerForInstruction(handlers, enumeratorCache, instruction);

            foreach (IYamlPlayoutHandler handler in maybeHandler)
            {
                if (!await handler.Handle(context, instruction, mode, ExecuteSequenceLocal, logger, cancellationToken))
                {
                    logger.LogInformation("YAML playout instruction handler failed");
                }

                continue;

                async Task ExecuteSequenceLocal(string sequence)
                {
                    await ExecuteSequence(
                        handlers,
                        fillerSequences,
                        enumeratorCache,
                        mode,
                        context,
                        sequence,
                        cancellationToken);
                }
            }

            if (!instruction.ChangesIndex)
            {
                //logger.LogDebug("Moving to next instruction");
                context.InstructionIndex++;
            }
        }

        result = CleanUpHistory(referenceData, start, result);

        DateTime maxTime = context.CurrentTime.UtcDateTime;
        if (context.AddedItems.Count > 0)
        {
            maxTime = context.AddedItems.Max(i => i.Finish);
        }

        var anchor = new PlayoutAnchor
        {
            NextStart = maxTime,
            Context = context.Serialize()
        };

        context.AdvanceGuideGroup();

        // logger.LogDebug(
        //     "Saving yaml context at {Start}, instruction {Instruction}",
        //     maxTime,
        //     context.InstructionIndex);

        playout.Anchor = anchor;

        result.AddedItems.AddRange(context.AddedItems);
        result.AddedHistory.AddRange(context.AddedHistory);

        return result;
    }

    private async Task ExecuteSequence(
        Dictionary<YamlPlayoutInstruction, IYamlPlayoutHandler> handlers,
        Dictionary<string, List<YamlPlayoutInstruction>> fillerSequences,
        EnumeratorCache enumeratorCache,
        PlayoutBuildMode mode,
        YamlPlayoutContext context,
        string sequence,
        CancellationToken cancellationToken)
    {
        List<YamlPlayoutInstruction> sequenceInstructions = GetFillerSequenceInstructions(
            context,
            fillerSequences,
            sequence);

        foreach (YamlPlayoutInstruction instruction in sequenceInstructions)
        {
            //logger.LogDebug("Current playout instruction: {Instruction}", instruction.GetType().Name);

            Option<IYamlPlayoutHandler> maybeHandler = GetHandlerForInstruction(handlers, enumeratorCache, instruction);

            foreach (IYamlPlayoutHandler handler in maybeHandler)
            {
                if (!await handler.Handle(
                        context,
                        instruction,
                        mode,
                        _ => Task.CompletedTask,
                        logger,
                        cancellationToken))
                {
                    logger.LogInformation("YAML playout instruction handler failed");
                }
            }
        }
    }

    // nested sequences are expanded once per build so their (cloned) instructions keep reusing the same handlers;
    // cycles are rejected before any filler sequence executes
    private static List<YamlPlayoutInstruction> GetFillerSequenceInstructions(
        YamlPlayoutContext context,
        Dictionary<string, List<YamlPlayoutInstruction>> fillerSequences,
        string sequence)
    {
        if (fillerSequences.TryGetValue(sequence, out List<YamlPlayoutInstruction> cached))
        {
            return cached;
        }

        var result = new List<YamlPlayoutInstruction>();

        IEnumerable<YamlPlayoutInstruction> rawInstructions = context.Definition.Sequence
            .Filter(s => s.Key == sequence)
            .HeadOrNone()
            .Map(s => s.Items)
            .Flatten();

        foreach (YamlPlayoutInstruction instruction in rawInstructions)
        {
            if (instruction is not YamlPlayoutSequenceInstruction sequenceInstruction)
            {
                result.Add(instruction);
                continue;
            }

            List<YamlPlayoutInstruction> nestedInstructions = GetFillerSequenceInstructions(
                context,
                fillerSequences,
                sequenceInstruction.Sequence);

            if (!string.IsNullOrWhiteSpace(sequenceInstruction.CustomTitle))
            {
                nestedInstructions = nestedInstructions
                    .Map(i =>
                    {
                        YamlPlayoutInstruction copy = i.Clone();
                        copy.CustomTitle = sequenceInstruction.CustomTitle;
                        return copy;
                    })
                    .ToList();
            }

            int repeat = sequenceInstruction.Repeat > 0 ? sequenceInstruction.Repeat : 1;
            for (var r = 0; r < repeat; r++)
            {
                result.AddRange(nestedInstructions);
            }
        }

        fillerSequences.Add(sequence, result);
        return result;
    }

    private static bool DetectCycle(YamlPlayoutDefinition definition)
    {
        var graph = new AdjGraph();

        foreach (YamlPlayoutSequenceItem sequence in definition.Sequence)
        {
            foreach (YamlPlayoutSequenceInstruction instruction in
                     sequence.Items.OfType<YamlPlayoutSequenceInstruction>())
            {
                graph.AddEdge(sequence.Key, instruction.Sequence);
            }
        }

        return graph.HasAnyCycle();
    }

    private async Task<int> GetDaysToBuild(CancellationToken cancellationToken) =>
        await configElementRepository
            .GetValue<int>(ConfigElementKey.PlayoutDaysToBuild, cancellationToken)
            .IfNoneAsync(2);

    private static void FlattenSequences(YamlPlayoutContext context, List<YamlPlayoutInstruction> playout)
    {
        var rawInstructions = playout.ToImmutableList();
        playout.Clear();

        foreach (YamlPlayoutInstruction instruction in rawInstructions)
        {
            switch (instruction)
            {
                case YamlPlayoutSequenceInstruction sequenceInstruction:
                    List<YamlPlayoutInstruction> sequenceInstructions = context.Definition.Sequence
                        .Filter(s => s.Key == sequenceInstruction.Sequence)
                        .HeadOrNone()
                        .Map(s => s.Items)
                        .Flatten()
                        .ToList();

                    var sequenceGuid = Guid.NewGuid();
                    string sequenceFingerprint = Convert.ToHexString(SHA256.HashData(
                        Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(sequenceInstructions, Formatting.None))));
                    int repeat = sequenceInstruction.Repeat > 0 ? sequenceInstruction.Repeat : 1;

                    for (var r = 0; r < repeat; r++)
                    {
                        // insert independent instructions so each flattened sequence keeps its own shuffle state
                        for (var index = 0; index < sequenceInstructions.Count; index++)
                        {
                            YamlPlayoutInstruction instructionCopy = sequenceInstructions[index].Clone();
                            instructionCopy.SequenceKey = sequenceInstruction.Sequence;
                            instructionCopy.SequenceGuid = sequenceGuid;
                            instructionCopy.SequenceIndex = r * sequenceInstructions.Count + index;
                            instructionCopy.SequenceShuffled = false;
                            instructionCopy.SequenceFingerprint = sequenceFingerprint;

                            // copy custom title
                            if (!string.IsNullOrWhiteSpace(sequenceInstruction.CustomTitle))
                            {
                                instructionCopy.CustomTitle = sequenceInstruction.CustomTitle;
                            }

                            playout.Add(instructionCopy);
                        }
                    }

                    break;
                default:
                    playout.Add(instruction);
                    break;
            }
        }
    }

    private Option<IYamlPlayoutHandler> GetHandlerForInstruction(
        Dictionary<YamlPlayoutInstruction, IYamlPlayoutHandler> handlers,
        EnumeratorCache enumeratorCache,
        YamlPlayoutInstruction instruction)
    {
        if (handlers.TryGetValue(instruction, out IYamlPlayoutHandler handler))
        {
            return Optional(handler);
        }

        handler = instruction switch
        {
            YamlPlayoutRepeatInstruction => new YamlPlayoutRepeatHandler(),
            YamlPlayoutWaitUntilInstruction => new YamlPlayoutWaitUntilHandler(),
            YamlPlayoutEpgGroupInstruction => new YamlPlayoutEpgGroupHandler(),
            YamlPlayoutWatermarkInstruction => new YamlPlayoutWatermarkHandler(channelRepository),
            YamlPlayoutShuffleSequenceInstruction => new YamlPlayoutShuffleSequenceHandler(),
            YamlPlayoutPreRollInstruction => new YamlPlayoutPreRollHandler(),
            YamlPlayoutPostRollInstruction => new YamlPlayoutPostRollHandler(),
            YamlPlayoutMidRollInstruction => new YamlPlayoutMidRollHandler(),
            YamlPlayoutGraphicsOnInstruction => new YamlPlayoutGraphicsOnHandler(graphicsElementRepository),
            YamlPlayoutGraphicsOffInstruction => new YamlPlayoutGraphicsOffHandler(graphicsElementRepository),

            YamlPlayoutSkipItemsInstruction => new YamlPlayoutSkipItemsHandler(enumeratorCache),
            YamlPlayoutSkipToItemInstruction => new YamlPlayoutSkipToItemHandler(enumeratorCache),
            YamlPlayoutRewindInstruction => new YamlPlayoutRewindHandler(),

            // content handlers
            YamlPlayoutAllInstruction => new YamlPlayoutAllHandler(enumeratorCache),
            YamlPlayoutCountInstruction => new YamlPlayoutCountHandler(enumeratorCache),
            YamlPlayoutDurationInstruction => new YamlPlayoutDurationHandler(enumeratorCache),
            YamlPlayoutPadToNextInstruction => new YamlPlayoutPadToNextHandler(enumeratorCache),
            YamlPlayoutPadUntilInstruction => new YamlPlayoutPadUntilHandler(enumeratorCache),

            _ => null
        };

        if (handler != null)
        {
            handlers.Add(instruction, handler);
        }

        return Optional(handler);
    }

    private async Task<Option<YamlPlayoutDefinition>> LoadYamlDefinition(
        string fileName,
        bool isImport,
        CancellationToken cancellationToken)
    {
        try
        {
            string yaml = await File.ReadAllTextAsync(fileName, cancellationToken);
            if (!await sequentialScheduleValidator.ValidateSchedule(yaml, isImport))
            {
                return Option<YamlPlayoutDefinition>.None;
            }

            IDeserializer deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .WithTypeDiscriminatingNodeDeserializer(o =>
                {
                    var contentKeyMappings = new Dictionary<string, Type>
                    {
                        { "collection", typeof(YamlPlayoutContentCollectionItem) },
                        { "marathon", typeof(YamlPlayoutContentMarathonItem) },
                        { "multi_collection", typeof(YamlPlayoutContentMultiCollectionItem) },
                        { "playlist", typeof(YamlPlayoutContentPlaylistItem) },
                        { "search", typeof(YamlPlayoutContentSearchItem) },
                        { "show", typeof(YamlPlayoutContentShowItem) },
                        { "smart_collection", typeof(YamlPlayoutContentSmartCollectionItem) }
                    };

                    o.AddUniqueKeyTypeDiscriminator<YamlPlayoutContentItem>(contentKeyMappings);

                    var instructionKeyMappings = new Dictionary<string, Type>
                    {
                        { "all", typeof(YamlPlayoutAllInstruction) },
                        { "count", typeof(YamlPlayoutCountInstruction) },
                        { "duration", typeof(YamlPlayoutDurationInstruction) },
                        { "epg_group", typeof(YamlPlayoutEpgGroupInstruction) },
                        { "graphics_on", typeof(YamlPlayoutGraphicsOnInstruction) },
                        { "graphics_off", typeof(YamlPlayoutGraphicsOffInstruction) },
                        { "watermark", typeof(YamlPlayoutWatermarkInstruction) },
                        { "pad_to_next", typeof(YamlPlayoutPadToNextInstruction) },
                        { "pad_until", typeof(YamlPlayoutPadUntilInstruction) },
                        { "pre_roll", typeof(YamlPlayoutPreRollInstruction) },
                        { "post_roll", typeof(YamlPlayoutPostRollInstruction) },
                        { "mid_roll", typeof(YamlPlayoutMidRollInstruction) },
                        { "repeat", typeof(YamlPlayoutRepeatInstruction) },
                        { "rewind", typeof(YamlPlayoutRewindInstruction) },
                        { "sequence", typeof(YamlPlayoutSequenceInstruction) },
                        { "shuffle_sequence", typeof(YamlPlayoutShuffleSequenceInstruction) },
                        { "skip_items", typeof(YamlPlayoutSkipItemsInstruction) },
                        { "skip_to_item", typeof(YamlPlayoutSkipToItemInstruction) },
                        { "wait_until", typeof(YamlPlayoutWaitUntilInstruction) }
                    };

                    o.AddUniqueKeyTypeDiscriminator<YamlPlayoutInstruction>(instructionKeyMappings);
                })
                .Build();

            return deserializer.Deserialize<YamlPlayoutDefinition>(yaml);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error loading YAML playout definition");
            return Option<YamlPlayoutDefinition>.None;
        }
    }


    // ── Fast-forward to the correct weekday block on reset ───────────────────────
    // Scans the flattened playout for day-name wait_until instructions and advances
    // InstructionIndex to the LAST one whose target day has already passed relative
    // to `start`, so the build loop begins at the correct weekday block immediately.
    private static void FastForwardToCurrentDay(YamlPlayoutContext context, DateTimeOffset start, ILogger<SequentialPlayoutBuilder> logger)
    {
        List<YamlPlayoutInstruction> instructions = context.CurrentInstructions;
        DateTime localNow = start.ToLocalTime().DateTime;
        DayOfWeek today = localNow.DayOfWeek;
        TimeOnly nowTime = TimeOnly.FromDateTime(localNow);

        // Strategy: find the FIRST day anchor in the playout that targets the
        // soonest upcoming day from today. This is the correct starting instruction
        // because the WaitUntilHandler will advance currentTime to that day.
        //
        // Example: today=Thursday, playout has Mon/Tue/Wed/Thu/Fri/Sat anchors.
        // Thu's anchor time (00:00) has passed → today counts as "soonest past/present".
        // We find Thursday's first anchor instruction and start there.
        // WaitUntilHandler sees Thursday 00:00 already passed → daysUntil=7 (next Thu)
        // BUT since we want to start from the CURRENT week's Friday, we need to find
        // the anchor that is either TODAY (time passed) or the NEXT FUTURE day.
        //
        // Correct logic:
        // 1. Find all day-name wait_until anchors and their "days until" from today
        // 2. Pick the one with the SMALLEST daysUntil > 0 (next future day)
        //    OR daysUntil == 0 with time already passed (today, time passed = next week same day)
        // 3. BUT if today's anchor exists and time hasn't passed yet, start there (daysUntil=0)
        //
        // Actually the simplest correct approach:
        // - If today has an anchor and nowTime >= anchorTime → start from today's anchor
        //   (WaitUntilHandler will treat it as 0 days until, i.e. today — but since time
        //   already passed it will jump to next week. That's wrong too.)
        //
        // THE REAL CORRECT APPROACH:
        // Don't use FastForwardToCurrentDay at all for future days.
        // Only fast-forward when the current day IS in the playout and its time has passed.
        // For all other cases, let the WaitUntilHandler naturally find the next occurrence.
        // The ONLY case we need to handle: today's anchor time has already passed,
        // and we want to start from today's content (not next week's same day).
        // For that we set InstructionIndex to today's anchor and let currentTime = start (now).

        int bestIndex = 0;
        int bestDaysUntil = int.MaxValue;
        var foundDayAnchor = false;

        for (int i = 0; i < instructions.Count; i++)
        {
            if (instructions[i] is not YamlPlayoutWaitUntilInstruction waitUntil)
                continue;

            if (!YamlPlayoutWaitUntilHandler.TryParseDayTime(
                    waitUntil.WaitUntil,
                    out DayOfWeek targetDay,
                    out TimeOnly targetTime))
                continue;

            int daysUntil = ((int)targetDay - (int)today + 7) % 7;

            if (daysUntil == 0)
            {
                // Today's anchor — always start here regardless of time
                // WaitUntilHandler handles whether to play now or wait
                bestIndex = i;
                foundDayAnchor = true;
                break; // today is the best possible match, stop scanning
            }

            if (daysUntil < bestDaysUntil)
            {
                // Closer future day than what we've seen — use first occurrence of it
                bestDaysUntil = daysUntil;
                bestIndex = i;
                foundDayAnchor = true;
                // Keep scanning — a different day may be even closer
                // But if we see this same daysUntil again, we keep the first occurrence
            }
        }

        logger.LogDebug("FastForwardToCurrentDay: today={Today} nowTime={Now} bestIndex={Best} currentTime={CT} start={Start}",
            today, nowTime, bestIndex, context.CurrentTime, start);

        // Run anchor scan for ANY bestIndex including 0 (Monday is instruction 0)
        if (bestIndex >= 0 && instructions.Count > bestIndex)
        {
            // Scan forward from today's day anchor, simulating only time-based
            // instructions to find the last plain-time anchor before 'start'.
            // This gives us the exact block we should be in right now.
            var todayMidnight = new DateTimeOffset(
                DateOnly.FromDateTime(start.ToLocalTime().DateTime),
                new TimeOnly(0, 0), start.Offset);

            DateTimeOffset simTime = todayMidnight;
            // With a day anchor, skip the anchor itself (its time is set below). Without one,
            // instruction 0 is a normal instruction and must not be skipped.
            int scanStart = foundDayAnchor ? bestIndex + 1 : 0;
            int lastPlainAnchorIndex = scanStart;
            DateTimeOffset lastPlainAnchorTime = todayMidnight;

            var seekInstrs = context.CurrentInstructions;
            for (int j = scanStart; j < seekInstrs.Count; j++)
            {
                var instr = seekInstrs[j];
                DateTimeOffset? nextTime = null;

                if (instr is YamlPlayoutWaitUntilInstruction wu2)
                {
                    if (TimeOnly.TryParse(wu2.WaitUntil, out TimeOnly wt))
                    {
                        var simLocal = simTime.LocalDateTime;
                        var simTod = TimeOnly.FromDateTime(simLocal);
                        if (wt >= simTod)
                        {
                            nextTime = new DateTimeOffset(
                                DateOnly.FromDateTime(simLocal), wt, start.Offset);
                        }
                        else if (wu2.Tomorrow)
                        {
                            nextTime = new DateTimeOffset(
                                DateOnly.FromDateTime(simLocal).AddDays(1), wt, start.Offset);
                        }
                    }
                    else if (YamlPlayoutWaitUntilHandler.TryParseDayTime(wu2.WaitUntil, out _, out _))
                    {
                        // Hit another day-name anchor — stop scanning (end of today)
                        break;
                    }
                }
                else if (instr is YamlPlayoutPadUntilInstruction pu2)
                {
                    if (TimeOnly.TryParse(pu2.PadUntil, out TimeOnly pt))
                    {
                        var simLocal = simTime.LocalDateTime;
                        var simTod = TimeOnly.FromDateTime(simLocal);
                        if (pt > simTod)
                        {
                            nextTime = new DateTimeOffset(
                                DateOnly.FromDateTime(simLocal), pt, start.Offset);
                        }
                        else
                        {
                            // pad_until already past for today — treat as next day
                            nextTime = new DateTimeOffset(
                                DateOnly.FromDateTime(simLocal).AddDays(1), pt, start.Offset);
                        }
                    }
                }
                else if (instr is YamlPlayoutRepeatInstruction)
                {
                    break;
                }

                if (nextTime.HasValue)
                {
                    simTime = nextTime.Value;
                    if (simTime <= start)
                    {
                        lastPlainAnchorIndex = j + 1;
                        lastPlainAnchorTime = simTime;
                    }
                    else
                    {
                        break; // this anchor is in the future — we're in the right block
                    }
                }
            }

            context.InstructionIndex = lastPlainAnchorIndex;
            context.CurrentTime = lastPlainAnchorTime > todayMidnight
                ? lastPlainAnchorTime
                : start;

            logger.LogDebug(
                "FastForwardToCurrentDay: idx={Idx} time={T} start={S}",
                lastPlainAnchorIndex, context.CurrentTime, start);
        }
    }

    
    /// <summary>
    /// Seeks the instruction index and current time to the correct position in the
    /// current schedule's playout based on the real current time (now).
    /// Works for both day-name anchors (weekly schedules) and plain time anchors
    /// (alternate schedules with wait_until: "00:00" etc.).
    ///
    /// Algorithm:
    /// 1. Simulate running the playout from the start, advancing ONLY via
    ///    wait_until and pad_until time anchors (no content items).
    /// 2. Find the LAST time anchor whose computed time is <= now.
    /// 3. Start building from that anchor's next instruction, with currentTime
    ///    set to that anchor's time.
    /// This ensures pad_until/wait_until block boundaries fire at the correct
    /// absolute times, keeping the schedule aligned regardless of when it started.
    /// </summary>
    private static void SeekToCurrentTime(
        YamlPlayoutContext context,
        DateTimeOffset now,
        ILogger<SequentialPlayoutBuilder> logger)
    {
        List<YamlPlayoutInstruction> instructions = context.CurrentInstructions;
        if (instructions == null || instructions.Count == 0) return;

        // Start simulation from the beginning of today (or from instruction 0)
        var localNow = now.LocalDateTime;
        var todayMidnight = new DateTimeOffset(
            DateOnly.FromDateTime(localNow), new TimeOnly(0, 0), now.Offset);

        DateTimeOffset simTime = todayMidnight;
        int bestIndex = 0;
        DateTimeOffset bestTime = todayMidnight;

        for (int i = 0; i < instructions.Count; i++)
        {
            var instr = instructions[i];
            DateTimeOffset? nextTime = null;

            if (instr is YamlPlayoutWaitUntilInstruction wu)
            {
                if (TimeOnly.TryParse(wu.WaitUntil, out TimeOnly t))
                {
                    // Plain time anchor
                    var simLocal = simTime.LocalDateTime;
                    var simTod = TimeOnly.FromDateTime(simLocal);
                    if (t >= simTod)
                    {
                        nextTime = new DateTimeOffset(
                            DateOnly.FromDateTime(simLocal), t, now.Offset);
                    }
                    else if (wu.Tomorrow)
                    {
                        nextTime = new DateTimeOffset(
                            DateOnly.FromDateTime(simLocal).AddDays(1), t, now.Offset);
                    }
                    else
                    {
                        // Time already passed, tomorrow:false — skip (same as handler)
                        nextTime = simTime; // no advance
                    }
                }
                else if (YamlPlayoutWaitUntilHandler.TryParseDayTime(
                    wu.WaitUntil, out DayOfWeek targetDay, out TimeOnly dayTime))
                {
                    // Day-name anchor
                    var simLocal = simTime.LocalDateTime;
                    int daysUntil = ((int)targetDay - (int)simLocal.DayOfWeek + 7) % 7;
                    if (daysUntil == 0)
                    {
                        var simTod = TimeOnly.FromDateTime(simLocal);
                        if (simTod > dayTime &&
                            !(simTod.Hour == 0 && simTod.Minute < 30))
                        {
                            daysUntil = 7;
                        }
                    }
                    nextTime = new DateTimeOffset(
                        DateOnly.FromDateTime(simLocal).AddDays(daysUntil),
                        dayTime, now.Offset);
                }
            }
            else if (instr is YamlPlayoutPadUntilInstruction pu)
            {
                if (TimeOnly.TryParse(pu.PadUntil, out TimeOnly padTime))
                {
                    var simLocal = simTime.LocalDateTime;
                    var simTod = TimeOnly.FromDateTime(simLocal);
                    if (padTime > simTod)
                    {
                        nextTime = new DateTimeOffset(
                            DateOnly.FromDateTime(simLocal), padTime, now.Offset);
                    }
                    else
                    {
                        // Already past — next day
                        nextTime = new DateTimeOffset(
                            DateOnly.FromDateTime(simLocal).AddDays(1), padTime, now.Offset);
                    }
                }
            }
            else if (instr is YamlPlayoutRepeatInstruction)
            {
                // Stop at repeat — don't simulate past it
                break;
            }

            if (nextTime.HasValue && nextTime.Value != simTime)
            {
                simTime = nextTime.Value;

                if (simTime <= now)
                {
                    // This anchor is in the past — record as best candidate
                    bestIndex = i + 1; // start AFTER this anchor
                    bestTime = simTime;
                }
                else
                {
                    // This anchor is in the future — we're in the block spanning now
                    break;
                }
            }
        }

        if (bestIndex > 0 || bestTime > todayMidnight)
        {
            context.InstructionIndex = bestIndex;
            context.CurrentTime = bestTime;
            logger.LogDebug(
                "SeekToCurrentTime: instruction={Idx} time={T} (now={Now})",
                bestIndex, bestTime, now);
        }
    }

    private static PlayoutBuildResult CleanUpHistory(
        PlayoutReferenceData referenceData,
        DateTimeOffset start,
        PlayoutBuildResult result)
    {
        var groups = new Dictionary<string, List<PlayoutHistory>>();
        foreach (PlayoutHistory history in referenceData.PlayoutHistory)
        {
            if (!groups.TryGetValue(history.Key, out List<PlayoutHistory> group))
            {
                group = [];
                groups[history.Key] = group;
            }

            group.Add(history);
        }

        foreach ((string _, List<PlayoutHistory> group) in groups)
        {
            //logger.LogDebug("History key {Key} has {Count} items in group", key, group.Count);
            Option<DateTime> whenToKeep = group
                .Filter(h => h.Finish < start.UtcDateTime)
                .OrderByDescending(h => h.When)
                .Map(h => h.When)
                .HeadOrNone();

            foreach (DateTime toKeep in whenToKeep)
            {
                // keep the most recent item from each history group that has already been played completely
                IEnumerable<PlayoutHistory> toDelete = group
                    .Filter(h => h.Finish < start.UtcDateTime)
                    .Filter(h => h.When != toKeep);

                foreach (PlayoutHistory delete in toDelete)
                {
                    result.HistoryToRemove.Add(delete.Id);
                }
            }
        }

        return result;
    }
}
