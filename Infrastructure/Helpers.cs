namespace DbMetaTool.Infrastructure;

public static class Helpers
{
    public static IReadOnlyList<string> GetSqlFiles(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            return Array.Empty<string>();

        return Directory.GetFiles(directoryPath, "*.sql", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string NormalizeSqlForAdo(string sqlText)
    {
        var trimmed = sqlText.Trim();

        if (trimmed.EndsWith(';'))
            trimmed = trimmed[..^1].TrimEnd();

        return trimmed;
    }
}