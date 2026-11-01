namespace Octopushy.Definitions;

/// <summary>Domain names as a DNS server compares them: without case or a final dot.</summary>
public static class DnsName
{
    public static string Key(string name) => name.TrimEnd('.').ToLowerInvariant();

    /// <summary>Whether a blocked or allowed entry applies to a name, which it does to its subdomains too.</summary>
    public static bool Covers(string entry, string name)
    {
        var (covering, covered) = (Key(entry), Key(name));
        return covered == covering || covered.EndsWith($".{covering}", StringComparison.Ordinal);
    }
}
