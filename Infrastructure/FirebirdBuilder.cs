namespace DbMetaTool.Infrastructure;

public static class FirebirdBuilder
{
    public static string BuildConnectionString(string databaseDirectory, string databaseFilePath)
    {

        var user = Environment.GetEnvironmentVariable("FB_USER") ?? "SYSDBA";
        var password = Environment.GetEnvironmentVariable("FB_PASSWORD") ?? "masterkey";
        var host = Environment.GetEnvironmentVariable("FB_HOST") ?? "localhost";

        return $"User={user};Password={password};Database={databaseFilePath};DataSource={host};Dialect=3;Charset=UTF8;";
    }

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
        var domainsDir = Path.Combine(scriptsDirectory, "domains");
        var tablesDir = Path.Combine(scriptsDirectory, "tables");
        var proceduresDir = Path.Combine(scriptsDirectory, "procedures");

        var domainFiles = GetSqlFiles(domainsDir);
        var tableFiles = GetSqlFiles(tablesDir);
        var procedureFiles = GetSqlFiles(proceduresDir);

        var failures = new List<(string File, string Error)>();
        var executedOk = 0;

        using (var connection = new FirebirdSql.Data.FirebirdClient.FbConnection(connectionString))
        {
            connection.Open();

            void ExecuteGroup(string groupName, IReadOnlyList<string> files)
            {
                Console.WriteLine($"Wykonywanie: {groupName} ({files.Count} plików)");

                foreach (var filePath in files)
                {
                    var result = ExecuteSingleStatement(connection, filePath);
                    if (result.Success)
                    {
                        executedOk++;
                        continue;
                    }

                    failures.Add((filePath, result.ErrorMessage ?? "Nieznany błąd"));
                    break;
                }
            }

            ExecuteGroup("domains", domainFiles);
            if (failures.Count == 0) ExecuteGroup("tables", tableFiles);
            if (failures.Count == 0) ExecuteGroup("procedures", procedureFiles);
        }

        return (executedOk, failures);
    }

    private static void InvokeFbCreateDatabase(string connectionString, bool overwrite)
    {
        var fbConnectionType = typeof(FirebirdSql.Data.FirebirdClient.FbConnection);

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

            fourParametersMethodInfo.Invoke(null, [connectionString, pageSize, forcedWrites, overwrite]);
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
            twoParametersMethodInfo.Invoke(null, [connectionString, overwrite]);
            return;
        }

        var oneParameterMethodInfo = createDatabaseMethods.FirstOrDefault(m =>
        {
            var parameters = m.GetParameters();
            return parameters.Length == 1 && parameters[0].ParameterType == typeof(string);
        });

        if (oneParameterMethodInfo != null)
        {
            oneParameterMethodInfo.Invoke(null, [connectionString]);
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

    private static string NormalizeSqlForAdo(string sqlText)
    {
        var trimmed = sqlText.Trim();

        if (trimmed.EndsWith(';'))
            trimmed = trimmed[..^1].TrimEnd();

        return trimmed;
    }

    private static List<string> GetSqlFiles(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            return [];

        return Directory.GetFiles(directoryPath, "*.sql", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static (bool Success, string? ErrorMessage) ExecuteSingleStatement(
        FirebirdSql.Data.FirebirdClient.FbConnection connection,
        string filePath)
    {
        var sql = File.ReadAllText(filePath);
        sql = NormalizeSqlForAdo(sql);

        if (string.IsNullOrWhiteSpace(sql))
            return (true, null);

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandType = System.Data.CommandType.Text;
            command.ExecuteNonQuery();
            return (true, null);
        }
        catch (FirebirdSql.Data.FirebirdClient.FbException fbEx)
        {
            return (false, fbEx.Message);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}