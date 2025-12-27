using FirebirdSql.Data.FirebirdClient;
using System.Text.RegularExpressions;

namespace DbMetaTool.Infrastructure
{
    public static class FirebirdBuilder
    {
        private static readonly Regex CreateProcedureHeaderRegex =
            new(@"(?is)^\s*CREATE\s+(OR\s+ALTER\s+)?(PROC|PROCEDURE)\s+.+?\bAS\b",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex CreateProcedureStartRegex =
            new(@"(?is)^\s*CREATE\s+(PROC|PROCEDURE)\b",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

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

        public static (int executedOk, List<(string File, string Error)> failures) ApplyScripts(
            string connectionString,
            string scriptsDirectory)
        {
            var failures = new List<(string File, string Error)>();
            var executedOk = 0;

            using var connection = Helpers.CreateAndOpenConnection(connectionString);

            var domainFiles = Helpers.GetSqlFiles(Path.Combine(scriptsDirectory, ScriptGroup.Domain.GetFolderName()));
            var tableFiles = Helpers.GetSqlFiles(Path.Combine(scriptsDirectory, ScriptGroup.Table.GetFolderName()));
            var procedureFiles = Helpers.GetSqlFiles(Path.Combine(scriptsDirectory, ScriptGroup.Procedure.GetFolderName()));

            if (!ExecuteGroupInNewTransaction(connection, ScriptGroup.Domain.GetFolderName(), domainFiles, sql => sql,
                    countAsExecutedFile: true, ref executedOk, failures)) 
            {
                return (executedOk, failures);
            }

            if (!ExecuteGroupInNewTransaction(connection, ScriptGroup.Table.GetFolderName(), tableFiles, sql => sql,
                    countAsExecutedFile: true, ref executedOk, failures))
            {
                return (executedOk, failures);
            }

            if (!ExecuteGroupInNewTransaction(connection, $"{ScriptGroup.Procedure.GetFolderName()} (stubs)",
                    procedureFiles, BuildProcedureStubOrThrow, countAsExecutedFile: false, ref executedOk, failures))
            {
                return (executedOk, failures);
            }

            ExecuteGroupInNewTransaction(connection, ScriptGroup.Procedure.GetFolderName(), procedureFiles,
                EnsureCreateOrAlterForProcedure, countAsExecutedFile: true, ref executedOk, failures);

            return (executedOk, failures);
        }

        private static bool ExecuteGroupInNewTransaction(FbConnection connection, string groupLabel,
            IReadOnlyList<string> files, Func<string, string> sqlTransformer, bool countAsExecutedFile,
            ref int executedOk, List<(string File, string Error)> failures)
        {
            using var transaction = BeginWaitTransaction(connection);

            var ok = ExecuteFilesInTransaction(connection, transaction, groupLabel, files, sqlTransformer, countAsExecutedFile, ref executedOk, failures);

            if (!ok)
                return Rollback(transaction);

            transaction.Commit();
            return true;
        }

        private static bool ExecuteFilesInTransaction(FbConnection connection, FbTransaction transaction,
            string groupLabel, IReadOnlyList<string> files, Func<string, string> sqlTransformer,
            bool countAsExecutedFile, ref int executedOk, List<(string File, string Error)> failures)
        {
            Console.WriteLine($"Wykonywanie: {groupLabel} ({files.Count} plików)");

            using var command = connection.CreateCommand();
            command.Transaction = transaction;

            foreach (var filePath in files)
            {
                var originalSql = File.ReadAllText(filePath);

                string sqlToRun;
                try
                {
                    sqlToRun = sqlTransformer(originalSql);
                }
                catch (Exception ex)
                {
                    failures.Add((filePath, ex.Message));
                    return false;
                }

                sqlToRun = Helpers.TrimTrailingSqlSemicolon(sqlToRun);

                if (string.IsNullOrWhiteSpace(sqlToRun))
                    continue;

                try
                {
                    command.CommandText = sqlToRun;
                    command.ExecuteNonQuery();

                    if (countAsExecutedFile)
                        executedOk++;
                }
                catch (FbException fbEx)
                {
                    failures.Add((filePath, fbEx.Message));
                    return false;
                }
                catch (Exception ex)
                {
                    failures.Add((filePath, ex.Message));
                    return false;
                }
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
            try { transaction.Rollback(); } catch { }
            return false;
        }

        private static string EnsureCreateOrAlterForProcedure(string sqlText)
        {
            return CreateProcedureStartRegex.Replace(sqlText, "CREATE OR ALTER PROCEDURE", count: 1);
        }

        private static string BuildProcedureStubOrThrow(string originalSql)
        {
            var stub = TryBuildProcedureStubSql(originalSql);
            return stub ?? throw new InvalidOperationException(
                "Nie udało się zbudować stub-a procedury (nie znaleziono nagłówka / AS).");
        }

        private static string? TryBuildProcedureStubSql(string sqlText)
        {
            var match = CreateProcedureHeaderRegex.Match(sqlText);
            if (!match.Success)
                return null;

            var headerIncludingAs = match.Value.TrimEnd();
            var stub = headerIncludingAs + Environment.NewLine + "BEGIN" + Environment.NewLine + "END";

            stub = EnsureCreateOrAlterForProcedure(stub);
            return stub;
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
    }
}