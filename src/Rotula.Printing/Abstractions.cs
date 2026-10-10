namespace Rotula.Printing;

/// <summary>An installed Windows printer.</summary>
/// <param name="Name">Queue name as shown in Windows; pass it to <see cref="PrinterTarget.Spooler"/>.</param>
/// <param name="PortName">Port (USB001, IP_192.168.1.5, ...).</param>
/// <param name="DriverName">Installed driver name.</param>
/// <param name="IsDefault">True for the Windows default printer.</param>
/// <param name="IsOffline">True when Windows reports the printer as offline or unavailable.</param>
public sealed record PrinterInfo(string Name, string PortName, string DriverName, bool IsDefault, bool IsOffline);

/// <summary>Lists the printers installed in Windows.</summary>
public interface IPrinterDiscovery
{
    /// <summary>Enumerates installed printers (local and connections). Returns an empty list when none or unsupported.</summary>
    Task<IReadOnlyList<PrinterInfo>> GetPrintersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads resolution and paper size of one queue from its Windows driver (no job, no printing, no network).
    /// Never throws; any failure yields <see cref="PrinterCapabilities.Unknown"/>. The default implementation returns
    /// unknown info so existing fakes keep compiling.
    /// </summary>
    /// <param name="printerName">Windows queue name.</param>
    /// <param name="ct">Cancellation (observed before the query starts).</param>
    Task<PrinterCapabilities> GetPrinterInfoAsync(string printerName, CancellationToken ct = default) =>
        Task.FromResult(PrinterCapabilities.Unknown(printerName));
}

/// <summary>
/// Sends raw bytes to an installed Windows printer through the spooler (RAW datatype, so the driver
/// does not touch the ZPL). Implemented by <see cref="WinspoolRawPrinter"/>; fake it in tests.
/// </summary>
public interface IRawPrinter
{
    /// <summary>Sends <paramref name="data"/> as one RAW document to <paramref name="printerName"/>.</summary>
    /// <param name="printerName">Windows queue name.</param>
    /// <param name="documentName">Name shown in the print queue.</param>
    /// <param name="data">Bytes to send.</param>
    /// <param name="cancellationToken">Cancels between spooler writes.</param>
    Task<PrintResult> SendAsync(string printerName, string documentName, byte[] data, CancellationToken cancellationToken = default);
}

/// <summary>Sends raw bytes to a printer listening on a TCP port (JetDirect style, usually 9100).</summary>
public interface INetworkPrinter
{
    /// <summary>Connects, sends <paramref name="data"/> and closes.</summary>
    Task<PrintResult> SendAsync(string host, int port, byte[] data, NetworkPrinterOptions options, CancellationToken cancellationToken = default);
}

/// <summary>Timeouts for direct TCP printing.</summary>
/// <param name="ConnectTimeout">Maximum time to establish the connection.</param>
/// <param name="SendTimeout">Maximum time to push all bytes (a stalled or powered-off printer stops accepting data).</param>
public sealed record NetworkPrinterOptions(TimeSpan ConnectTimeout, TimeSpan SendTimeout)
{
    /// <summary>5 seconds to connect, 15 seconds to send.</summary>
    public static NetworkPrinterOptions Default { get; } = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));
}

/// <summary>How a job reaches the printer.</summary>
public enum PrinterTransport
{
    /// <summary>Through the installed Windows driver and spooler (USB, shared, network queues).</summary>
    Spooler,
    /// <summary>Straight to host:port over TCP/IP.</summary>
    Tcp,
}

/// <summary>Where to print: a Windows queue or a TCP endpoint.</summary>
public sealed record PrinterTarget
{
    /// <summary>The default raw TCP port of Zebra and compatible printers.</summary>
    public const int DefaultTcpPort = 9100;

    private PrinterTarget() { }

    /// <summary>Transport in use.</summary>
    public PrinterTransport Transport { get; private init; }
    /// <summary>Windows queue name (spooler transport).</summary>
    public string PrinterName { get; private init; } = string.Empty;
    /// <summary>Host name or IP address (TCP transport).</summary>
    public string Host { get; private init; } = string.Empty;
    /// <summary>TCP port (TCP transport).</summary>
    public int Port { get; private init; } = DefaultTcpPort;
    /// <summary>TCP timeouts.</summary>
    public NetworkPrinterOptions Network { get; private init; } = NetworkPrinterOptions.Default;

    /// <summary>Targets an installed Windows printer (use this for USB printers).</summary>
    public static PrinterTarget Spooler(string printerName) =>
        new() { Transport = PrinterTransport.Spooler, PrinterName = printerName };

    /// <summary>Targets a network printer directly by TCP.</summary>
    public static PrinterTarget TcpIp(string host, int port = DefaultTcpPort, NetworkPrinterOptions? options = null) =>
        new() { Transport = PrinterTransport.Tcp, Host = host, Port = port, Network = options ?? NetworkPrinterOptions.Default };
}
