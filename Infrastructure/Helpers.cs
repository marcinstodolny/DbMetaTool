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

    public static string NormalizeSqlForAdo(string sqlText)
    {
        var trimmed = sqlText.Trim();

        if (trimmed.EndsWith(';'))
            trimmed = trimmed[..^1].TrimEnd();

        return trimmed;
    }

    public static string BuildConnectionString(string databaseFilePath)
    {

        var user = Environment.GetEnvironmentVariable("FB_USER") ?? "SYSDBA";
        var password = Environment.GetEnvironmentVariable("FB_PASSWORD") ?? "masterkey";
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