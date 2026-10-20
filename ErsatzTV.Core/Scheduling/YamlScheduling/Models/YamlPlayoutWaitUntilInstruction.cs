using YamlDotNet.Serialization;

namespace ErsatzTV.Core.Scheduling.YamlScheduling.Models;

public class YamlPlayoutWaitUntilInstruction : YamlPlayoutInstruction
{
    [YamlMember(Alias = "wait_until", ApplyNamingConventions = false)]
    public string WaitUntil { get; set; }

    public bool Tomorrow { get; set; }

    [YamlMember(Alias = "rewind_on_reset", ApplyNamingConventions = false)]
    public bool RewindOnReset { get; set; }

    // When true and used with a day-name wait_until (e.g. "Tuesday 00:00"),
    // the instruction is silently skipped if that day has already passed in
    // the current week. This allows repeat: true to loop the full 7-day
    // playout without jumping ahead to next week mid-cycle.
    [YamlMember(Alias = "skip_if_passed", ApplyNamingConventions = false)]
    public bool SkipIfPassed { get; set; }
}
