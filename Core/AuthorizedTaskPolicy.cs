namespace ClickClean.Core;

public sealed record AuthorizedTaskDefinition(string UserSid, int LogonType, int RunLevel, bool Enabled,
    bool AllowDemandStart, int TriggerCount, int ActionCount, int ActionType, string Path, string Arguments,
    string WorkingDirectory, string ExecutionTimeLimit, int MultipleInstances);

public static class AuthorizedTaskPolicy
{
    public const string NamePrefix = "ClickClean-Privileged-";
    public const string Arguments = "--pipe $(Arg0) --client $(Arg1)";
    public static bool IsValid(AuthorizedTaskDefinition task, string sid, string helper, string directory) =>
        task.UserSid == sid && task.LogonType == 3 && task.RunLevel == 1 && task.Enabled && task.AllowDemandStart &&
        task.TriggerCount == 0 && task.ActionCount == 1 && task.ActionType == 0 &&
        string.Equals(task.Path, helper, StringComparison.OrdinalIgnoreCase) && task.Arguments == Arguments &&
        string.Equals(task.WorkingDirectory, directory, StringComparison.OrdinalIgnoreCase) &&
        task.ExecutionTimeLimit == "PT0S" && task.MultipleInstances == 2;
}
