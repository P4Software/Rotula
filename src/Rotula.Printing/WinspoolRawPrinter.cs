using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Rotula.Printing;

/// <summary>
/// Windows spooler printing with the RAW datatype (winspool.drv). Works for any installed queue, which
/// is how USB printers are reached: the Windows driver owns the USB port, Rotula only hands it bytes.
/// All calls are blocking Win32 calls, so they run on the thread pool and never touch the UI thread.
/// </summary>
public sealed class WinspoolRawPrinter : IRawPrinter, IPrinterDiscovery
{
    private const int WriteChunk = 64 * 1024;
    private readonly IPrinterCapsReader _capsReader;

    /// <summary>Creates the printer using the real Win32 capability queries.</summary>
    public WinspoolRawPrinter() : this(new Win32PrinterCapsReader()) { }

    /// <summary>Creates the printer with an injected capability reader (tests).</summary>
    internal WinspoolRawPrinter(IPrinterCapsReader capsReader) => _capsReader = capsReader ?? throw new ArgumentNullException(nameof(capsReader));

    /// <inheritdoc />
    public Task<PrinterCapabilities> GetPrinterInfoAsync(string printerName, CancellationToken ct = default)
    {
        string name = printerName ?? string.Empty;
        if (!OperatingSystem.IsWindows() || ct.IsCancellationRequested) return Task.FromResult(PrinterCapabilities.Unknown(name));
        // Off the UI thread: DocumentProperties can take a second or more on a network queue. Detect never throws.
        return Task.Run(() => PrinterInfoDetector.Detect(name, _capsReader), CancellationToken.None);
    }

    /// <inheritdoc />
    public Task<PrintResult> SendAsync(string printerName, string documentName, byte[] data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!OperatingSystem.IsWindows()) return Task.FromResult(PrintResult.Fail(PrintErrorKind.Unsupported));
        if (string.IsNullOrWhiteSpace(printerName)) return Task.FromResult(PrintResult.Fail(PrintErrorKind.PrinterNotFound, printerName ?? string.Empty));
        if (cancellationToken.IsCancellationRequested) return Task.FromResult(PrintResult.Fail(PrintErrorKind.Cancelled));
        return Task.Run(() => SendCore(printerName, documentName, data, cancellationToken), CancellationToken.None);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PrinterInfo>> GetPrintersAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return Task.FromResult<IReadOnlyList<PrinterInfo>>([]);
        return Task.Run(() => (IReadOnlyList<PrinterInfo>)Enumerate(cancellationToken), cancellationToken);
    }

    private static PrintResult SendCore(string printerName, string documentName, byte[] data, CancellationToken ct)
    {
        if (!NativeMethods.OpenPrinter(printerName, out var handle, IntPtr.Zero))
            return PrintResult.Fail(PrinterErrors.FromWin32(Marshal.GetLastWin32Error(), printerName));

        int written = 0;
        try
        {
            // Check status first: Windows happily accepts jobs for an unplugged USB printer and they then
            // sit in the queue forever, which is worse for the user than an immediate "offline" message.
            if (TryGetState(handle, out _, out _, out bool offline) && offline)
                return PrintResult.Fail(PrintErrorKind.Offline, printerName);

            var doc = new NativeMethods.DOC_INFO_1 { pDocName = string.IsNullOrEmpty(documentName) ? "Rotula label" : documentName, pOutputFile = null, pDatatype = "RAW" };
            if (NativeMethods.StartDocPrinter(handle, 1, ref doc) == 0)
                return PrintResult.Fail(PrinterErrors.FromWin32(Marshal.GetLastWin32Error(), printerName));
            try
            {
                if (!NativeMethods.StartPagePrinter(handle))
                    return PrintResult.Fail(PrinterErrors.FromWin32(Marshal.GetLastWin32Error(), printerName));
                try
                {
                    var buffer = GCHandle.Alloc(data, GCHandleType.Pinned);
                    try
                    {
                        while (written < data.Length)
                        {
                            if (ct.IsCancellationRequested) return PrintResult.Fail(PrintError.Create(PrintErrorKind.Cancelled), written);
                            int n = Math.Min(WriteChunk, data.Length - written);
                            var ptr = Marshal.UnsafeAddrOfPinnedArrayElement(data, written);
                            if (!NativeMethods.WritePrinter(handle, ptr, n, out int done) || done <= 0)
                                return PrintResult.Fail(PrinterErrors.FromWin32(Marshal.GetLastWin32Error(), printerName), written);
                            written += done;
                        }
                    }
                    finally { buffer.Free(); }
                }
                finally { NativeMethods.EndPagePrinter(handle); }
            }
            finally { NativeMethods.EndDocPrinter(handle); }
            return PrintResult.Ok(written);
        }
        finally
        {
            NativeMethods.ClosePrinter(handle);
        }
    }

    /// <summary>True when Windows reports the open queue as offline; false when it is online or the state cannot be read.</summary>
    internal static bool IsQueueOffline(IntPtr handle) => TryGetState(handle, out _, out _, out bool offline) && offline;

    /// <summary>Reads status/attributes of an open printer. Returns false when the query itself fails (state unknown).</summary>
    private static bool TryGetState(IntPtr handle, out uint status, out uint attributes, out bool offline)
    {
        status = attributes = 0;
        offline = false;
        NativeMethods.GetPrinter(handle, 2, IntPtr.Zero, 0, out int needed);
        if (needed <= 0) return false;
        var buf = Marshal.AllocHGlobal(needed);
        try
        {
            if (!NativeMethods.GetPrinter(handle, 2, buf, needed, out _)) return false;
            var info = Marshal.PtrToStructure<NativeMethods.PRINTER_INFO_2>(buf);
            status = info.Status;
            attributes = info.Attributes;
            offline = PrinterErrors.IsOffline(status, attributes);
            return true;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static List<PrinterInfo> Enumerate(CancellationToken ct)
    {
        const uint flags = NativeMethods.PRINTER_ENUM_LOCAL | NativeMethods.PRINTER_ENUM_CONNECTIONS;
        NativeMethods.EnumPrinters(flags, null, 2, IntPtr.Zero, 0, out int needed, out _);
        var result = new List<PrinterInfo>();
        if (needed <= 0) return result;

        string? defaultName = GetDefaultPrinterName();
        var buf = Marshal.AllocHGlobal(needed);
        try
        {
            if (!NativeMethods.EnumPrinters(flags, null, 2, buf, needed, out _, out int returned)) return result;
            int size = Marshal.SizeOf<NativeMethods.PRINTER_INFO_2>();
            for (int i = 0; i < returned; i++)
            {
                ct.ThrowIfCancellationRequested();
                var info = Marshal.PtrToStructure<NativeMethods.PRINTER_INFO_2>(buf + i * size);
                string name = info.pPrinterName ?? string.Empty;
                result.Add(new PrinterInfo(name, info.pPortName ?? string.Empty, info.pDriverName ?? string.Empty,
                    string.Equals(name, defaultName, StringComparison.OrdinalIgnoreCase), PrinterErrors.IsOffline(info.Status, info.Attributes)));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return result.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string? GetDefaultPrinterName()
    {
        int size = 0;
        NativeMethods.GetDefaultPrinter(null, ref size);
        if (size <= 0) return null;
        var sb = new System.Text.StringBuilder(size);
        return NativeMethods.GetDefaultPrinter(sb, ref size) ? sb.ToString() : null;
    }
}

/// <summary>Maps Win32 spooler error codes and printer states to <see cref="PrintError"/>.</summary>
internal static class PrinterErrors
{
    internal const int ErrorAccessDenied = 5;
    internal const int ErrorInvalidPrinterName = 1801;
    internal const int ErrorPrinterNotFound = 3012;      // ERROR_PRINTER_NOT_FOUND
    internal const int ErrorFileNotFound = 2;
    internal const int ErrorPrinterOffline = 1722;       // RPC server unavailable: remote queue unreachable
    internal const int ErrorNotReady = 21;
    internal const int ErrorBadNetPath = 53;

    private const uint StatusOffline = 0x00000080;
    private const uint StatusNotAvailable = 0x00001000;
    private const uint AttributeWorkOffline = 0x00000400;

    /// <summary>True when Windows flags the queue as offline or unavailable.</summary>
    internal static bool IsOffline(uint status, uint attributes) =>
        (status & (StatusOffline | StatusNotAvailable)) != 0 || (attributes & AttributeWorkOffline) != 0;

    /// <summary>Translates a GetLastError value from the spooler calls.</summary>
    internal static PrintError FromWin32(int code, string printerName) => code switch
    {
        ErrorAccessDenied => PrintError.Create(PrintErrorKind.AccessDenied, printerName),
        ErrorInvalidPrinterName or ErrorPrinterNotFound or ErrorFileNotFound or ErrorBadNetPath
            => PrintError.Create(PrintErrorKind.PrinterNotFound, printerName),
        ErrorPrinterOffline or ErrorNotReady => PrintError.Create(PrintErrorKind.Offline, printerName),
        _ => PrintError.Create(PrintErrorKind.Unknown, printerName, code.ToString(), new Win32Exception(code).Message),
    };
}

internal static class NativeMethods
{
    internal const uint PRINTER_ENUM_LOCAL = 0x2;
    internal const uint PRINTER_ENUM_CONNECTIONS = 0x4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DOC_INFO_1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? pDocName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pOutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pDatatype;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct PRINTER_INFO_2
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? pServerName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pPrinterName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pShareName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pPortName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pDriverName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pComment;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pLocation;
        public IntPtr pDevMode;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pSepFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pPrintProcessor;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pDatatype;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pParameters;
        public IntPtr pSecurityDescriptor;
        public uint Attributes;
        public uint Priority;
        public uint DefaultPriority;
        public uint StartTime;
        public uint UntilTime;
        public uint Status;
        public uint cJobs;
        public uint AveragePPM;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern int StartDocPrinter(IntPtr hPrinter, int level, ref DOC_INFO_1 pDocInfo);

    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBuf, int cbBuf, out int pcWritten);

    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadPrinter(IntPtr hPrinter, IntPtr pBuf, int cbBuf, out int pNoBytesRead);

    [DllImport("winspool.drv", EntryPoint = "GetPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetPrinter(IntPtr hPrinter, int level, IntPtr pPrinter, int cbBuf, out int pcbNeeded);

    [DllImport("winspool.drv", EntryPoint = "EnumPrintersW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumPrinters(uint flags, string? name, int level, IntPtr pPrinterEnum, int cbBuf, out int pcbNeeded, out int pcReturned);

    [DllImport("winspool.drv", EntryPoint = "DocumentPropertiesW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern int DocumentProperties(IntPtr hwnd, IntPtr hPrinter, string pDeviceName, IntPtr pDevModeOutput, IntPtr pDevModeInput, int fMode);

    [DllImport("winspool.drv", EntryPoint = "DeviceCapabilitiesW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern int DeviceCapabilities(string pDevice, string? pPort, ushort fwCapability, IntPtr pOutput, IntPtr pDevMode);

    [DllImport("gdi32.dll", EntryPoint = "CreateICW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateIC(string? pwszDriver, string? pwszDevice, string? pszPort, IntPtr pdm);

    [DllImport("gdi32.dll")]
    internal static extern int GetDeviceCaps(IntPtr hdc, int index);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteDC(IntPtr hdc);

    [DllImport("winspool.drv", EntryPoint = "GetDefaultPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetDefaultPrinter(System.Text.StringBuilder? pszBuffer, ref int pcchBuffer);
}
