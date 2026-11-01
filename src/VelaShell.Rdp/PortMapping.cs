using System.Globalization;

namespace VelaShell.Rdp;

/// <summary>An explicitly selected Windows COM or LPT port. Device paths and aliases are not accepted.</summary>
public sealed record PortMapping
{
    public PortMapping(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string canonical = name.ToUpperInvariant();
        if (canonical.Length is < 4 or > 7 || !(canonical.StartsWith("COM", StringComparison.Ordinal) || canonical.StartsWith("LPT", StringComparison.Ordinal))
            || canonical[3] == '0' || !canonical[3..].All(char.IsAsciiDigit)
            || !int.TryParse(canonical.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number > 9999)
        {
            throw new ArgumentException("Use a port name such as COM3 or LPT1 (1–9999), without a path or colon.", nameof(name));
        }
        Name = canonical;
    }

    public string Name { get; }
    public bool IsSerial => Name.StartsWith("COM", StringComparison.Ordinal);

    internal static PortMapping[] Validate(IReadOnlyList<PortMapping> ports)
    {
        ArgumentNullException.ThrowIfNull(ports);
        if (ports.Count > 32) { throw new ArgumentException("At most 32 ports can be redirected.", nameof(ports)); }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PortMapping port in ports)
        {
            if (port is null || !names.Add(port.Name)) { throw new ArgumentException("Port names must be unique.", nameof(ports)); }
        }
        return [.. ports];
    }
}
