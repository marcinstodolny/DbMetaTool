using FirebirdSql.Data.FirebirdClient;
using System.Text.RegularExpressions;

namespace DbMetaTool.Infrastructure;

public class FirebirdUpdater
{
    private int _executedCount;
    private int _skippedCount;
    private bool _dryRun;
    private readonly List<(string File, string Error)> _failures = new();
    private readonly List<string> _addedColumns = new();
    private readonly List<string> _alteredColumns = new();
    private readonly List<string> _droppedColumns = new();
    private readonly List<string> _addedTables = new();
    private readonly List<string> _alteredTables = new();
    private readonly List<string> _droppedTables = new();
    private readonly List<string> _addedProcedures = new();
    private readonly List<string> _alteredProcedures = new();
    private readonly List<string> _droppedProcedures = new();
    private readonly List<string> _addedDomains = new();
    private readonly List<string> _alteredDomains = new();
    private readonly List<string> _droppedDomains = new();
    private readonly List<(string Statement, string TableToken, string TableName, string ColumnToken, string ColumnName)> _deferredColumnDrops = new();
    private readonly HashSet<string> _deferredColumnDropKeys = new(StringComparer.Ordinal);
    private readonly List<string> _columnDropCandidates = new();
    private readonly List<string> _domainDropCandidates = new();
    private readonly List<string> _tableDropCandidates = new();
    private readonly List<string> _procedureDropCandidates = new();
    private readonly List<string> _dryRunPlanStatements = new();
    private readonly List<string> _dryRunDependencyBlocks = new();


    public void UpdateTwoPhases(string scriptsDirectory, FbConnection connection, bool destructiveEnabled, bool dryRun)
    {
        Helpers.EnsureOpen(connection);
        _dryRun = dryRun;
        if (_failures.Count > 0) return;

        var domainFiles = Helpers.GetSqlFiles(Path.Combine(scriptsDirectory, ScriptGroup.Domain.GetFolderName()));
        var tableFiles = Helpers.GetSqlFiles(Path.Combine(scriptsDirectory, ScriptGroup.Table.GetFolderName()));
        var procedureFiles = Helpers.GetSqlFiles(Path.Combine(scriptsDirectory, ScriptGroup.Procedure.GetFolderName()));

        var targetDomains = ReadTargetNamesFromFiles(domainFiles, "DOMAIN");
        var targetTables = ReadTargetTablesFromFiles(tableFiles);
        if (_failures.Count > 0) return;
        var targetProcedures = ReadTargetNamesFromFiles(procedureFiles, "PROCEDURE");

        ExecuteGroup(ScriptGroup.Domain, domainFiles, connection);
        if (_failures.Count > 0) return;

        ExecuteGroup(ScriptGroup.Table, tableFiles, connection);
        if (_failures.Count > 0) return;

        ExecuteGroup(ScriptGroup.Procedure, procedureFiles, connection, procedureStubOnly: true, countAsExecutedFile: false);
        if (_failures.Count > 0) return;

        ExecuteGroup(ScriptGroup.Procedure, procedureFiles, connection);
        if (_failures.Count > 0) return;

        ApplyDeferredColumnDrops(connection, destructiveEnabled);
        if (_failures.Count > 0) return;

        DropMissingObjects(connection, targetDomains, targetTables, targetProcedures, destructiveEnabled);
    }

    public void Report()
    {
        Console.WriteLine();
        Console.WriteLine("RAPORT UPDATE-DB");
        Console.WriteLine($"Wykonane: {_executedCount}");
        Console.WriteLine($"Pominięte: {_skippedCount}");
        Console.WriteLine($"Błędy: {_failures.Count}");
        if (_dryRun)
        {
            Console.WriteLine();
            Console.WriteLine("TRYB DRY-RUN");
            Console.WriteLine("Plan:");
            if (_dryRunPlanStatements.Count > 0)
            {
                foreach (var stmt in _dryRunPlanStatements) Console.WriteLine($"- {stmt}");
            }
            else
            {
                Console.WriteLine("- brak zarejestrowanych instrukcji");
            }

            if (_dryRunDependencyBlocks.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Blokady zależności:");
                foreach (var block in _dryRunDependencyBlocks) Console.WriteLine($"- {block}");
            }

            if (_domainDropCandidates.Count > 0 || _tableDropCandidates.Count > 0 || _procedureDropCandidates.Count > 0 || _columnDropCandidates.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Kandydaci do usunięcia (destrukcja wyłączona - zmienna FB_DESTRUCTIVE != 1):");
                if (_domainDropCandidates.Count > 0)
                {
                    Console.WriteLine("Domeny:");
                    foreach (var dom in _domainDropCandidates) Console.WriteLine($"- {dom}");
                }
                if (_tableDropCandidates.Count > 0)
                {
                    Console.WriteLine("Tabele:");
                    foreach (var tbl in _tableDropCandidates) Console.WriteLine($"- {tbl}");
                }
                if (_procedureDropCandidates.Count > 0)
                {
                    Console.WriteLine("Procedury:");
                    foreach (var proc in _procedureDropCandidates) Console.WriteLine($"- {proc}");
                }
                if (_columnDropCandidates.Count > 0)
                {
                    Console.WriteLine("Kolumny:");
                    foreach (var col in _columnDropCandidates) Console.WriteLine($"- {col}");
                }
            }

            if (_failures.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Szczegóły błędów:");
                foreach (var failure in _failures)
                {
                    Console.WriteLine($"Plik: {failure.File}");
                    Console.WriteLine($"Błąd: {failure.Error}");
                    Console.WriteLine();
                }

                throw new Exception("Update-db przerwany: wystąpiły błędy w skryptach.");
            }

            return;
        }
        if (_addedDomains.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Dodane domeny:");
            foreach (var dom in _addedDomains) Console.WriteLine($"- {dom}");
        }
        if (_alteredDomains.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Zmodyfikowane domeny:");
            foreach (var dom in _alteredDomains) Console.WriteLine($"- {dom}");
        }
        if (_droppedDomains.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Usunięte domeny:");
            foreach (var dom in _droppedDomains) Console.WriteLine($"- {dom}");
        }
        if (_addedTables.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Dodane tabele:");
            foreach (var tbl in _addedTables) Console.WriteLine($"- {tbl}");
        }
        if (_alteredTables.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Zmodyfikowane tabele:");
            foreach (var tbl in _alteredTables) Console.WriteLine($"- {tbl}");
        }
        if (_droppedTables.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Usunięte tabele:");
            foreach (var tbl in _droppedTables) Console.WriteLine($"- {tbl}");
        }
        if (_addedProcedures.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Dodane procedury:");
            foreach (var proc in _addedProcedures) Console.WriteLine($"- {proc}");
        }
        if (_alteredProcedures.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Zmodyfikowane procedury:");
            foreach (var proc in _alteredProcedures) Console.WriteLine($"- {proc}");
        }
        if (_droppedProcedures.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Usunięte procedury:");
            foreach (var proc in _droppedProcedures) Console.WriteLine($"- {proc}");
        }
        if (_addedColumns.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Dodane kolumny:");
            foreach (var col in _addedColumns) Console.WriteLine($"- {col}");
        }
        if (_alteredColumns.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Zmodyfikowane kolumny:");
            foreach (var col in _alteredColumns) Console.WriteLine($"- {col}");
        }
        if (_droppedColumns.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Usunięte kolumny:");
            foreach (var col in _droppedColumns) Console.WriteLine($"- {col}");
        }
        if (_domainDropCandidates.Count > 0 || _tableDropCandidates.Count > 0 || _procedureDropCandidates.Count > 0 || _columnDropCandidates.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Kandydaci do usunięcia (destrukcja wyłączona - zmienna FB_DESTRUCTIVE != 1):");
            if (_domainDropCandidates.Count > 0)
            {
                Console.WriteLine("Domeny:");
                foreach (var dom in _domainDropCandidates) Console.WriteLine($"- {dom}");
            }
            if (_tableDropCandidates.Count > 0)
            {
                Console.WriteLine("Tabele:");
                foreach (var tbl in _tableDropCandidates) Console.WriteLine($"- {tbl}");
            }
            if (_procedureDropCandidates.Count > 0)
            {
                Console.WriteLine("Procedury:");
                foreach (var proc in _procedureDropCandidates) Console.WriteLine($"- {proc}");
            }
            if (_columnDropCandidates.Count > 0)
            {
                Console.WriteLine("Kolumny:");
                foreach (var col in _columnDropCandidates) Console.WriteLine($"- {col}");
            }
        }
        if (_failures.Count <= 0) return;
        Console.WriteLine();
        Console.WriteLine("Szczegóły błędów:");
        foreach (var failure in _failures)
        {
            Console.WriteLine($"Plik: {failure.File}");
            Console.WriteLine($"Błąd: {failure.Error}");
            Console.WriteLine();
        }

        throw new Exception("Update-db przerwany: wystąpiły błędy w skryptach.");
    }
    private void ApplyDeferredColumnDrops(FbConnection connection, bool destructiveEnabled)
    {
        if (_deferredColumnDrops.Count == 0)
            return;

        if (!destructiveEnabled)
        {
            foreach (var drop in _deferredColumnDrops)
                _columnDropCandidates.Add($"{drop.TableToken}.{drop.ColumnToken}");

            if (!_dryRun)
            {
                Console.WriteLine();
                Console.WriteLine("FB_DESTRUCTIVE != \"1\" - pomijam DROP brakujących kolumn (lista kandydatów w raporcie).");
                if (_columnDropCandidates.Count == 0)
                {
                    Console.WriteLine("Brak kandydatów do usunięcia kolumn.");
                }
            }

            _deferredColumnDrops.Clear();
            _deferredColumnDropKeys.Clear();
            return;
        }

        var blocked = new List<string>();
        var toExecute = new List<string>();
        var toExecuteSet = new HashSet<string>(StringComparer.Ordinal);

        foreach (var drop in _deferredColumnDrops)
        {
            if (ColumnHasDependencies(connection, drop.TableName, drop.ColumnName))
            {
                blocked.Add($"{drop.TableToken}.{drop.ColumnToken}");
                continue;
            }
            toExecute.Add(drop.Statement);
            toExecuteSet.Add(drop.Statement);
        }

        if (toExecute.Count > 0)
        {
            var result = ExecuteStatementsInSingleTransaction(connection, toExecute);
            if (!result.Success)
            {
                _failures.Add(("", $"{result.ErrorMessage ?? "Nieznany błąd podczas usuwania kolumn."} SQL: {toExecute.FirstOrDefault()}"));
            }
            else
            {
                foreach (var drop in _deferredColumnDrops)
                {
                    if (toExecuteSet.Contains(drop.Statement))
                        _droppedColumns.Add($"{drop.TableToken}.{drop.ColumnToken}");
                }
                if (!_dryRun)
                    _executedCount += toExecute.Count;
            }
        }

        foreach (var b in blocked)
        {
            var message = $"Pominięto DROP kolumny {b}: istnieją zależne obiekty.";
            if (_dryRun)
                _dryRunDependencyBlocks.Add(message);
            else
                _failures.Add(("", message));
        }

        _deferredColumnDrops.Clear();
        _deferredColumnDropKeys.Clear();
    }
    private void ExecuteGroup(
        ScriptGroup scriptGroup,
        IReadOnlyList<string> files,
        FbConnection connection,
        bool procedureStubOnly = false,
        bool countAsExecutedFile = true)
    {
        if (!_dryRun)
            Console.WriteLine($"Update: {scriptGroup.GetFolderName()} ({files.Count} plików)");

        foreach (var filePath in files)
        {
            var originalSql = File.ReadAllText(filePath);

            var sqlToRun = originalSql;
            var normalizedSql = StripLeadingEmptyAndCommentLines(originalSql);
            string? tableNameFromHeader = null;
            string? procedureName = null;
            bool isDropProcedure = false;
            bool isDropTable = false;
            bool existedTableBefore = false;
            bool existedProcedureBefore = false;
            bool existedDomainBefore = false;
            bool isDropDomain = false;
            bool isCreateDomain = false;
            DomainDefinition? domainDefinition = null;

            if (scriptGroup == ScriptGroup.Domain)
            {
                var domainName = TryExtractObjectName(originalSql, "DOMAIN") ?? TryExtractDropObjectName(originalSql, "DOMAIN");
                if (domainName != null)
                {
                    isCreateDomain = System.Text.RegularExpressions.Regex.IsMatch(
                        StripLeadingEmptyAndCommentLines(originalSql),
                        @"(?is)^\s*CREATE\s+DOMAIN\b"
                    );

                    isDropDomain = IsDropStatement(normalizedSql, "DOMAIN");
                    existedDomainBefore = DomainExists(connection, domainName);

                    if (!isDropDomain)
                    {
                        try
                        {
                            domainDefinition = ParseDomainDefinition(originalSql, domainName);
                        }
                        catch (Exception ex)
                        {
                            _failures.Add((filePath, ex.Message));
                            break;
                        }
                    }

                    if (isCreateDomain && existedDomainBefore)
                    {
                        if (domainDefinition == null)
                        {
                            _failures.Add((filePath, "Nie udało się sparsować definicji domeny."));
                            break;
                        }

                        var existingDomain = ReadExistingDomain(connection, domainName);
                        var (domainStatements, desc) = BuildAlterStatementsForDomain(domainDefinition, existingDomain);

                        if (domainStatements.Count == 0)
                        {
                            _skippedCount++;
                            continue;
                        }

                        var alterResult = ExecuteStatementsInSingleTransaction(connection, domainStatements);
                        if (alterResult.Success)
                        {
                            _alteredDomains.Add(domainName + (string.IsNullOrWhiteSpace(desc) ? "" : $" ({desc})"));
                            if (!_dryRun)
                                _executedCount++;
                            continue;
                        }

                        _failures.Add((filePath, alterResult.ErrorMessage ?? "Nieznany błąd"));
                        break;
                    }

                    if (!isCreateDomain && isDropDomain)
                    {
                        if (DomainHasDependencies(connection, domainName))
                        {
                            var message = $"Nie można usunąć domeny {domainName}: istnieją zależne obiekty.";
                            if (_dryRun)
                                _dryRunDependencyBlocks.Add(message);
                            else
                                _failures.Add((filePath, message));

                            if (_dryRun)
                            {
                                _skippedCount++;
                                continue;
                            }

                            break;
                        }
                    }

                    var domainResult = ExecuteSingleStatementInTransaction(connection, sqlToRun);
                    if (domainResult.Success)
                    {
                        if (isDropDomain) _droppedDomains.Add(domainName);
                        else if (isCreateDomain && !existedDomainBefore) _addedDomains.Add(domainName);
                        else if (existedDomainBefore) _alteredDomains.Add(domainName);
                        else _addedDomains.Add(domainName);

                        if (!_dryRun)
                            _executedCount++;
                        continue;
                    }

                    _failures.Add((filePath, domainResult.ErrorMessage ?? "Nieznany błąd"));
                    break;
                }
            }
            else if (scriptGroup == ScriptGroup.Table)
            {
                isDropTable = IsDropStatement(normalizedSql, "TABLE");
                tableNameFromHeader = TryExtractObjectName(originalSql, "TABLE") ?? TryExtractDropObjectName(originalSql, "TABLE");
                if (tableNameFromHeader != null)
                    existedTableBefore = TableExists(connection, tableNameFromHeader);

                if (isDropTable && tableNameFromHeader != null)
                {
                    if (HasTableDependencies(connection, tableNameFromHeader))
                    {
                        var message = $"Nie można usunąć tabeli {tableNameFromHeader}: istnieją zależne obiekty.";
                        if (_dryRun)
                            _dryRunDependencyBlocks.Add(message);
                        else
                            _failures.Add((filePath, message));

                        if (_dryRun)
                        {
                            _skippedCount++;
                            continue;
                        }

                        break;
                    }

                    var dropResult = ExecuteSingleStatementInTransaction(connection, sqlToRun);
                    if (dropResult.Success)
                    {
                        _droppedTables.Add(tableNameFromHeader);
                        if (!_dryRun)
                            _executedCount++;
                        continue;
                    }

                    _failures.Add((filePath, dropResult.ErrorMessage ?? "Nieznany błąd"));
                    break;
                }

                if (tableNameFromHeader == null)
                {
                    try
                    {
                        var parsed = ParseCreateTableColumns(originalSql);
                        tableNameFromHeader = parsed.TableName;
                        existedTableBefore = TableExists(connection, tableNameFromHeader);
                    }
                    catch (Exception ex)
                    {
                        _failures.Add((filePath, ex.Message));
                        break;
                    }
                }

                if (tableNameFromHeader != null && existedTableBefore)
                {
                    var parsed = ParseCreateTableColumns(originalSql);
                    var existingColumns = ReadExistingColumns(connection, parsed.TableName);

                    var alterStatements = new List<string>();
                    var addedThisTable = new List<string>();
                    var alteredThisTable = new List<string>();
                    var droppedThisTable = new List<string>();

                    foreach (var column in parsed.Columns)
                    {
                        if (!existingColumns.TryGetValue(column.ColumnName, out var existingColumn))
                        {
                            var alterSql =
                                $"ALTER TABLE {parsed.TableToken} ADD {column.ColumnToken} {column.DefinitionSql}";
                            alterStatements.Add(alterSql);
                            addedThisTable.Add($"{parsed.TableToken}.{column.ColumnToken}");
                            continue;
                        }

                        var (columnAlterations, changeDescription) = BuildAlterStatementsForColumn(
                            parsed.TableToken, column, existingColumn);
                        alterStatements.AddRange(columnAlterations);
                        if (!string.IsNullOrWhiteSpace(changeDescription))
                            alteredThisTable.Add($"{parsed.TableToken}.{column.ColumnToken} ({changeDescription})");
                    }

                    foreach (var existing in existingColumns.Keys)
                    {
                        var missing = parsed.Columns.All(c => !string.Equals(c.ColumnName, existing, StringComparison.Ordinal));
                        if (!missing) continue;

                        var existingColumn = existingColumns[existing];
                        var columnToken = existingColumn.ColumnToken ?? BuildIdentifierToken(existingColumn.ColumnName);
                        var dropKey = $"{parsed.TableName}|{existingColumn.ColumnName}";
                        if (_deferredColumnDropKeys.Add(dropKey))
                        {
                            var dropStmt = $"ALTER TABLE {parsed.TableToken} DROP {columnToken}";
                            _deferredColumnDrops.Add((dropStmt, parsed.TableToken, parsed.TableName, columnToken, existingColumn.ColumnName));
                        }
                    }

                    if (alterStatements.Count == 0)
                    {
                        _skippedCount++;
                        continue;
                    }

                    var alterResult = ExecuteStatementsInSingleTransaction(connection, alterStatements);
                    if (alterResult.Success)
                    {
                        _addedColumns.AddRange(addedThisTable);
                        _alteredColumns.AddRange(alteredThisTable);
                        if (alterStatements.Count > 0)
                            _alteredTables.Add(parsed.TableToken);
                        if (!_dryRun)
                            _executedCount++;
                        continue;
                    }

                    _failures.Add((filePath, alterResult.ErrorMessage ?? "Nieznany błąd"));
                    break;
                }
            }
            else if (scriptGroup == ScriptGroup.Procedure)
            {
                isDropProcedure = IsDropStatement(normalizedSql, "PROCEDURE");
                procedureName = TryExtractObjectName(originalSql, "PROCEDURE") ?? TryExtractDropObjectName(originalSql, "PROCEDURE");
                if (procedureName != null)
                    existedProcedureBefore = ProcedureExists(connection, procedureName);

                if (procedureStubOnly && isDropProcedure)
                {
                    _skippedCount++;
                    continue;
                }

                try
                {
                    sqlToRun = procedureStubOnly
                        ? BuildProcedureStubOrThrow(originalSql)
                        : EnsureCreateOrAlterForProcedure(originalSql);
                }
                catch (Exception ex)
                {
                    _failures.Add((filePath, ex.Message));
                    break;
                }

                if (!procedureStubOnly && procedureName != null)
                {
                    if (isDropProcedure)
                    {
                        if (HasProcedureDependencies(connection, procedureName))
                        {
                            var message = $"Nie można usunąć procedury {procedureName}: istnieją zależne obiekty.";
                            if (_dryRun)
                                _dryRunDependencyBlocks.Add(message);
                            else
                                _failures.Add((filePath, message));

                            if (_dryRun)
                            {
                                _skippedCount++;
                                continue;
                            }

                            break;
                        }
                    }
                }
            }
            else
            {
                _failures.Add((filePath, $"Nieobsługiwany typ skryptu: {scriptGroup}"));
                break;
            }

            var result = ExecuteSingleStatementInTransaction(connection, sqlToRun);
            if (result.Success)
            {
                if (!_dryRun && countAsExecutedFile) _executedCount++;
                if (scriptGroup == ScriptGroup.Table && tableNameFromHeader != null)
                {
                    if (!existedTableBefore) _addedTables.Add(tableNameFromHeader);
                }

                if (scriptGroup == ScriptGroup.Procedure && !procedureStubOnly && procedureName != null && !isDropProcedure)
                {
                    if (existedProcedureBefore) _alteredProcedures.Add(procedureName);
                    else _addedProcedures.Add(procedureName);
                }
                if (scriptGroup == ScriptGroup.Procedure && !procedureStubOnly && procedureName != null && isDropProcedure)
                {
                    _droppedProcedures.Add(procedureName);
                }
                continue;
            }

            _failures.Add((filePath, result.ErrorMessage ?? "Nieznany błąd"));

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
    private string? TryExtractDropObjectName(string sqlText, string objectKind)
    {
        var clean = StripLeadingEmptyAndCommentLines(sqlText);

        var pattern = objectKind switch
        {
            "TABLE" => @"(?is)^\s*DROP\s+TABLE\s+(""[^""]+""|\w+)",
            "PROCEDURE" => @"(?is)^\s*DROP\s+PROCEDURE\s+(""[^""]+""|\w+)",
            "DOMAIN" => @"(?is)^\s*DROP\s+DOMAIN\s+(""[^""]+""|\w+)",
            _ => throw new ArgumentOutOfRangeException(nameof(objectKind))
        };

        var match = Regex.Match(clean, pattern);
        if (!match.Success) return null;

        var nameGroup = match.Groups[1].Value.Trim();
        if (string.IsNullOrWhiteSpace(nameGroup)) return null;

        var objectName = nameGroup;
        if (objectName.StartsWith('\"') && objectName.EndsWith('\"') && objectName.Length >= 2)
            objectName = objectName.Substring(1, objectName.Length - 2).Replace("\"\"", "\"");

        return objectName.StartsWith('\"') ? objectName : objectName.ToUpperInvariant();
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
        var clean = StripLeadingEmptyAndCommentLines(sqlText);
        var rgx = new System.Text.RegularExpressions.Regex(@"(?is)^\s*CREATE\s+PROCEDURE\b");
        return rgx.Replace(clean, "CREATE OR ALTER PROCEDURE", 1);
    }
    private DomainDefinition ParseDomainDefinition(string sqlText, string domainName)
    {
        var match = Regex.Match(sqlText, @"(?is)^\s*(CREATE|ALTER)\s+DOMAIN\s+(""[^""]+""|\w+)\s+AS\s+(?<rest>.+)$");
        if (!match.Success)
            throw new InvalidOperationException("Nie udało się znaleźć nagłówka CREATE/ALTER DOMAIN.");

        var rest = match.Groups["rest"].Value.Trim();
        if (string.IsNullOrWhiteSpace(rest))
            throw new InvalidOperationException("Brak definicji typu dla domeny.");

        var keywordIndex = FindFirstKeywordIndex(rest);
        var typeSql = keywordIndex >= 0 ? rest[..keywordIndex].Trim() : rest.Trim();
        var remainder = keywordIndex >= 0 ? rest[keywordIndex..].Trim() : string.Empty;

        var defaultExpression = ExtractDefaultExpression(remainder, out var remainderWithoutDefault);
        var isNullable = DetermineNullability(remainderWithoutDefault);

        var typeDefinition = ParseTypeSql(typeSql);

        return new DomainDefinition(
            domainName,
            typeDefinition,
            isNullable,
            NormalizeDefaultExpression(defaultExpression));
    }
    private bool HasTableDependencies(FbConnection connection, string tableName)
    {
        const string sql = @"SELECT 1 FROM RDB$DEPENDENCIES d WHERE TRIM(d.RDB$DEPENDED_ON_NAME) = @name ROWS 1";
        using var cmd = new FbCommand(sql, connection);
        cmd.Parameters.AddWithValue("@name", tableName);
        return cmd.ExecuteScalar() != null;
    }

    private bool ColumnHasDependencies(FbConnection connection, string tableName, string columnName)
    {
        const string sql = @"
            SELECT 1
            FROM RDB$DEPENDENCIES d
            WHERE TRIM(d.RDB$DEPENDED_ON_NAME) = @table
              AND TRIM(d.RDB$FIELD_NAME) = @column
            ROWS 1";
        using var cmd = new FbCommand(sql, connection);
        cmd.Parameters.AddWithValue("@table", tableName);
        cmd.Parameters.AddWithValue("@column", columnName);
        return cmd.ExecuteScalar() != null;
    }

    private bool HasProcedureDependencies(FbConnection connection, string procedureName)
    {
        const string sql = @"SELECT 1 FROM RDB$DEPENDENCIES d WHERE TRIM(d.RDB$DEPENDED_ON_NAME) = @name ROWS 1";
        using var cmd = new FbCommand(sql, connection);
        cmd.Parameters.AddWithValue("@name", procedureName);
        return cmd.ExecuteScalar() != null;
    }

    private bool DomainHasDependencies(FbConnection connection, string domainName)
    {
        const string sql = @"
            SELECT 1
            FROM RDB$RELATION_FIELDS rf
            JOIN RDB$FIELDS f ON rf.RDB$FIELD_SOURCE = f.RDB$FIELD_NAME
            WHERE TRIM(f.RDB$FIELD_NAME) = @name
            ROWS 1";
        using var cmd = new FbCommand(sql, connection);
        cmd.Parameters.AddWithValue("@name", domainName);
        return cmd.ExecuteScalar() != null;
    }
    private bool IsDropStatement(string sqlText, string objectKind)
    {
        var pattern = objectKind switch
        {
            "TABLE" => @"(?is)^\s*DROP\s+TABLE\b",
            "PROCEDURE" => @"(?is)^\s*DROP\s+PROCEDURE\b",
            "DOMAIN" => @"(?is)^\s*DROP\s+DOMAIN\b",
            _ => throw new ArgumentOutOfRangeException(nameof(objectKind))
        };

        return Regex.IsMatch(sqlText, pattern);
    }
    private string BuildProcedureStubOrThrow(string originalSql)
    {
        var stub = TryBuildProcedureStubSql(originalSql);
        if (stub != null) return stub;

        throw new InvalidOperationException("Nie udało się zbudować stub-a procedury (nie znaleziono nagłówka / AS).");
    }

    private string? TryBuildProcedureStubSql(string sqlText)
    {
        var clean = StripLeadingEmptyAndCommentLines(sqlText);
        var match = Regex.Match(clean, @"(?is)^\s*CREATE\s+(OR\s+ALTER\s+)?(PROC|PROCEDURE)\s+.+?\bAS\b");
        if (!match.Success)
            return null;

        var headerIncludingAs = match.Value.TrimEnd();
        var stub = headerIncludingAs + Environment.NewLine + "BEGIN" + Environment.NewLine + "END";

        stub = EnsureCreateOrAlterForProcedure(stub);
        return stub;
    }

    private (bool Success, string? ErrorMessage) ExecuteSingleStatementInTransaction(
        FbConnection connection,
        string sqlText)
    {
        var sqlToExecute = Helpers.TrimTrailingSqlSemicolon(sqlText);
        if (string.IsNullOrWhiteSpace(sqlToExecute))
            return (true, null);

        if (_dryRun)
        {
            _dryRunPlanStatements.Add(sqlToExecute);
            return (true, null);
        }

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
            var message = fbEx.Message;
            if (message != null && message.Contains("object", StringComparison.OrdinalIgnoreCase) &&
                message.Contains("in use", StringComparison.OrdinalIgnoreCase))
            {
                message += " (obiekt w użyciu – spróbuj ponownie, gdy nie jest wykonywany)";
            }
            return (false, message);
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
        if (_dryRun)
        {
            foreach (var statement in statements)
            {
                var sql = Helpers.TrimTrailingSqlSemicolon(statement);
                if (!string.IsNullOrWhiteSpace(sql))
                    _dryRunPlanStatements.Add(sql);
            }

            return (true, null);
        }

        using var transaction = BeginWaitTransaction(connection);
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        try
        {
            foreach (var statement in statements)
            {
                var sql = Helpers.TrimTrailingSqlSemicolon(statement);
                if (string.IsNullOrWhiteSpace(sql))
                    continue;

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

    private ParsedTableDefinition ParseCreateTableColumns(string sqlText)
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

        var columns = new List<ColumnDefinition>();

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

            columns.Add(ParseColumnDefinition(columnToken, columnName, definitionSql));
        }

        return new ParsedTableDefinition(tableToken, tableName, columns);
    }

    private string NormalizeIdentifierForComparison(string identifierToken)
    {
        identifierToken = identifierToken.Trim();
        if (!identifierToken.StartsWith('\"') ||
            !identifierToken.EndsWith('\"')) return identifierToken.ToUpperInvariant();
        var unquoted = identifierToken.Substring(1, identifierToken.Length - 2).Replace("\"\"", "\"");
        return unquoted;

    }

    private Dictionary<string, ColumnDefinition> ReadExistingColumns(
        FbConnection connection,
        string tableName)
    {
        const string sql = @"
            SELECT
                TRIM(rf.RDB$FIELD_NAME) AS COL_NAME,
                TRIM(f.RDB$FIELD_NAME) AS FIELD_SOURCE,
                rf.RDB$DEFAULT_SOURCE AS COL_DEFAULT,
                rf.RDB$NULL_FLAG AS COL_NULL_FLAG,
                f.RDB$DEFAULT_SOURCE AS DOMAIN_DEFAULT,
                f.RDB$NULL_FLAG AS DOMAIN_NULL_FLAG,
                f.RDB$FIELD_TYPE AS FIELD_TYPE,
                f.RDB$FIELD_SUB_TYPE AS FIELD_SUB_TYPE,
                f.RDB$FIELD_LENGTH AS FIELD_LENGTH,
                f.RDB$FIELD_SCALE AS FIELD_SCALE,
                f.RDB$FIELD_PRECISION AS FIELD_PRECISION,
                COALESCE(f.RDB$CHARACTER_LENGTH, f.RDB$FIELD_LENGTH) AS CHAR_LEN,
                f.RDB$CHARACTER_SET_ID AS CHARACTER_SET_ID,
                f.RDB$SYSTEM_FLAG AS SYSTEM_FLAG,
                cs.RDB$CHARACTER_SET_NAME AS CHARSET_NAME
            FROM RDB$RELATION_FIELDS rf
            JOIN RDB$FIELDS f ON rf.RDB$FIELD_SOURCE = f.RDB$FIELD_NAME
            LEFT JOIN RDB$CHARACTER_SETS cs ON cs.RDB$CHARACTER_SET_ID = f.RDB$CHARACTER_SET_ID
            WHERE TRIM(rf.RDB$RELATION_NAME) = @tableName";

        using var cmd = new FbCommand(sql, connection);
        cmd.Parameters.AddWithValue("@tableName", tableName);

        var result = new Dictionary<string, ColumnDefinition>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var colName = Convert.ToString(reader["COL_NAME"])!.Trim();
            var fieldSource = Convert.ToString(reader["FIELD_SOURCE"])?.Trim();
            var colDefault = Convert.ToString(reader["COL_DEFAULT"]);
            var domainDefault = Convert.ToString(reader["DOMAIN_DEFAULT"]);
            var nullFlag = ReadNullableInt(reader, "COL_NULL_FLAG");
            var domainNullFlag = ReadNullableInt(reader, "DOMAIN_NULL_FLAG");
            var fieldType = ReadNullableInt(reader, "FIELD_TYPE");
            var fieldSubType = ReadNullableInt(reader, "FIELD_SUB_TYPE");
            var fieldLength = ReadNullableInt(reader, "FIELD_LENGTH");
            var fieldScale = ReadNullableInt(reader, "FIELD_SCALE");
            var fieldPrecision = ReadNullableInt(reader, "FIELD_PRECISION");
            var characterLength = ReadNullableInt(reader, "CHAR_LEN");
            var charsetName = Convert.ToString(reader["CHARSET_NAME"])?.Trim();
            var systemFlag = ReadNullableInt(reader, "SYSTEM_FLAG");

            var typeDefinition = BuildTypeDefinitionFromMetadata(
                fieldType,
                fieldSubType,
                fieldLength,
                fieldScale,
                fieldPrecision,
                characterLength,
                charsetName,
                fieldSource,
                systemFlag);

            var isNullable = (nullFlag ?? domainNullFlag) != 1;
            var defaultSql = NormalizeDefaultExpression(colDefault) ?? NormalizeDefaultExpression(domainDefault);

            var normalizedName = NormalizeIdentifierForComparison(colName);
            result[normalizedName] = new ColumnDefinition(
                BuildIdentifierToken(colName),
                normalizedName,
                string.Empty,
                typeDefinition,
                isNullable,
                defaultSql);
        }

        return result;
    }

    private DomainDefinition ReadExistingDomain(
        FbConnection connection,
        string domainName)
    {
        const string sql = @"
            SELECT
                f.RDB$FIELD_TYPE AS FIELD_TYPE,
                f.RDB$FIELD_SUB_TYPE AS FIELD_SUB_TYPE,
                f.RDB$FIELD_LENGTH AS FIELD_LENGTH,
                f.RDB$FIELD_SCALE AS FIELD_SCALE,
                f.RDB$FIELD_PRECISION AS FIELD_PRECISION,
                COALESCE(f.RDB$CHARACTER_LENGTH, f.RDB$FIELD_LENGTH) AS CHAR_LEN,
                cs.RDB$CHARACTER_SET_NAME AS CHARSET_NAME,
                f.RDB$DEFAULT_SOURCE AS DEFAULT_SOURCE,
                f.RDB$NULL_FLAG AS NULL_FLAG
            FROM RDB$FIELDS f
            LEFT JOIN RDB$CHARACTER_SETS cs ON cs.RDB$CHARACTER_SET_ID = f.RDB$CHARACTER_SET_ID
            WHERE TRIM(f.RDB$FIELD_NAME) = @name";

        using var cmd = new FbCommand(sql, connection);
        cmd.Parameters.AddWithValue("@name", domainName);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException($"Nie znaleziono domeny {domainName}.");

        var fieldType = ReadNullableInt(reader, "FIELD_TYPE");
        var fieldSubType = ReadNullableInt(reader, "FIELD_SUB_TYPE");
        var fieldLength = ReadNullableInt(reader, "FIELD_LENGTH");
        var fieldScale = ReadNullableInt(reader, "FIELD_SCALE");
        var fieldPrecision = ReadNullableInt(reader, "FIELD_PRECISION");
        var characterLength = ReadNullableInt(reader, "CHAR_LEN");
        var charsetName = Convert.ToString(reader["CHARSET_NAME"]);
        var defaultSource = Convert.ToString(reader["DEFAULT_SOURCE"]);
        var nullFlag = ReadNullableInt(reader, "NULL_FLAG");

        var typeDefinition = BuildBuiltinTypeDefinition(
            fieldType,
            fieldSubType,
            fieldLength,
            fieldScale,
            fieldPrecision,
            characterLength,
            charsetName);

        var isNullable = (nullFlag ?? 0) != 1;
        var defaultSql = NormalizeDefaultExpression(defaultSource);

        return new DomainDefinition(domainName, typeDefinition, isNullable, defaultSql);
    }

    private (IReadOnlyList<string> Statements, string ChangeDescription) BuildAlterStatementsForDomain(
        DomainDefinition desired,
        DomainDefinition existing)
    {
        var statements = new List<string>();
        var changes = new List<string>();
        var domainToken = BuildIdentifierToken(desired.DomainName);

        if (!ColumnTypeEquals(desired.Type, existing.Type))
        {
            statements.Add($"ALTER DOMAIN {domainToken} TYPE {desired.Type.TypeSql}");
            changes.Add("typ");
        }

        if (!DefaultEquals(desired.DefaultExpression, existing.DefaultExpression))
        {
            var sql = desired.DefaultExpression == null
                ? $"ALTER DOMAIN {domainToken} DROP DEFAULT"
                : $"ALTER DOMAIN {domainToken} SET DEFAULT {desired.DefaultExpression}";
            statements.Add(sql);
            changes.Add("default");
        }

        if (desired.IsNullable != existing.IsNullable)
        {
            var sql = desired.IsNullable
                ? $"ALTER DOMAIN {domainToken} DROP NOT NULL"
                : $"ALTER DOMAIN {domainToken} SET NOT NULL";
            statements.Add(sql);
            changes.Add("null");
        }

        var changeDescription = changes.Count == 0 ? string.Empty : string.Join("/", changes);
        return (statements, changeDescription);
    }

    private IReadOnlyCollection<string> ReadTargetNamesFromFiles(IReadOnlyList<string> files, string objectKind)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var content = File.ReadAllText(file);
            var name = TryExtractObjectName(content, objectKind) ?? TryExtractDropObjectName(content, objectKind);
            if (name != null) set.Add(name);
        }

        return set;
    }

    private IReadOnlyCollection<string> ReadTargetTablesFromFiles(IReadOnlyList<string> files)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var content = File.ReadAllText(file);
            if (IsDropStatement(content, "TABLE"))
            {
                var dropName = TryExtractDropObjectName(content, "TABLE");
                if (dropName != null) set.Add(dropName);
                continue;
            }

            try
            {
                var parsed = ParseCreateTableColumns(content);
                set.Add(parsed.TableName);
            }
            catch (Exception ex)
            {
                _failures.Add((file, $"Nie udało się sparsować tabeli: {ex.Message}"));
                break;
            }
        }

        return set;
    }

    private IReadOnlyCollection<string> ReadExistingNames(FbConnection connection, string objectKind)
    {
        string sql = objectKind switch
        {
            "DOMAIN" => @"SELECT TRIM(f.RDB$FIELD_NAME) AS NAME FROM RDB$FIELDS f WHERE (f.RDB$SYSTEM_FLAG IS NULL OR f.RDB$SYSTEM_FLAG = 0)",
            "TABLE" => @"SELECT TRIM(r.RDB$RELATION_NAME) AS NAME FROM RDB$RELATIONS r WHERE (r.RDB$SYSTEM_FLAG IS NULL OR r.RDB$SYSTEM_FLAG = 0) AND r.RDB$VIEW_BLR IS NULL",
            "PROCEDURE" => @"SELECT TRIM(p.RDB$PROCEDURE_NAME) AS NAME FROM RDB$PROCEDURES p WHERE (p.RDB$SYSTEM_FLAG IS NULL OR p.RDB$SYSTEM_FLAG = 0)",
            _ => throw new ArgumentOutOfRangeException(nameof(objectKind))
        };

        var list = new List<string>();
        using var cmd = new FbCommand(sql, connection);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = Convert.ToString(reader["NAME"]);
            if (!string.IsNullOrWhiteSpace(name)) list.Add(name.Trim());
        }

        return list;
    }

    private bool IsSystemObjectName(string name) =>
        name.StartsWith("RDB$", StringComparison.OrdinalIgnoreCase);

    private void DropMissingObjects(
        FbConnection connection,
        IReadOnlyCollection<string> targetDomains,
        IReadOnlyCollection<string> targetTables,
        IReadOnlyCollection<string> targetProcedures,
        bool destructiveEnabled)
    {
        var existingProcedures = ReadExistingNames(connection, "PROCEDURE");
        var existingTables = ReadExistingNames(connection, "TABLE");
        var existingDomains = ReadExistingNames(connection, "DOMAIN");

        var proceduresToDrop = existingProcedures.Where(p => !targetProcedures.Contains(p) && !IsSystemObjectName(p)).ToList();
        var tablesToDrop = existingTables.Where(t => !targetTables.Contains(t) && !IsSystemObjectName(t)).ToList();
        var domainsToDrop = existingDomains.Where(d => !targetDomains.Contains(d) && !IsSystemObjectName(d)).ToList();

        if (!destructiveEnabled)
        {
            _procedureDropCandidates.AddRange(proceduresToDrop);
            _tableDropCandidates.AddRange(tablesToDrop);
            _domainDropCandidates.AddRange(domainsToDrop);

            if (!_dryRun)
            {
                Console.WriteLine();
                Console.WriteLine("FB_DESTRUCTIVE != \"1\" - pomijam DROP brakujących obiektów (lista kandydatów w raporcie).");
                if (_procedureDropCandidates.Count == 0 && _tableDropCandidates.Count == 0 && _domainDropCandidates.Count == 0)
                    Console.WriteLine("Brak kandydatów do usunięcia.");
            }

            return;
        }

        DropMissingProcedures(connection, proceduresToDrop);
        DropMissingTables(connection, tablesToDrop);
        DropMissingDomains(connection, domainsToDrop);
    }

    private void DropMissingProcedures(FbConnection connection, List<string> proceduresToDrop)
    {
        if (proceduresToDrop.Count == 0) return;

        foreach (var proc in proceduresToDrop)
        {
            var stmt = $"DROP PROCEDURE {BuildIdentifierToken(proc)}";
            var result = ExecuteSingleStatementInTransaction(connection, stmt);
            if (result.Success)
            {
                _droppedProcedures.Add(proc);
                if (!_dryRun)
                    _executedCount++;
            }
            else
            {
                _failures.Add(("", $"{result.ErrorMessage ?? "Błąd drop procedury"}; SQL: {stmt}"));
            }
        }
    }

    private void DropMissingTables(FbConnection connection, List<string> tablesToDrop)
    {
        if (tablesToDrop.Count == 0) return;

        foreach (var table in tablesToDrop)
        {
            if (HasTableDependencies(connection, table))
            {
                var message = $"Pominięto DROP tabeli {table}: istnieją zależne obiekty.";
                if (_dryRun)
                    _dryRunDependencyBlocks.Add(message);
                else
                    _failures.Add(("", message));
                continue;
            }

            var stmt = $"DROP TABLE {BuildIdentifierToken(table)}";
            var result = ExecuteSingleStatementInTransaction(connection, stmt);
            if (result.Success)
            {
                _droppedTables.Add(table);
                if (!_dryRun)
                    _executedCount++;
            }
            else
            {
                _failures.Add(("", $"{result.ErrorMessage ?? "Błąd drop tabeli"}; SQL: {stmt}"));
            }
        }
    }

    private void DropMissingDomains(FbConnection connection, List<string> domainsToDrop)
    {
        if (domainsToDrop.Count == 0) return;

        foreach (var domain in domainsToDrop)
        {
            if (DomainHasDependencies(connection, domain))
            {
                var message = $"Pominięto DROP domeny {domain}: istnieją zależne obiekty.";
                if (_dryRun)
                    _dryRunDependencyBlocks.Add(message);
                else
                    _failures.Add(("", message));
                continue;
            }

            var stmt = $"DROP DOMAIN {BuildIdentifierToken(domain)}";
            var result = ExecuteSingleStatementInTransaction(connection, stmt);
            if (result.Success)
            {
                _droppedDomains.Add(domain);
                if (!_dryRun)
                    _executedCount++;
            }
            else
            {
                _failures.Add(("", $"{result.ErrorMessage ?? "Błąd drop domeny"}; SQL: {stmt}"));
            }
        }
    }

    private (IReadOnlyList<string> Statements, string ChangeDescription) BuildAlterStatementsForColumn(
        string tableToken,
        ColumnDefinition desired,
        ColumnDefinition existing)
    {
        var statements = new List<string>();
        var changes = new List<string>();

        if (!ColumnTypeEquals(desired.Type, existing.Type))
        {
            statements.Add($"ALTER TABLE {tableToken} ALTER COLUMN {desired.ColumnToken} TYPE {desired.Type.TypeSql}");
            changes.Add("typ");
        }

        if (desired.IsNullable != existing.IsNullable)
        {
            var sql = desired.IsNullable
                ? $"ALTER TABLE {tableToken} ALTER COLUMN {desired.ColumnToken} DROP NOT NULL"
                : $"ALTER TABLE {tableToken} ALTER COLUMN {desired.ColumnToken} SET NOT NULL";

            statements.Add(sql);
            changes.Add("null");
        }

        if (!DefaultEquals(desired.DefaultExpression, existing.DefaultExpression))
        {
            var sql = desired.DefaultExpression == null
                ? $"ALTER TABLE {tableToken} ALTER COLUMN {desired.ColumnToken} DROP DEFAULT"
                : $"ALTER TABLE {tableToken} ALTER COLUMN {desired.ColumnToken} SET DEFAULT {desired.DefaultExpression}";

            statements.Add(sql);
            changes.Add("default");
        }

        var changeDescription = changes.Count == 0 ? string.Empty : string.Join("/", changes);
        return (statements, changeDescription);
    }

    private bool DefaultEquals(string? left, string? right) =>
        string.Equals(NormalizeDefaultExpression(left), NormalizeDefaultExpression(right), StringComparison.OrdinalIgnoreCase);

    private ColumnDefinition ParseColumnDefinition(string columnToken, string columnName, string definitionSql)
    {
        var keywordIndex = FindFirstKeywordIndex(definitionSql);
        var typeSql = keywordIndex >= 0 ? definitionSql[..keywordIndex].Trim() : definitionSql.Trim();
        var remainder = keywordIndex >= 0 ? definitionSql[keywordIndex..].Trim() : string.Empty;

        var defaultExpression = ExtractDefaultExpression(remainder, out var remainderWithoutDefault);
        var isNullable = DetermineNullability(remainderWithoutDefault);

        var typeDefinition = ParseTypeSql(typeSql);
        return new ColumnDefinition(
            columnToken,
            columnName,
            definitionSql,
            typeDefinition,
            isNullable,
            NormalizeDefaultExpression(defaultExpression));
    }

    private int FindFirstKeywordIndex(string definitionSql)
    {
        var indexes = new[]
        {
            FindKeywordIndex(definitionSql, "DEFAULT"),
            FindKeywordIndex(definitionSql, "NOT NULL"),
            FindKeywordIndex(definitionSql, "NULL")
        }.Where(i => i >= 0).ToList();

        return indexes.Count == 0 ? -1 : indexes.Min();
    }

    private int FindKeywordIndex(string text, string keyword)
    {
        if (string.IsNullOrWhiteSpace(text))
            return -1;

        var inSingle = false;
        var inDouble = false;

        for (var i = 0; i <= text.Length - keyword.Length; i++)
        {
            var ch = text[i];
            if (ch == '\'' && !inDouble) inSingle = !inSingle;
            else if (ch == '"' && !inSingle) inDouble = !inDouble;

            if (inSingle || inDouble) continue;

            if (!text.AsSpan(i).StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
                continue;

            var beforeOk = i == 0 || char.IsWhiteSpace(text[i - 1]) || text[i - 1] == '(' || text[i - 1] == ',';
            var afterIndex = i + keyword.Length;
            var afterOk = afterIndex >= text.Length || char.IsWhiteSpace(text[afterIndex]) || text[afterIndex] == ')' || text[afterIndex] == ',';

            if (beforeOk && afterOk)
                return i;
        }

        return -1;
    }

    private string? ExtractDefaultExpression(string text, out string remainderWithoutDefault)
    {
        var match = Regex.Match(text, @"(?is)\bDEFAULT\b\s+(?<expr>.*?)(\bNOT\s+NULL\b|\bNULL\b|$)");
        if (!match.Success)
        {
            remainderWithoutDefault = text;
            return null;
        }

        remainderWithoutDefault = (text[..match.Index] + text[(match.Index + match.Length)..]).Trim();
        return match.Groups["expr"].Value.Trim();
    }

    private bool DetermineNullability(string text)
    {
        if (Regex.IsMatch(text, @"(?is)\bNOT\s+NULL\b"))
            return false;

        if (Regex.IsMatch(text, @"(?is)\bNULL\b"))
            return true;

        return true;
    }

    private ColumnTypeDefinition ParseTypeSql(string typeSql)
    {
        var trimmed = Regex.Replace(typeSql, @"\s+", " ").Trim();
        var upper = trimmed.ToUpperInvariant();

        var charMatch = Regex.Match(
            upper,
            @"^(?<type>(CHARACTER VARYING|VARCHAR|CHARACTER|CHAR|NATIONAL CHARACTER VARYING|NATIONAL CHARACTER|NCHAR VARYING|NCHAR))\s*(\((?<len>\d+)\))?(?:\s+CHARACTER SET\s+(?<charset>""[^""]+""|\w+))?$");
        if (charMatch.Success)
        {
            var baseType = charMatch.Groups["type"].Value;
            var normalizedType = baseType.Contains("VARYING", StringComparison.OrdinalIgnoreCase) || baseType.Contains("VARCHAR", StringComparison.OrdinalIgnoreCase)
                ? "VARCHAR"
                : "CHAR";
            var length = ParseNullableInt(charMatch.Groups["len"].Value);
            var charset = NormalizeIdentifier(charMatch.Groups["charset"].Value);

            return new ColumnTypeDefinition(
                trimmed,
                false,
                null,
                normalizedType,
                length,
                null,
                null,
                null,
                charset);
        }

        var decMatch = Regex.Match(upper, @"^(?<type>DECIMAL|NUMERIC)\s*\((?<prec>\d+)(\s*,\s*(?<scale>-?\d+))?\)$");
        if (decMatch.Success)
        {
            var typeName = decMatch.Groups["type"].Value;
            var precision = ParseNullableInt(decMatch.Groups["prec"].Value);
            var scale = Math.Abs(ParseNullableInt(decMatch.Groups["scale"].Value) ?? 0);
            return new ColumnTypeDefinition(trimmed, false, null, typeName, null, precision, scale, null, null);
        }

        var cstringMatch = Regex.Match(upper, @"^CSTRING\s*\((?<len>\d+)\)$");
        if (cstringMatch.Success)
        {
            var length = ParseNullableInt(cstringMatch.Groups["len"].Value);
            return new ColumnTypeDefinition(trimmed, false, null, "CSTRING", length, null, null, null, null);
        }

        if (upper is "SMALLINT" or "INTEGER" or "BIGINT")
            return new ColumnTypeDefinition(trimmed, false, null, upper, null, null, null, null, null);

        if (upper is "FLOAT" or "DOUBLE PRECISION")
            return new ColumnTypeDefinition(trimmed, false, null, upper, null, null, null, null, null);

        if (upper is "DATE" or "TIME" or "TIMESTAMP")
            return new ColumnTypeDefinition(trimmed, false, null, upper, null, null, null, null, null);

        if (upper is "BOOLEAN")
            return new ColumnTypeDefinition(trimmed, false, null, upper, null, null, null, null, null);

        var blobMatch = Regex.Match(
            upper,
            @"^BLOB(?:\s+SUB_TYPE\s+(?<subtype>TEXT|\d+))?(?:\s+CHARACTER SET\s+(?<charset>""[^""]+""|\w+))?");
        if (blobMatch.Success)
        {
            var subTypeGroup = blobMatch.Groups["subtype"].Value;
            var subType = string.Equals(subTypeGroup, "TEXT", StringComparison.OrdinalIgnoreCase)
                ? 1
                : ParseNullableInt(subTypeGroup);
            var charset = NormalizeIdentifier(blobMatch.Groups["charset"].Value);
            return new ColumnTypeDefinition(trimmed, false, null, "BLOB", null, null, null, subType, charset);
        }

        var domainName = typeSql.Trim();
        return new ColumnTypeDefinition(
            domainName,
            true,
            NormalizeIdentifierForComparison(domainName),
            domainName,
            null,
            null,
            null,
            null,
            null);
    }

    private ColumnTypeDefinition BuildTypeDefinitionFromMetadata(
        int? fieldType,
        int? fieldSubType,
        int? fieldLength,
        int? fieldScale,
        int? fieldPrecision,
        int? characterLength,
        string? charsetName,
        string? fieldSource,
        int? systemFlag)
    {
        var builtInType = BuildBuiltinTypeDefinition(
            fieldType,
            fieldSubType,
            fieldLength,
            fieldScale,
            fieldPrecision,
            characterLength,
            charsetName);

        var domainName = fieldSource?.Trim();
        var isUserDomain = !string.IsNullOrWhiteSpace(domainName) &&
                           (systemFlag ?? 0) == 0 &&
                           !domainName!.StartsWith("RDB$", StringComparison.OrdinalIgnoreCase);

        if (isUserDomain)
        {
            var normalizedDomain = NormalizeIdentifierForComparison(domainName!);
            return new ColumnTypeDefinition(
                BuildIdentifierToken(domainName!),
                true,
                normalizedDomain,
                builtInType.NormalizedTypeName,
                builtInType.Length,
                builtInType.Precision,
                builtInType.Scale,
                builtInType.SubType,
                builtInType.CharacterSet);
        }

        return builtInType;
    }

    private ColumnTypeDefinition BuildBuiltinTypeDefinition(
        int? fieldType,
        int? fieldSubType,
        int? fieldLength,
        int? fieldScale,
        int? fieldPrecision,
        int? characterLength,
        string? charsetName)
    {
        var typeName = string.Empty;
        int? length = null;
        int? precision = null;
        int? scale = null;
        int? subType = null;

        switch (fieldType)
        {
            case 7:
                typeName = "SMALLINT";
                precision = 4;
                break;
            case 8:
                typeName = "INTEGER";
                precision = 9;
                break;
            case 16:
                typeName = "BIGINT";
                precision = 18;
                break;
            case 10:
                typeName = "FLOAT";
                break;
            case 27:
                typeName = "DOUBLE PRECISION";
                break;
            case 12:
                typeName = "DATE";
                break;
            case 13:
                typeName = "TIME";
                break;
            case 35:
                typeName = "TIMESTAMP";
                break;
            case 37:
                typeName = "VARCHAR";
                length = characterLength ?? fieldLength;
                break;
            case 40:
                typeName = "CSTRING";
                length = characterLength ?? fieldLength;
                break;
            case 14:
                typeName = "CHAR";
                length = characterLength ?? fieldLength;
                break;
            case 23:
                typeName = "BOOLEAN";
                break;
            case 261:
                typeName = "BLOB";
                subType = fieldSubType;
                break;
        }

        if ((fieldScale ?? 0) < 0 && (fieldType == 7 || fieldType == 8 || fieldType == 16))
        {
            precision = fieldPrecision ?? precision;
            scale = Math.Abs(fieldScale ?? 0);
            typeName = fieldSubType switch
            {
                2 => "DECIMAL",
                _ => "NUMERIC"
            };
        }

        var typeSql = BuildTypeSql(typeName, length, precision, scale, subType, charsetName);
        return new ColumnTypeDefinition(
            typeSql,
            false,
            null,
            typeName,
            length,
            precision,
            scale,
            subType,
            charsetName);
    }

    private string BuildTypeSql(
        string typeName,
        int? length,
        int? precision,
        int? scale,
        int? subType,
        string? charsetName)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(typeName);

        if (typeName is "CHAR" or "VARCHAR" or "CSTRING" or "NCHAR")
        {
            if (length.HasValue)
                builder.Append('(').Append(length.Value).Append(')');
        }
        else if (typeName is "DECIMAL" or "NUMERIC")
        {
            if (precision.HasValue)
            {
                builder.Append('(').Append(precision.Value);
                if (scale.HasValue && scale.Value >= 0)
                    builder.Append(',').Append(scale.Value);
                builder.Append(')');
            }
        }
        else if (typeName == "BLOB" && subType.HasValue)
        {
            builder.Append(" SUB_TYPE ");
            builder.Append(subType.Value == 1 ? "TEXT" : subType.Value);
        }

        if (!string.IsNullOrWhiteSpace(charsetName))
            builder.Append(" CHARACTER SET ").Append(charsetName!.Trim());

        return builder.ToString();
    }

    private bool ColumnTypeEquals(ColumnTypeDefinition left, ColumnTypeDefinition right)
    {
        if (left.IsDomain || right.IsDomain)
        {
            return left.IsDomain &&
                   right.IsDomain &&
                   string.Equals(left.DomainName, right.DomainName, StringComparison.Ordinal);
        }

        return string.Equals(left.NormalizedTypeName, right.NormalizedTypeName, StringComparison.OrdinalIgnoreCase)
               && left.Length == right.Length
               && left.Precision == right.Precision
               && left.Scale == right.Scale
               && left.SubType == right.SubType
               && string.Equals(left.CharacterSet ?? string.Empty, right.CharacterSet ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private string? NormalizeDefaultExpression(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return null;

        var trimmed = expression.Trim();
        if (trimmed.StartsWith("DEFAULT", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed["DEFAULT".Length..].TrimStart();

        while (trimmed.StartsWith('(') && trimmed.EndsWith(')'))
        {
            trimmed = trimmed[1..^1].Trim();
        }

        return trimmed;
    }

    private string NormalizeIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            return identifier;

        identifier = identifier.Trim();
        if (identifier.StartsWith('"') && identifier.EndsWith('"') && identifier.Length >= 2)
        {
            return identifier.Substring(1, identifier.Length - 2).Replace("\"\"", "\"");
        }

        return identifier;
    }

    private int? ParseNullableInt(string text) =>
        int.TryParse(text, out var value) ? value : null;

    private int? ReadNullableInt(FbDataReader reader, string columnName)
    {
        var value = reader[columnName];
        return value == DBNull.Value ? null : Convert.ToInt32(value);
    }

    private string BuildIdentifierToken(string identifier)
    {
        identifier = identifier.Trim();
        if (Regex.IsMatch(identifier, "^[A-Z][A-Z0-9_\\$]*$", RegexOptions.CultureInvariant))
            return identifier;

        var escaped = identifier.Replace("\"", "\"\"");
        return $"\"{escaped}\"";
    }

    private record ColumnDefinition(
        string ColumnToken,
        string ColumnName,
        string DefinitionSql,
        ColumnTypeDefinition Type,
        bool IsNullable,
        string? DefaultExpression);

    private record ColumnTypeDefinition(
        string TypeSql,
        bool IsDomain,
        string? DomainName,
        string NormalizedTypeName,
        int? Length,
        int? Precision,
        int? Scale,
        int? SubType,
        string? CharacterSet);

    private record ParsedTableDefinition(
        string TableToken,
        string TableName,
        List<ColumnDefinition> Columns);

    private record DomainDefinition(
        string DomainName,
        ColumnTypeDefinition Type,
        bool IsNullable,
        string? DefaultExpression);

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
