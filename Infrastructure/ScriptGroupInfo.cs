namespace DbMetaTool.Infrastructure;

public enum ScriptGroup
{
    Domain,
    Table,
    Procedure
}

public static class ScriptGroupInfo
{
    private static readonly Dictionary<ScriptGroup, string> FolderNames =
        new()
        {
            [ScriptGroup.Domain] = "domains",
            [ScriptGroup.Table] = "tables",
            [ScriptGroup.Procedure] = "procedures",
        };

    public static string GetFolderName(this ScriptGroup group) => FolderNames[group];

    public static IReadOnlyList<ScriptGroup> ExecutionOrder { get; } = new[] { ScriptGroup.Domain, ScriptGroup.Table, ScriptGroup.Procedure };
}