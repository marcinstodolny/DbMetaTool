using FirebirdSql.Data.FirebirdClient;

namespace DbMetaTool.Infrastructure;

public static class FirebirdExporter
{
    public static int ExportDomains(FbConnection connection, string outputDirectory)
    {
        EnsureOpen(connection);
        var domainsDirectory = Path.Combine(outputDirectory, "domains");
        Directory.CreateDirectory(domainsDirectory);
        
        var domainExported = 0;
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
            domainExported++;
        }
        return domainExported;
    }

    private static void EnsureOpen(FbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
        }
    }

    static string TrimFbString(object value)
        => value == DBNull.Value ? string.Empty : Convert.ToString(value)!.Trim();

    static int? ReadNullableInt(object value)
        => value == DBNull.Value ? null : Convert.ToInt32(value);

    static short? ReadNullableInt16(object value)
        => value == DBNull.Value ? null : Convert.ToInt16(value);

    static string QuoteIdentifier(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    static string SanitizeFileName(string name)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var result = name;
        foreach (var invalidChar in invalidChars)
            result = result.Replace(invalidChar, '_');
        return result;
    }

    static string BuildTypeSql(
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