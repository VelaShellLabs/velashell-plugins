namespace VelaShell.Rdp;

/// <summary>An explicitly selected legacy printer queue. The server needs a matching printer driver.</summary>
public sealed record PrinterMapping
{
    public PrinterMapping(string printerName, string driverName, bool isDefault = false, bool isNetwork = false)
    {
        ValidateName(printerName, nameof(printerName));
        ValidateName(driverName, nameof(driverName));
        PrinterName = printerName;
        DriverName = driverName;
        IsDefault = isDefault;
        IsNetwork = isNetwork;
    }

    public string PrinterName { get; }
    public string DriverName { get; }
    public bool IsDefault { get; }
    public bool IsNetwork { get; }

    private static void ValidateName(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024 || value.Any(char.IsControl))
        {
            throw new ArgumentException("Printer and driver names must be nonempty and contain no control characters (maximum 1024 characters).", parameter);
        }
    }

    internal static PrinterMapping[] Validate(IReadOnlyList<PrinterMapping> printers)
    {
        ArgumentNullException.ThrowIfNull(printers);
        if (printers.Count > 16) { throw new ArgumentException("At most 16 printers can be redirected.", nameof(printers)); }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int defaults = 0;
        foreach (PrinterMapping printer in printers)
        {
            if (printer is null || !names.Add(printer.PrinterName) || (printer.IsDefault && ++defaults > 1))
            {
                throw new ArgumentException("Printer queues must be unique and at most one can be the default.", nameof(printers));
            }
        }
        return [.. printers];
    }
}
