using DbMetaTool.Infrastructure;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace DbMetaTool.Tests;

public class SqlNormalizationTests
{
    [Fact]
    public void NormalizeSqlText_RemovesBomCommentsSetTermAndTrailingSemicolon()
    {
        var rawSql = "\uFEFFSET TERM ^;\n-- header comment\n/* block comment */\nCREATE TABLE test_table (\n    id INT\n);\nSET TERM ;^\n";

        var normalized = Helpers.NormalizeSqlText(rawSql);

        Assert.Equal("CREATE TABLE test_table (\n    id INT\n)", normalized);
    }

    [Fact]
    public void TryExtractObjectName_ParsesNormalizedSqlWithNoise()
    {
        var rawSql = "\uFEFF-- file comment\nSET TERM ^;\nCREATE TABLE \"MixedName\" (\n    id INT\n);\n/* after */";
        var normalized = Helpers.NormalizeSqlText(rawSql);

        var result = InvokePrivateMethod<string?>(new FirebirdUpdater(), "TryExtractObjectName", normalized, "TABLE");

        Assert.Equal("MixedName", result);
    }

    [Fact]
    public void ParseCreateTableColumns_ParsesColumnsFromNormalizedSql()
    {
        var rawSql = "\uFEFF/*before*/\nSET TERM ^;\nCREATE TABLE sample_table (\n    id INT,\n    name VARCHAR(100)\n);\n-- trailing comment\nSET TERM ;^\n";
        var normalized = Helpers.NormalizeSqlText(rawSql);

        var updater = new FirebirdUpdater();
        var domainDefinitions = CreateEmptyDomainDictionary();

        var parsed = InvokePrivateMethod<object>(updater, "ParseCreateTableColumns", normalized, domainDefinitions, null)!;

        var tableName = parsed.GetType().GetProperty("TableName", BindingFlags.Public | BindingFlags.Instance)!.GetValue(parsed);
        var columns = (IEnumerable)parsed.GetType().GetProperty("Columns", BindingFlags.Public | BindingFlags.Instance)!.GetValue(parsed)!;

        Assert.Equal("SAMPLE_TABLE", tableName);
        Assert.Equal(2, columns.Cast<object>().Count());
    }

    private static object CreateEmptyDomainDictionary()
    {
        var domainDefinitionType = typeof(FirebirdUpdater).GetNestedType("DomainDefinition", BindingFlags.NonPublic)!;
        var dictionaryType = typeof(Dictionary<,>).MakeGenericType(typeof(string), domainDefinitionType);
        return Activator.CreateInstance(dictionaryType)!;
    }

    private static T? InvokePrivateMethod<T>(object instance, string methodName, params object[] arguments)
    {
        var method = instance.GetType()
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == arguments.Length);
        if (method == null)
            throw new InvalidOperationException($"Method {methodName} not found.");

        return (T?)method.Invoke(instance, arguments);
    }
}