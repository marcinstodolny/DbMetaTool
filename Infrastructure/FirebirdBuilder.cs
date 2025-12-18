using FirebirdSql.Data.FirebirdClient;

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

        using var connection = new FbConnection(connectionString);
        connection.Open();

        using var transaction = connection.BeginTransaction();

        foreach (var scriptGroup in ScriptGroupInfo.ExecutionOrder)
        {
            var groupDirectory = Path.Combine(scriptsDirectory, scriptGroup.GetFolderName());
            var groupFiles = Helpers.GetSqlFiles(groupDirectory);

            Console.WriteLine($"Wykonywanie: {scriptGroup.GetFolderName()} ({groupFiles.Count} plików)");

            foreach (var filePath in groupFiles)
            {
                var result = ExecuteSingleStatement(connection, transaction, filePath);
                if (result.Success)
                {
                    executedOk++;
                    continue;
                }

                failures.Add((filePath, result.ErrorMessage ?? "Nieznany błąd"));
                break;
            }

            if (failures.Count > 0)
                break;
        }

        if (failures.Count == 0) transaction.Commit();
        else transaction.Rollback();

        return (executedOk, failures);
    }

    public static void Report(string databaseFilePath, int executedOk, List<(string File, string Error)> failures)
    {
        Console.WriteLine();
        Console.WriteLine("RAPORT BUILD-DB");
        Console.WriteLine($"DB: {databaseFilePath}");
        Console.WriteLine($"OK: {executedOk}");
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

            fourParametersMethodInfo.Invoke(null, new Object[] { connectionString, pageSize, forcedWrites, overwrite});
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
            twoParametersMethodInfo.Invoke(null, new Object[] { connectionString, overwrite});
            return;
        }

        var oneParameterMethodInfo = createDatabaseMethods.FirstOrDefault(m =>
        {
            var parameters = m.GetParameters();
            return parameters.Length == 1 && parameters[0].ParameterType == typeof(string);
        });

        if (oneParameterMethodInfo != null)
        {
            oneParameterMethodInfo.Invoke(null, new Object[] {connectionString});
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

    private static (bool Success, string? ErrorMessage) ExecuteSingleStatement(
        FbConnection connection,
        FbTransaction transaction,
        string filePath)
    {
        var sql = File.ReadAllText(filePath);
        sql = Helpers.NormalizeSqlForAdo(sql);

        if (string.IsNullOrWhiteSpace(sql))
            return (true, null);

        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.ExecuteNonQuery();
            return (true, null);
        }
        catch (FbException fbEx) { return (false, fbEx.Message); }
        catch (Exception ex) { return (false, ex.Message); }
    }
}