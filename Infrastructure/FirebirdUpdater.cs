using FirebirdSql.Data.FirebirdClient;

namespace DbMetaTool.Infrastructure;

public static class FirebirdUpdater
{
    private static int _executedCount;
    private static int _skippedCount;
    private static readonly List<(string File, string Error)> Failures = new();


    public static void UpdateDomains(string scriptsDirectory, FbConnection connection)
    {
        var domainsDir = Path.Combine(scriptsDirectory, "domains");
        var domainFiles = GetSqlFiles(domainsDir);

        ExecuteGroup("domains", domainFiles, connection);
    }

    static void ExecuteGroup(string groupName, IReadOnlyList<string> files, FbConnection connection)
    {
        Console.WriteLine($"Update: {groupName} ({files.Count} plików)");

        foreach (var filePath in files)
        {
            var originalSql = File.ReadAllText(filePath);

            var sqlToRun = originalSql;

            switch (groupName)
            {
                case "domains":
                    {
                        var domainName = TryExtractObjectName(originalSql, "DOMAIN");
                        if (domainName != null)
                        {
                            var isCreate = System.Text.RegularExpressions.Regex.IsMatch(
                                StripLeadingEmptyAndCommentLines(originalSql),
                                @"(?is)^\s*CREATE\s+DOMAIN\b"
                            );

                            if (isCreate && DomainExists(connection, domainName))
                            {
                                _skippedCount++;
                                continue;
                            }
                        }
                        break;
                    }
            }

            var result = ExecuteSingleStatementInTransaction(connection, sqlToRun);
            if (result.Success)
            {
                _executedCount++;
                continue;
            }

            Failures.Add((filePath, result.ErrorMessage ?? "Nieznany błąd"));

            break;
        }
    }

    static IReadOnlyList<string> GetSqlFiles(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            return Array.Empty<string>();

        return Directory.GetFiles(directoryPath, "*.sql", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    static string NormalizeSqlForAdo(string sqlText)
    {
        var trimmed = sqlText.Trim();
        if (trimmed.EndsWith(';'))
            trimmed = trimmed[..^1].TrimEnd();
        return trimmed;
    }

    static string StripLeadingEmptyAndCommentLines(string sqlText)
    {
        var lines = sqlText.Replace("\r\n", "\n").Split('\n');
        var index = 0;

        while (index < lines.Length)
        {
            var line = lines[index].Trim();

            if (line.Length == 0 || line.StartsWith("--"))
            {
                index++;
                continue;
            }

            break;
        }

        return string.Join("\n", lines.Skip(index));
    }

    static string? TryExtractObjectName(string sqlText, string objectKind)
    {
        var clean = StripLeadingEmptyAndCommentLines(sqlText);

        var pattern = objectKind switch
        {
            "DOMAIN" => @"(?is)^\s*(CREATE|ALTER)\s+DOMAIN\s+(""[^""]+""|\w+)",
            "TABLE" => @"(?is)^\s*CREATE\s+TABLE\s+(""[^""]+""|\w+)",
            "PROCEDURE" => @"(?is)^\s*CREATE\s+(OR\s+ALTER\s+)?PROCEDURE\s+(""[^""]+""|\w+)",
            _ => throw new ArgumentOutOfRangeException(nameof(objectKind))
        };

        var match = System.Text.RegularExpressions.Regex.Match(clean, pattern);
        if (!match.Success)
            return null;

        var nameGroup = match.Groups[2].Success ? match.Groups[2].Value : null;

        if (string.IsNullOrWhiteSpace(nameGroup))
            return null;

        var objectName = nameGroup.Trim();
        if (objectName.StartsWith("\"") && objectName.EndsWith("\"") && objectName.Length >= 2)
            objectName = objectName.Substring(1, objectName.Length - 2).Replace("\"\"", "\"");

        return objectName;
    }

    static bool DomainExists(FbConnection connection, string domainName)
    {
        const string sql = @"SELECT 1 FROM RDB$FIELDS f WHERE TRIM(f.RDB$FIELD_NAME) = @name ROWS 1";
        using var cmd = new FbCommand(sql, connection);
        cmd.Parameters.AddWithValue("@name", domainName);
        return cmd.ExecuteScalar() != null;
    }

    static (bool Success, string? ErrorMessage) ExecuteSingleStatementInTransaction(
        FbConnection connection,
        string sqlText)
    {
        var sqlToExecute = NormalizeSqlForAdo(sqlText);
        if (string.IsNullOrWhiteSpace(sqlToExecute))
            return (true, null);

        using var transaction = connection.BeginTransaction();
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sqlToExecute;
            command.CommandType = System.Data.CommandType.Text;
            command.ExecuteNonQuery();

            transaction.Commit();
            return (true, null);
        }
        catch (FbException fbEx)
        {
            try { transaction.Rollback(); } catch { /* ignore */ }
            return (false, fbEx.Message);
        }
        catch (Exception ex)
        {
            try { transaction.Rollback(); } catch { /* ignore */ }
            return (false, ex.Message);
        }
    }
}