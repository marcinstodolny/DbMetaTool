using FirebirdSql.Data.FirebirdClient;

namespace DbMetaTool.Infrastructure;

public static class FirebirdUpdater
{
    private static int _executedCount;
    private static int _skippedCount;
    private static readonly List<(string File, string Error)> Failures = new();


    public static void UpdateGroup(string scriptsDirectory, FbConnection connection, GroupName groupName)
    {
        var domainsDir = Path.Combine(scriptsDirectory, groupName.Value);
        var domainFiles = GetSqlFiles(domainsDir);

        ExecuteGroup(groupName, domainFiles, connection);
    }

    static void ExecuteGroup(GroupName groupName, IReadOnlyList<string> files, FbConnection connection)
    {
        Console.WriteLine($"Update: {groupName.Value} ({files.Count} plików)");

        foreach (var filePath in files)
        {
            var originalSql = File.ReadAllText(filePath);

            var sqlToRun = originalSql;

            if (groupName == GroupName.Domain)
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
            }
            else if (groupName == GroupName.Table)
            {
                var tableNameFromHeader = TryExtractObjectName(originalSql, "TABLE");

                if (tableNameFromHeader == null)
                {
                    try
                    {
                        var parsed = ParseCreateTableColumns(originalSql);
                        tableNameFromHeader = parsed.TableName;
                    }
                    catch
                    {
                        // TODO
                    }
                }

                if (tableNameFromHeader != null && TableExists(connection, tableNameFromHeader))
                {
                    var parsed = ParseCreateTableColumns(originalSql);
                    var existingColumns = ReadExistingColumnNames(connection, parsed.TableName);

                    var alterStatements = new List<string>();

                    foreach (var column in parsed.Columns)
                    {
                        var alreadyExists = existingColumns.Contains(column.ColumnName);
                        if (alreadyExists)
                            continue;

                        var alterSql =
                            $"ALTER TABLE {parsed.TableToken} ADD {column.ColumnToken} {column.DefinitionSql}";
                        alterStatements.Add(alterSql);
                    }

                    if (alterStatements.Count == 0)
                    {
                        _skippedCount++;
                        continue;
                    }

                    var alterResult = ExecuteStatementsInSingleTransaction(connection, alterStatements);
                    if (alterResult.Success)
                    {
                        _executedCount++;
                        continue;
                    }

                    Failures.Add((filePath, alterResult.ErrorMessage ?? "Nieznany błąd"));
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

    static bool TableExists(FbConnection connection, string tableName)
    {
        const string sql = @"SELECT 1 FROM RDB$RELATIONS r WHERE TRIM(r.RDB$RELATION_NAME) = @name ROWS 1";
        using var cmd = new FbCommand(sql, connection);
        cmd.Parameters.AddWithValue("@name", tableName);
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

    static (bool Success, string? ErrorMessage) ExecuteStatementsInSingleTransaction(
        FbConnection connection,
        IReadOnlyList<string> statements)
    {
        using var transaction = connection.BeginTransaction();
        try
        {
            foreach (var statement in statements)
            {
                var sql = NormalizeSqlForAdo(statement);
                if (string.IsNullOrWhiteSpace(sql))
                    continue;

                using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
            return (true, null);
        }
        catch (FbException fbEx)
        {
            try { transaction.Rollback(); } catch { }
            return (false, fbEx.Message);
        }
        catch (Exception ex)
        {
            try { transaction.Rollback(); } catch { }
            return (false, ex.Message);
        }
    }

    static (string TableToken, string TableName, List<(string ColumnToken, string ColumnName, string DefinitionSql)> Columns)
                ParseCreateTableColumns(string sqlText)
    {
        var clean = StripLeadingEmptyAndCommentLines(sqlText);

        var match = System.Text.RegularExpressions.Regex.Match(
            clean,
            @"(?is)^\s*CREATE\s+TABLE\s+(""[^""]+""|\w+)"
        );
        if (!match.Success)
            throw new InvalidOperationException("Nie umiem znaleźć nagłówka CREATE TABLE w pliku.");

        var tableToken = match.Groups[1].Value.Trim();
        var tableName = NormalizeIdentifierForComparison(tableToken);

        var startIndex = clean.IndexOf('(', match.Index + match.Length);
        if (startIndex < 0)
            throw new InvalidOperationException("Nie umiem znaleźć '(' listy kolumn w CREATE TABLE.");

        var items = new List<string>();
        var current = new System.Text.StringBuilder();

        var inSingleQuotes = false;
        var inDoubleQuotes = false;
        var depth = 0;

        for (var i = startIndex; i < clean.Length; i++)
        {
            var ch = clean[i];

            if (!inDoubleQuotes && ch == '\'')
            {
                inSingleQuotes = !inSingleQuotes;
                current.Append(ch);
                continue;
            }

            if (!inSingleQuotes && ch == '"')
            {
                if (inDoubleQuotes && i + 1 < clean.Length && clean[i + 1] == '"')
                {
                    current.Append("\"\"");
                    i++;
                    continue;
                }

                inDoubleQuotes = !inDoubleQuotes;
                current.Append(ch);
                continue;
            }

            if (!inSingleQuotes && !inDoubleQuotes)
            {
                if (ch == '(')
                {
                    depth++;
                    if (depth > 1) current.Append(ch);
                    continue;
                }

                if (ch == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        var last = current.ToString().Trim();
                        if (last.Length > 0) items.Add(last);
                        break;
                    }

                    current.Append(ch);
                    continue;
                }

                if (ch == ',' && depth == 1)
                {
                    var item = current.ToString().Trim();
                    if (item.Length > 0) items.Add(item);
                    current.Clear();
                    continue;
                }
            }
            if (depth >= 1) current.Append(ch);
        }

        var columns = new List<(string ColumnToken, string ColumnName, string DefinitionSql)>();

        foreach (var rawItem in items)
        {
            var item = rawItem.Trim();

            if (item.StartsWith("CONSTRAINT", StringComparison.OrdinalIgnoreCase) ||
                item.StartsWith("PRIMARY", StringComparison.OrdinalIgnoreCase) ||
                item.StartsWith("UNIQUE", StringComparison.OrdinalIgnoreCase) ||
                item.StartsWith("FOREIGN", StringComparison.OrdinalIgnoreCase) ||
                item.StartsWith("CHECK", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var colMatch = System.Text.RegularExpressions.Regex.Match(item, @"(?is)^\s*(""[^""]+""|\w+)\s+(.*)$");
            if (!colMatch.Success)
                continue;

            var columnToken = colMatch.Groups[1].Value.Trim();
            var columnName = NormalizeIdentifierForComparison(columnToken);
            var definitionSql = colMatch.Groups[2].Value.Trim();

            if (definitionSql.Length == 0)
                continue;

            columns.Add((columnToken, columnName, definitionSql));
        }

        return (tableToken, tableName, columns);
    }

    static string NormalizeIdentifierForComparison(string identifierToken)
    {
        identifierToken = identifierToken.Trim();
        if (!identifierToken.StartsWith("\"", StringComparison.Ordinal) ||
            !identifierToken.EndsWith("\"", StringComparison.Ordinal)) return identifierToken.ToUpperInvariant();
        var unquoted = identifierToken.Substring(1, identifierToken.Length - 2).Replace("\"\"", "\"");
        return unquoted;

    }

    static HashSet<string> ReadExistingColumnNames(
        FbConnection connection,
        string tableName)
    {
        const string sql = @"
            SELECT TRIM(rf.RDB$FIELD_NAME) AS COL_NAME
            FROM RDB$RELATION_FIELDS rf
            WHERE TRIM(rf.RDB$RELATION_NAME) = @tableName";

        using var cmd = new FbCommand(sql, connection);
        cmd.Parameters.AddWithValue("@tableName", tableName);

        var result = new HashSet<string>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var colName = Convert.ToString(reader["COL_NAME"])!.Trim();
            result.Add(colName);
        }

        return result;
    }
}