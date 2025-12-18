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
}