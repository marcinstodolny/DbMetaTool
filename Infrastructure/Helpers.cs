using System.Text;
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
        var withoutLineComments = RemoveLineCommentsOutsideStrings(withoutBlockComments);
        var normalizedNewLines = withoutLineComments.Replace("\r\n", "\n");
        var trimmedStart = normalizedNewLines.TrimStart();

        var withoutTrailingSemicolon = TrimTrailingSqlSemicolon(trimmedStart);
        return withoutTrailingSemicolon.Trim();
    }

    private static string RemoveLineCommentsOutsideStrings(string text)
    {
        var result = new StringBuilder(text.Length);
        var inSingleQuote = false;
        var inDoubleQuote = false;

        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];

            if (!inDoubleQuote && current == '\'')
            {
                result.Append(current);

                if (inSingleQuote)
                {
                    if (index + 1 < text.Length && text[index + 1] == '\'')
                    {
                        result.Append('\'');
                        index++;
                    }
                    else
                    {
                        inSingleQuote = false;
                    }
                }
                else
                {
                    inSingleQuote = true;
                }

                continue;
            }

            if (!inSingleQuote && current == '"')
            {
                result.Append(current);

                if (inDoubleQuote)
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        result.Append('"');
                        index++;
                    }
                    else
                    {
                        inDoubleQuote = false;
                    }
                }
                else
                {
                    inDoubleQuote = true;
                }

                continue;
            }

            if (!inSingleQuote && !inDoubleQuote && current == '-' && index + 1 < text.Length && text[index + 1] == '-')
            {
                var next = index + 2;
                while (next < text.Length && text[next] != '\n' && text[next] != '\r')
                {
                    next++;
                }

                if (next < text.Length)
                {
                    result.Append(text[next]);

                    if (text[next] == '\r' && next + 1 < text.Length && text[next + 1] == '\n')
                    {
                        result.Append(text[next + 1]);
                        next++;
                    }
                }

                index = next;
                continue;
            }

            result.Append(current);
        }

        return result.ToString();
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
