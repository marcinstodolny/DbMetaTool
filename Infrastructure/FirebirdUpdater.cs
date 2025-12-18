using FirebirdSql.Data.FirebirdClient;

namespace DbMetaTool.Infrastructure;

public class FirebirdUpdater
{
    private int _executedCount;
    private int _skippedCount;
    private readonly List<(string File, string Error)> Failures = new();


    public  void UpdateGroup(string scriptsDirectory, FbConnection connection, ScriptGroup scriptGroup)
    {
        Helpers.EnsureOpen(connection);
        if (Failures.Count > 0) return;
        var groupDirectory = Path.Combine(scriptsDirectory, scriptGroup.GetFolderName());
        var groupFiles = Helpers.GetSqlFiles(groupDirectory);

        ExecuteGroup(scriptGroup, groupFiles, connection);
    }

    public void Report()
    {
        Console.WriteLine();
        Console.WriteLine("RAPORT UPDATE-DB");
        Console.WriteLine($"Wykonane: {_executedCount}");
        Console.WriteLine($"Pominięte: {_skippedCount}");
        Console.WriteLine($"Błędy: {Failures.Count}");
        if (Failures.Count <= 0) return;
        Console.WriteLine();
        Console.WriteLine("Szczegóły błędów:");
        foreach (var failure in Failures)
        {
            Console.WriteLine($"Plik: {failure.File}");
            Console.WriteLine($"Błąd: {failure.Error}");
            Console.WriteLine();
        }

        throw new Exception("Update-db przerwany: wystąpiły błędy w skryptach.");
    }
    private void ExecuteGroup(ScriptGroup scriptGroup, IReadOnlyList<string> files, FbConnection connection)
    {
        Console.WriteLine($"Update: {scriptGroup.GetFolderName()} ({files.Count} plików)");

        foreach (var filePath in files)
        {
            var originalSql = File.ReadAllText(filePath);

            var sqlToRun = originalSql;

            if (scriptGroup == ScriptGroup.Domain)
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
            else if (scriptGroup == ScriptGroup.Table)
            {
                var tableNameFromHeader = TryExtractObjectName(originalSql, "TABLE");

                if (tableNameFromHeader == null)
                {
                    try
                    {
                        var parsed = ParseCreateTableColumns(originalSql);
                        tableNameFromHeader = parsed.TableName;
                    }
                    catch (Exception ex)
                    {
                        Failures.Add((filePath, ex.Message));
                        break;
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
                    break;
                }
            }
            else if (scriptGroup == ScriptGroup.Procedure)
            {
                sqlToRun = EnsureCreateOrAlterForProcedure(originalSql);
            }
            else
            {
                Failures.Add((filePath, $"Nieobsługiwany typ skryptu: {scriptGroup}"));
                break;
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

    private string StripLeadingEmptyAndCommentLines(string sqlText)
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

    private string? TryExtractObjectName(string sqlText, string objectKind)
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
        if (objectName.StartsWith('\"') && objectName.EndsWith('\"') && objectName.Length >= 2)
            objectName = objectName.Substring(1, objectName.Length - 2).Replace("\"\"", "\"");

        if (nameGroup.StartsWith('\"') && nameGroup.EndsWith('\"'))
            return objectName;

        return objectName.ToUpperInvariant(); 
    }

    private bool DomainExists(FbConnection connection, string domainName)
    {
        const string sql = @"SELECT 1 FROM RDB$FIELDS f WHERE TRIM(f.RDB$FIELD_NAME) = @name ROWS 1";
        using var cmd = new FbCommand(sql, connection);
        cmd.Parameters.AddWithValue("@name", domainName);
        return cmd.ExecuteScalar() != null;
    }

    private bool TableExists(FbConnection connection, string tableName)
    {
        const string sql = @"SELECT 1 FROM RDB$RELATIONS r WHERE TRIM(r.RDB$RELATION_NAME) = @name ROWS 1";
        using var cmd = new FbCommand(sql, connection);
        cmd.Parameters.AddWithValue("@name", tableName);
        return cmd.ExecuteScalar() != null;
    }

    private bool ProcedureExists(FbConnection connection, string procedureName)
    {
        const string sql = @"SELECT 1 FROM RDB$PROCEDURES p WHERE TRIM(p.RDB$PROCEDURE_NAME) = @name ROWS 1";
        using var cmd = new FbCommand(sql, connection);
        cmd.Parameters.AddWithValue("@name", procedureName);
        return cmd.ExecuteScalar() != null;
    }

    private string EnsureCreateOrAlterForProcedure(string sqlText)
    {
        var rgx = new System.Text.RegularExpressions.Regex(@"(?is)^\s*CREATE\s+PROCEDURE\b");
        return rgx.Replace(sqlText, "CREATE OR ALTER PROCEDURE", 1
        );
    }

    private (bool Success, string? ErrorMessage) ExecuteSingleStatementInTransaction(
        FbConnection connection,
        string sqlText)
    {
        var sqlToExecute = Helpers.NormalizeSqlForAdo(sqlText);
        if (string.IsNullOrWhiteSpace(sqlToExecute))
            return (true, null);

        using var transaction = BeginWaitTransaction(connection);
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

    private (bool Success, string? ErrorMessage) ExecuteStatementsInSingleTransaction(
        FbConnection connection,
        IReadOnlyList<string> statements)
    {
        using var transaction = BeginWaitTransaction(connection);
        try
        {
            foreach (var statement in statements)
            {
                var sql = Helpers.NormalizeSqlForAdo(statement);
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

    private (string TableToken, string TableName, List<(string ColumnToken, string ColumnName, string DefinitionSql)> Columns) ParseCreateTableColumns(string sqlText)
    {
        var clean = StripLeadingEmptyAndCommentLines(sqlText);

        var match = System.Text.RegularExpressions.Regex.Match(
            clean,
            @"(?is)^\s*CREATE\s+TABLE\s+(""[^""]+""|\w+)"
        );
        if (!match.Success)
            throw new InvalidOperationException("Nie udało się znaleźć nagłówka CREATE TABLE w pliku.");

        var tableToken = match.Groups[1].Value.Trim();
        var tableName = NormalizeIdentifierForComparison(tableToken);

        var startIndex = clean.IndexOf('(', match.Index + match.Length);
        if (startIndex < 0)
            throw new InvalidOperationException("Nie udało się znaleźć '(' listy kolumn w CREATE TABLE.");

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

    private string NormalizeIdentifierForComparison(string identifierToken)
    {
        identifierToken = identifierToken.Trim();
        if (!identifierToken.StartsWith('\"') ||
            !identifierToken.EndsWith('\"')) return identifierToken.ToUpperInvariant();
        var unquoted = identifierToken.Substring(1, identifierToken.Length - 2).Replace("\"\"", "\"");
        return unquoted;

    }

    private HashSet<string> ReadExistingColumnNames(
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

    private static FbTransaction BeginWaitTransaction(FbConnection connection)
    {
        var transactionOptions = new FbTransactionOptions
        {
            TransactionBehavior =
                FbTransactionBehavior.Concurrency |
                FbTransactionBehavior.Write |
                FbTransactionBehavior.Wait
        };

        return connection.BeginTransaction(transactionOptions);
    }
}