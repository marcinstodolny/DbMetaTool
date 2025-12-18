using FirebirdSql.Data.FirebirdClient;

namespace DbMetaTool.Infrastructure;

public class FirebirdExporter
{
    private int _domainsExported; 
    private int _tablesExported;
    private int _proceduresExported;
    public void ExportDomains(FbConnection connection, string outputDirectory)
    {
        Helpers.EnsureOpen(connection);
        var domainsDirectory = Path.Combine(outputDirectory, ScriptGroup.Domain.GetFolderName());
        Directory.CreateDirectory(domainsDirectory);
        
        const string domainQuery = @"
            SELECT
                f.RDB$FIELD_NAME            AS DOMAIN_NAME,
                f.RDB$FIELD_TYPE            AS FIELD_TYPE,
                f.RDB$FIELD_SUB_TYPE        AS FIELD_SUB_TYPE,
                f.RDB$FIELD_LENGTH          AS FIELD_LENGTH,
                f.RDB$FIELD_PRECISION       AS FIELD_PRECISION,
                f.RDB$FIELD_SCALE           AS FIELD_SCALE,
                f.RDB$CHARACTER_LENGTH      AS CHAR_LEN,
                f.RDB$NULL_FLAG             AS NULL_FLAG,
                f.RDB$DEFAULT_SOURCE        AS DEFAULT_SOURCE,
                f.RDB$VALIDATION_SOURCE     AS VALIDATION_SOURCE
            FROM RDB$FIELDS f
            WHERE COALESCE(f.RDB$SYSTEM_FLAG, 0) = 0
              AND f.RDB$FIELD_NAME NOT STARTING WITH 'RDB$'
            ORDER BY f.RDB$FIELD_NAME";

        using var domainCommand = new FbCommand(domainQuery, connection);
        using var reader = domainCommand.ExecuteReader();
        while (reader.Read())
        {
            var domainName = TrimFbString(reader["DOMAIN_NAME"]);
            var fieldType = Convert.ToInt16(reader["FIELD_TYPE"]);
            var fieldSubType = ReadNullableInt16(reader["FIELD_SUB_TYPE"]);
            var fieldLength = ReadNullableInt(reader["FIELD_LENGTH"]);
            var fieldPrecision = ReadNullableInt16(reader["FIELD_PRECISION"]);
            var fieldScale = ReadNullableInt16(reader["FIELD_SCALE"]);
            var characterLength = ReadNullableInt16(reader["CHAR_LEN"]);

            var isNotNull = reader["NULL_FLAG"] != DBNull.Value && Convert.ToInt16(reader["NULL_FLAG"]) == 1;
            var defaultSource = reader["DEFAULT_SOURCE"] == DBNull.Value ? string.Empty : Convert.ToString(reader["DEFAULT_SOURCE"])!.Trim();
            var validationSource = reader["VALIDATION_SOURCE"] == DBNull.Value ? string.Empty : Convert.ToString(reader["VALIDATION_SOURCE"])!.Trim();

            var typeSql = BuildTypeSql(fieldType, fieldSubType, fieldLength, fieldPrecision, fieldScale, characterLength);

            var createSql = $"CREATE DOMAIN {QuoteIdentifier(domainName)} AS {typeSql}";
            if (!string.IsNullOrWhiteSpace(defaultSource)) createSql += "\n" + defaultSource;
            if (!string.IsNullOrWhiteSpace(validationSource)) createSql += "\n" + validationSource;
            if (isNotNull) createSql += "\nNOT NULL";

            var filePath = Path.Combine(domainsDirectory, SanitizeFileName(domainName) + ".sql");
            File.WriteAllText(filePath, createSql.Trim() + Environment.NewLine);
            _domainsExported++;
        }
    }

    public void ExportTablesWithColumns(FbConnection connection, string outputDirectory)
    {
        Helpers.EnsureOpen(connection);
        var tablesDirectory = Path.Combine(outputDirectory, ScriptGroup.Table.GetFolderName());
        Directory.CreateDirectory(tablesDirectory);
        const string tablesQuery = @"
            SELECT TRIM(r.RDB$RELATION_NAME) AS TABLE_NAME
            FROM RDB$RELATIONS r
            WHERE COALESCE(r.RDB$SYSTEM_FLAG, 0) = 0
              AND r.RDB$RELATION_TYPE = 0
            ORDER BY r.RDB$RELATION_NAME";

        var tableNames = new List<string>();
        using (var tablesCommand = new FbCommand(tablesQuery, connection))
        using (var reader = tablesCommand.ExecuteReader())
        {
            while (reader.Read())
                tableNames.Add(TrimFbString(reader["TABLE_NAME"]));
        }

        const string columnsQuery = @"
            SELECT
                rf.RDB$FIELD_POSITION       AS FIELD_POSITION,
                rf.RDB$FIELD_NAME           AS COLUMN_NAME,
                rf.RDB$NULL_FLAG            AS NULL_FLAG,
                rf.RDB$DEFAULT_SOURCE       AS DEFAULT_SOURCE,
                rf.RDB$FIELD_SOURCE         AS FIELD_SOURCE,
                f.RDB$FIELD_TYPE            AS FIELD_TYPE,
                f.RDB$FIELD_SUB_TYPE        AS FIELD_SUB_TYPE,
                f.RDB$FIELD_LENGTH          AS FIELD_LENGTH,
                f.RDB$FIELD_PRECISION       AS FIELD_PRECISION,
                f.RDB$FIELD_SCALE           AS FIELD_SCALE,
                f.RDB$CHARACTER_LENGTH      AS CHAR_LEN
            FROM RDB$RELATION_FIELDS rf
            JOIN RDB$FIELDS f ON f.RDB$FIELD_NAME = rf.RDB$FIELD_SOURCE
            WHERE TRIM(rf.RDB$RELATION_NAME) = @tableName
            ORDER BY rf.RDB$FIELD_POSITION";

        foreach (var tableName in tableNames)
        {
            var columnLines = new List<string>();

            using var columnsCommand = new FbCommand(columnsQuery, connection);
            columnsCommand.Parameters.AddWithValue("@tableName", tableName);

            using var reader = columnsCommand.ExecuteReader();
            while (reader.Read())
            {
                var columnName = TrimFbString(reader["COLUMN_NAME"]);
                var fieldSource = TrimFbString(reader["FIELD_SOURCE"]);

                var isNotNull = reader["NULL_FLAG"] != DBNull.Value && Convert.ToInt16(reader["NULL_FLAG"]) == 1;
                var defaultSource = reader["DEFAULT_SOURCE"] == DBNull.Value ? string.Empty : Convert.ToString(reader["DEFAULT_SOURCE"])!.Trim();

                string typeSql;
                if (!StartsWithRdb(fieldSource))
                {
                    typeSql = QuoteIdentifier(fieldSource);
                }
                else
                {
                    var fieldType = Convert.ToInt16(reader["FIELD_TYPE"]);
                    var fieldSubType = ReadNullableInt16(reader["FIELD_SUB_TYPE"]);
                    var fieldLength = ReadNullableInt(reader["FIELD_LENGTH"]);
                    var fieldPrecision = ReadNullableInt16(reader["FIELD_PRECISION"]);
                    var fieldScale = ReadNullableInt16(reader["FIELD_SCALE"]);
                    var characterLength = ReadNullableInt16(reader["CHAR_LEN"]);

                    typeSql = BuildTypeSql(fieldType, fieldSubType, fieldLength, fieldPrecision, fieldScale, characterLength);
                }

                var columnSql = $"{QuoteIdentifier(columnName)} {typeSql}";
                if (!string.IsNullOrWhiteSpace(defaultSource)) columnSql += " " + defaultSource;
                if (isNotNull) columnSql += " NOT NULL";

                columnLines.Add("  " + columnSql);
            }

            var createTableSql =
                $"CREATE TABLE {QuoteIdentifier(tableName)}\n(\n" +
                string.Join(",\n", columnLines) +
                "\n)";

            var filePath = Path.Combine(tablesDirectory, SanitizeFileName(tableName) + ".sql");
            File.WriteAllText(filePath, createTableSql.Trim() + Environment.NewLine);

            _tablesExported++;
        }
    }

    public void ExportProcedures(FbConnection connection, string outputDirectory)
    {
        Helpers.EnsureOpen(connection);
        var proceduresDirectory = Path.Combine(outputDirectory, ScriptGroup.Procedure.GetFolderName());
        Directory.CreateDirectory(proceduresDirectory);

        const string proceduresQuery = @"
        SELECT
            TRIM(p.RDB$PROCEDURE_NAME)  AS PROC_NAME,
            p.RDB$PROCEDURE_SOURCE      AS PROC_SOURCE
        FROM RDB$PROCEDURES p
        WHERE COALESCE(p.RDB$SYSTEM_FLAG, 0) = 0
        ORDER BY p.RDB$PROCEDURE_NAME";

        const string procedureParamsQuery = @"
        SELECT
            pp.RDB$PARAMETER_TYPE       AS PARAMETER_TYPE,
            pp.RDB$PARAMETER_NUMBER     AS PARAMETER_NUMBER,
            pp.RDB$PARAMETER_NAME       AS PARAMETER_NAME,
            pp.RDB$FIELD_SOURCE         AS FIELD_SOURCE,
            f.RDB$FIELD_TYPE            AS FIELD_TYPE,
            f.RDB$FIELD_SUB_TYPE        AS FIELD_SUB_TYPE,
            f.RDB$FIELD_LENGTH          AS FIELD_LENGTH,
            f.RDB$FIELD_PRECISION       AS FIELD_PRECISION,
            f.RDB$FIELD_SCALE           AS FIELD_SCALE,
            f.RDB$CHARACTER_LENGTH      AS CHAR_LEN
        FROM RDB$PROCEDURE_PARAMETERS pp
        JOIN RDB$FIELDS f ON f.RDB$FIELD_NAME = pp.RDB$FIELD_SOURCE
        WHERE TRIM(pp.RDB$PROCEDURE_NAME) = @procName
        ORDER BY pp.RDB$PARAMETER_TYPE, pp.RDB$PARAMETER_NUMBER";

        using var proceduresCommand = new FbCommand(proceduresQuery, connection);
        using var reader = proceduresCommand.ExecuteReader();
        while (reader.Read())
        {
            var procedureName = TrimFbString(reader["PROC_NAME"]);
            var procedureSource = reader["PROC_SOURCE"] == DBNull.Value ? string.Empty : Convert.ToString(reader["PROC_SOURCE"])!.TrimEnd();

            var inputParams = new List<string>();
            var outputParams = new List<string>();

            using (var paramsCommand = new FbCommand(procedureParamsQuery, connection))
            {
                paramsCommand.Parameters.AddWithValue("@procName", procedureName);

                using var paramsReader = paramsCommand.ExecuteReader();
                while (paramsReader.Read())
                {
                    var parameterType = Convert.ToInt16(paramsReader["PARAMETER_TYPE"]);
                    var parameterName = TrimFbString(paramsReader["PARAMETER_NAME"]);
                    var fieldSource = TrimFbString(paramsReader["FIELD_SOURCE"]);

                    string typeSql;
                    if (!StartsWithRdb(fieldSource))
                    {
                        typeSql = QuoteIdentifier(fieldSource);
                    }
                    else
                    {
                        var fieldType = Convert.ToInt16(paramsReader["FIELD_TYPE"]);
                        var fieldSubType = ReadNullableInt16(paramsReader["FIELD_SUB_TYPE"]);
                        var fieldLength = ReadNullableInt(paramsReader["FIELD_LENGTH"]);
                        var fieldPrecision = ReadNullableInt16(paramsReader["FIELD_PRECISION"]);
                        var fieldScale = ReadNullableInt16(paramsReader["FIELD_SCALE"]);
                        var characterLength = ReadNullableInt16(paramsReader["CHAR_LEN"]);

                        typeSql = BuildTypeSql(fieldType, fieldSubType, fieldLength, fieldPrecision, fieldScale, characterLength);
                    }

                    var paramSql = $"{QuoteIdentifier(parameterName)} {typeSql}";

                    if (parameterType == 0) inputParams.Add("  " + paramSql);
                    else outputParams.Add("  " + paramSql);
                }
            }

            var header = $"CREATE OR ALTER PROCEDURE {QuoteIdentifier(procedureName)}";
            if (inputParams.Count > 0)
                header += "\n(\n" + string.Join(",\n", inputParams) + "\n)";

            if (outputParams.Count > 0)
                header += "\nRETURNS\n(\n" + string.Join(",\n", outputParams) + "\n)";

            var body = procedureSource;
            if (string.IsNullOrWhiteSpace(body))
            {
                body = "AS\nBEGIN\nEND";
            }
            else
            {
                var trimmed = body.TrimStart();
                if (!trimmed.StartsWith("AS", StringComparison.OrdinalIgnoreCase))
                    body = "AS\n" + body;
            }

            var finalSql = header + "\n" + body;

            var filePath = Path.Combine(proceduresDirectory, SanitizeFileName(procedureName) + ".sql");
            File.WriteAllText(filePath, finalSql.Trim() + Environment.NewLine);

            _proceduresExported++;
        }
    }

    public void Report()
    {
        Console.WriteLine($"Exported count:");
        Console.WriteLine($"Domains: {_domainsExported}");
        Console.WriteLine($"Tables: {_tablesExported}");
        Console.WriteLine($"Procedures: {_proceduresExported}");
    }

    private string TrimFbString(object value)
        => value == DBNull.Value ? string.Empty : Convert.ToString(value)!.Trim();

    private int? ReadNullableInt(object value)
        => value == DBNull.Value ? null : Convert.ToInt32(value);

    private short? ReadNullableInt16(object value)
        => value == DBNull.Value ? null : Convert.ToInt16(value);

    private bool StartsWithRdb(string identifier)
        => identifier.StartsWith("RDB$", StringComparison.OrdinalIgnoreCase);

    private string QuoteIdentifier(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private string SanitizeFileName(string name)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var result = name;
        foreach (var invalidChar in invalidChars)
            result = result.Replace(invalidChar, '_');
        return result;
    }

    private string BuildTypeSql(
        short fieldType,
        short? fieldSubType,
        int? fieldLength,
        short? fieldPrecision,
        short? fieldScale,
        short? characterLength)
    {
        var scale = fieldScale.HasValue ? Math.Abs(fieldScale.Value) : 0;

        string NumericOrDecimalOrBase(string baseType)
        {
            if (fieldSubType is not (1 or 2)) return baseType;
            var numericKeyword = fieldSubType == 1 ? "NUMERIC" : "DECIMAL";
            int precision = fieldPrecision ?? 18;
            return $"{numericKeyword}({precision},{scale})";
        }

        var lengthForChar = characterLength ?? fieldLength ?? 0;

        return fieldType switch
        {
            7 => NumericOrDecimalOrBase("SMALLINT"),
            8 => NumericOrDecimalOrBase("INTEGER"),
            10 => "FLOAT",
            12 => "DATE",
            13 => "TIME",
            14 => $"CHAR({lengthForChar})",
            16 => fieldSubType switch
            {
                0 or null when fieldLength == 16 => "INT128",
                1 or 2 => NumericOrDecimalOrBase(fieldLength == 16 ? "INT128" : "BIGINT"),
                _ => fieldLength == 16 ? "INT128" : "BIGINT"
            },
            23 => "BOOLEAN",
            27 => "DOUBLE PRECISION",
            35 => "TIMESTAMP",
            37 => $"VARCHAR({lengthForChar})",
            40 => $"CSTRING({lengthForChar})",
            261 => "BLOB",
            _ => $"BLOB"
        };
    }
}