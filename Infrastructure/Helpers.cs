using System.Text.RegularExpressions;
using FirebirdSql.Data.FirebirdClient;

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

    public static string ReadNormalizedSql(string filePath)
    {
        var sqlText = File.ReadAllText(filePath);
        return NormalizeSqlText(sqlText);
    }

    public static string NormalizeSqlText(string sqlText)
    {
        ArgumentNullException.ThrowIfNull(sqlText);

        var withoutBom = sqlText.TrimStart('\uFEFF');
        var withoutSetTerm = Regex.Replace(withoutBom, @"(?im)^\s*SET\s+TERM\b.*(?:\r?\n)?", string.Empty);
        var withoutBlockComments = Regex.Replace(withoutSetTerm, @"(?s)/\*.*?\*/", string.Empty);
        var withoutLineComments = Regex.Replace(withoutBlockComments, @"--.*?(?:\r?\n|$)", string.Empty);
        var normalizedNewLines = withoutLineComments.Replace("\r\n", "\n");
        var trimmedStart = normalizedNewLines.TrimStart();

        var withoutTrailingSemicolon = TrimTrailingSqlSemicolon(trimmedStart);
        return withoutTrailingSemicolon.Trim();
    }

    public static string TrimTrailingSqlSemicolon(string sqlText)
    {
        var trimmed = sqlText.Trim();

        if (trimmed.EndsWith(';'))
            trimmed = trimmed[..^1].TrimEnd();

        return trimmed;
    }

    public static string BuildConnectionString(string databaseFilePath)
    {

        var user = Environment.GetEnvironmentVariable("FB_USER") ?? "SYSDBA";
        var password = Environment.GetEnvironmentVariable("FB_PASSWORD");
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("FB_PASSWORD is not set. Set environment variable FB_PASSWORD before running build-db.");

        var host = Environment.GetEnvironmentVariable("FB_HOST") ?? "localhost";

        return $"User={user};Password={password};Database={databaseFilePath};DataSource={host};Dialect=3;Charset=UTF8;";
    }

    public static void EnsureOpen(FbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
        }
    }

    public static FbConnection CreateAndOpenConnection(string connectionString)
    {
        var connection = new FbConnection(connectionString);
        try
        {
            connection.Open();
            return connection;
        }
        catch (FbException exception)
        {
            connection.Dispose();
            throw new InvalidOperationException($"nie udało się otworzyć połączenia (sprawdź connection string).", exception);
        }
    }
}
