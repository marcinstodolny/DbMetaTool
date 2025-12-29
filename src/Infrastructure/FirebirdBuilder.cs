using FirebirdSql.Data.FirebirdClient;
using System.Text.RegularExpressions;

namespace DbMetaTool.Infrastructure;

public static class FirebirdBuilder
{
    public static void CreateDatabase(string connectionString, string databaseDirectory, string databaseFilePath)
    {
        Directory.CreateDirectory(databaseDirectory);

        if (File.Exists(databaseFilePath))
        {
            File.Delete(databaseFilePath);
        }

        try
        {
            InvokeFbCreateDatabase(connectionString, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Nie udało się utworzyć bazy danych.");
            Console.WriteLine("Ścieżka: " + databaseFilePath);
            Console.WriteLine("Błąd: " + ex.Message);
            throw;
        }
    }

    public static (int executedOk, List<(string File, string Error)> failures) ApplyScripts(string connectionString, string scriptsDirectory)
    {
        var failures = new List<(string File, string Error)>();
        var executedOk = 0;

        using var connection = Helpers.CreateAndOpenConnection(connectionString);

        var domainFiles = Helpers.GetSqlFiles(Path.Combine(scriptsDirectory, ScriptGroup.Domain.GetFolderName()));
        var tableFiles = Helpers.GetSqlFiles(Path.Combine(scriptsDirectory, ScriptGroup.Table.GetFolderName()));
        var procedureFiles = Helpers.GetSqlFiles(Path.Combine(scriptsDirectory, ScriptGroup.Procedure.GetFolderName()));

        if (!RunPhase1(connection, domainFiles, tableFiles, procedureFiles, ref executedOk, failures))
        {
            return (executedOk, failures);
        }
        
        RunPhase2(connection, procedureFiles, ref executedOk, failures);
        return (executedOk, failures);
    }

    private static bool RunPhase1(FbConnection connection, IReadOnlyList<string> domainFiles,
        IReadOnlyList<string> tableFiles, IReadOnlyList<string> procedureFiles, ref int executedOk,
        List<(string File, string Error)> failures)
    {
        using var transaction = BeginWaitTransaction(connection);

        if (!ExecuteFilesInTransaction(connection, transaction, ScriptGroup.Domain.GetFolderName(), domainFiles, sql => sql, countAsExecutedFile: true, ref executedOk, failures)
            || !ExecuteFilesInTransaction(connection, transaction, ScriptGroup.Table.GetFolderName(), tableFiles, sql => sql, countAsExecutedFile: true, ref executedOk, failures)
            || !ExecuteFilesInTransaction(connection, transaction, $"{ScriptGroup.Procedure.GetFolderName()} (stubs)", procedureFiles, BuildProcedureStubOrThrow, countAsExecutedFile: false, ref executedOk, failures))
        {
            return Rollback(transaction);
        }

        transaction.Commit();
        return true;
    }

    private static bool RunPhase2(FbConnection connection, IReadOnlyList<string> procedureFiles, ref int executedOk, List<(string File, string Error)> failures)
    {
        using var transaction = BeginWaitTransaction(connection);

        if (!ExecuteFilesInTransaction(connection, transaction, ScriptGroup.Procedure.GetFolderName(), procedureFiles, EnsureCreateOrAlterForProcedure, countAsExecutedFile: true, ref executedOk, failures))
        {
            return Rollback(transaction);
        }

        transaction.Commit();
        return true;
    }

    public static void Report(string databaseFilePath, int executedOk, List<(string File, string Error)> failures)
    {
        Console.WriteLine();
        Console.WriteLine("RAPORT BUILD-DB");
        Console.WriteLine($"DB: {databaseFilePath}");
        Console.WriteLine($"Wykonane: {executedOk}");
        Console.WriteLine($"Błędy: {failures.Count}");

        if (failures.Count <= 0) return;

        Console.WriteLine();
        Console.WriteLine("Szczegóły błędów:");
        foreach (var failure in failures)
        {
            Console.WriteLine("- Plik: " + failure.File);
            Console.WriteLine("  Błąd: " + failure.Error);
        }
        throw new Exception("Build-db przerwany: wystąpiły błędy w skryptach.");
    }


    private static void InvokeFbCreateDatabase(string connectionString, bool overwrite)
    {
        var fbConnectionType = typeof(FbConnection);

        var createDatabaseMethods = fbConnectionType
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(methodInfo => string.Equals(methodInfo.Name, "CreateDatabase", StringComparison.Ordinal))
            .ToArray();

        var fourParametersMethodInfo = createDatabaseMethods.FirstOrDefault(m =>
        {
            var parameters = m.GetParameters();
            return parameters.Length == 4
                && parameters[0].ParameterType == typeof(string)
                && parameters[1].ParameterType == typeof(int)
                && parameters[2].ParameterType == typeof(bool)
                && parameters[3].ParameterType == typeof(bool);
        });

        if (fourParametersMethodInfo != null)
        {
            const int pageSize = 8192;
            const bool forcedWrites = true;

            fourParametersMethodInfo.Invoke(null, new Object[] { connectionString, pageSize, forcedWrites, overwrite });
            return;
        }

        var twoParametersMethodInfo = createDatabaseMethods.FirstOrDefault(m =>
        {
            var parameters = m.GetParameters();
            return parameters.Length == 2
                && parameters[0].ParameterType == typeof(string)
                && parameters[1].ParameterType == typeof(bool);
        });

        if (twoParametersMethodInfo != null)
        {
            twoParametersMethodInfo.Invoke(null, new Object[] { connectionString, overwrite });
            return;
        }

        var oneParameterMethodInfo = createDatabaseMethods.FirstOrDefault(m =>
        {
            var parameters = m.GetParameters();
            return parameters.Length == 1 && parameters[0].ParameterType == typeof(string);
        });

        if (oneParameterMethodInfo != null)
        {
            oneParameterMethodInfo.Invoke(null, new Object[] { connectionString });
            return;
        }

        var available = string.Join(
            Environment.NewLine,
            createDatabaseMethods.Select(m =>
            {
                var ps = m.GetParameters();
                return "CreateDatabase(" + string.Join(", ", ps.Select(p => p.ParameterType.Name)) + ")";
            })
        );

        throw new InvalidOperationException("Nie znaleziono pasującego overloadu CreateDatabase. Dostępne:\n" + available);
    }

    private static bool ExecuteFilesInTransaction(FbConnection connection, FbTransaction transaction, string groupLabel,
        IReadOnlyList<string> files, Func<string, string> sqlTransformer, bool countAsExecutedFile, ref int executedOk,
        List<(string File, string Error)> failures)
    {
        Console.WriteLine($"Wykonywanie: {groupLabel} ({files.Count} plików)");

        foreach (var filePath in files)
        {
            var originalSql = Helpers.ReadNormalizedSql(filePath);

            var sqlToRun = sqlTransformer(originalSql);

            if (string.IsNullOrWhiteSpace(sqlToRun))
                continue;

            var executeResult = ExecuteSqlInExistingTransaction(connection, transaction, sqlToRun);
            if (executeResult.Success)
            {
                if (countAsExecutedFile) executedOk++;
                continue;
            }

            failures.Add((filePath, executeResult.ErrorMessage ?? "Nieznany błąd"));
            return false; 
        }

        return true;
    }

    private static FbTransaction BeginWaitTransaction(FbConnection connection)
    {
        var options = new FbTransactionOptions
        {
            TransactionBehavior = FbTransactionBehavior.Concurrency | FbTransactionBehavior.Write | FbTransactionBehavior.Wait
        };

        return connection.BeginTransaction(options);
    }

    private static bool Rollback(FbTransaction transaction)
    {
        try { transaction.Rollback(); } catch { /* ignored */ }
        return false;
    }

    private static (bool Success, string? ErrorMessage) ExecuteSqlInExistingTransaction(FbConnection connection, FbTransaction transaction, string sql)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.CommandType = System.Data.CommandType.Text;
            command.ExecuteNonQuery();
            return (true, null);
        }
        catch (FbException fbEx) { return (false, fbEx.Message); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private static string EnsureCreateOrAlterForProcedure(string sqlText)
    {
        return Regex.Replace(
            sqlText,
            @"(?is)^\s*CREATE\s+(?:PROC|PROCEDURE)\b",
            "CREATE OR ALTER PROCEDURE");
    }

    private static string BuildProcedureStubOrThrow(string originalSql)
    {
        var stub = TryBuildProcedureStubSql(originalSql);
        return stub ?? throw new InvalidOperationException("Nie udało się zbudować stub-a procedury (nie znaleziono nagłówka / AS).");
    }

    private static string? TryBuildProcedureStubSql(string sqlText)
    {
        var match = Regex.Match(sqlText, @"(?is)^\s*CREATE\s+(OR\s+ALTER\s+)?(PROC|PROCEDURE)\s+.+?\bAS\b");
        if (!match.Success)
            return null;

        var headerIncludingAs = match.Value.TrimEnd();
        var stub = headerIncludingAs + Environment.NewLine + "BEGIN" + Environment.NewLine + "END";

        stub = EnsureCreateOrAlterForProcedure(stub);
        return stub;
    }
}
