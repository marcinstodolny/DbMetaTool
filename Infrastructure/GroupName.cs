namespace DbMetaTool.Infrastructure;

public class GroupName
{
    private GroupName(string value) { Value = value; }

    public string Value { get; private set; }

    public static GroupName Domain => new("domains");
    public static GroupName Table => new("tables");
    public static GroupName Procedure => new("procedures");

    public override string ToString()
    {
        return Value;
    }
}