using System.Runtime.InteropServices;

namespace Rotula.Printing;

/// <summary>Raw, unmapped answers from the Windows driver for one queue. Any member can be null when that query failed.</summary>
/// <param name="DriverName">Driver name from the spooler.</param>
/// <param name="DevModeXDpi">dmPrintQuality when it is a positive resolution.</param>
/// <param name="DevModeYDpi">dmYResolution.</param>
/// <param name="IcXDpi">GetDeviceCaps(LOGPIXELSX) on a printer information context.</param>
/// <param name="IcYDpi">GetDeviceCaps(LOGPIXELSY).</param>
/// <param name="EnumDpi">The only resolution listed by DC_ENUMRESOLUTIONS, when the driver lists exactly one.</param>
/// <param name="PaperWidthTenthsMm">Current form width in 0.1 mm.</param>
/// <param name="PaperLengthTenthsMm">Current form length in 0.1 mm.</param>
internal sealed record RawPrinterCaps(
    string? DriverName,
    int? DevModeXDpi, int? DevModeYDpi,
    int? IcXDpi, int? IcYDpi,
    int? EnumDpi,
    int? PaperWidthTenthsMm, int? PaperLengthTenthsMm);

/// <summary>The Win32 side of printer detection, behind an interface so the logic can be tested with a fake.</summary>
internal interface IPrinterCapsReader
{
    /// <summary>Reads the driver's answers for <paramref name="printerName"/>. Returns null when the queue cannot be opened. May throw; the caller guards.</summary>
    RawPrinterCaps? Read(string printerName);
}

/// <summary>
/// Read-only Win32 implementation: OpenPrinter + DocumentProperties(DM_OUT_BUFFER) for the default DEVMODE,
/// CreateIC("WINSPOOL") + GetDeviceCaps for the resolution, DeviceCapabilities for resolutions and forms.
/// None of these start a document, create an output DC or touch the network.
/// </summary>
internal sealed class Win32PrinterCapsReader : IPrinterCapsReader
{
    // DEVMODEW field offsets (the printer half of the union starts at 76).
    private const int OffDmSize = 68, OffFields = 72, OffPaperSize = 78, OffPaperLength = 80, OffPaperWidth = 82,
        OffPrintQuality = 90, OffYResolution = 96;
    private const uint DM_PAPERSIZE = 0x2, DM_PAPERLENGTH = 0x4, DM_PAPERWIDTH = 0x8, DM_PRINTQUALITY = 0x400, DM_YRESOLUTION = 0x2000;
    private const int DM_OUT_BUFFER = 2;
    private const int DMPAPER_USER = 256;
    private const ushort DC_PAPERS = 2, DC_PAPERSIZE = 3, DC_ENUMRESOLUTIONS = 13;
    private const int LOGPIXELSX = 88, LOGPIXELSY = 90;

    public RawPrinterCaps? Read(string printerName)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(printerName)) return null;
        if (!NativeMethods.OpenPrinter(printerName, out var handle, IntPtr.Zero)) return null;

        string? driver = null, port = null;
        int? mx = null, my = null, paperW = null, paperL = null, enumDpi = null;
        try
        {
            (driver, port) = ReadDriverAndPort(handle);
            try { ReadDevMode(handle, printerName, port, out mx, out my, out paperW, out paperL); } catch { /* keep other sources */ }
        }
        finally { NativeMethods.ClosePrinter(handle); }

        int? ix = null, iy = null;
        try { ReadInfoContext(printerName, out ix, out iy); } catch { }
        try { enumDpi = ReadSingleEnumResolution(printerName, port); } catch { }

        return new RawPrinterCaps(driver, mx, my, ix, iy, enumDpi, paperW, paperL);
    }

    private static (string? Driver, string? Port) ReadDriverAndPort(IntPtr handle)
    {
        NativeMethods.GetPrinter(handle, 2, IntPtr.Zero, 0, out int needed);
        if (needed <= 0) return (null, null);
        var buf = Marshal.AllocHGlobal(needed);
        try
        {
            if (!NativeMethods.GetPrinter(handle, 2, buf, needed, out _)) return (null, null);
            var info = Marshal.PtrToStructure<NativeMethods.PRINTER_INFO_2>(buf);
            return (info.pDriverName, info.pPortName);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static void ReadDevMode(IntPtr handle, string name, string? port, out int? xDpi, out int? yDpi, out int? paperW, out int? paperL)
    {
        xDpi = yDpi = paperW = paperL = null;
        int size = NativeMethods.DocumentProperties(IntPtr.Zero, handle, name, IntPtr.Zero, IntPtr.Zero, 0);
        if (size <= 0) return;
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            // Zero first: a driver that fills fewer bytes than it reports must not leave garbage in the fields we read.
            for (int i = 0; i < size; i++) Marshal.WriteByte(buf, i, 0);
            if (NativeMethods.DocumentProperties(IntPtr.Zero, handle, name, buf, IntPtr.Zero, DM_OUT_BUFFER) < 0) return;

            int dmSize = (ushort)Marshal.ReadInt16(buf, OffDmSize);
            uint fields = (uint)Marshal.ReadInt32(buf, OffFields);
            // Guard every read: a short DEVMODE from an old driver does not contain the later fields.
            bool Has(int offset) => dmSize >= offset + 2 && offset + 2 <= size;

            // dmPrintQuality is a resolution only when positive (negative values are the DMRES_* draft/low/medium/high enums).
            if ((fields & DM_PRINTQUALITY) != 0 && Has(OffPrintQuality) && Marshal.ReadInt16(buf, OffPrintQuality) > 0)
                xDpi = Marshal.ReadInt16(buf, OffPrintQuality);
            if ((fields & DM_YRESOLUTION) != 0 && Has(OffYResolution) && Marshal.ReadInt16(buf, OffYResolution) > 0)
                yDpi = Marshal.ReadInt16(buf, OffYResolution);
            // Drivers that give only dmPrintQuality mean a square resolution.
            if (xDpi is not null && yDpi is null) yDpi = xDpi;

            if ((fields & DM_PAPERWIDTH) != 0 && (fields & DM_PAPERLENGTH) != 0 && Has(OffPaperWidth) && Has(OffPaperLength))
            {
                int pw = Marshal.ReadInt16(buf, OffPaperWidth), pl = Marshal.ReadInt16(buf, OffPaperLength);
                if (pw > 0 && pl > 0) { paperW = pw; paperL = pl; }
            }
            else if ((fields & DM_PAPERSIZE) != 0 && Has(OffPaperSize))
            {
                // A predefined form (dmPaperSize is an index): look its size up in the driver's form list.
                int index = Marshal.ReadInt16(buf, OffPaperSize);
                if (index > 0 && index != DMPAPER_USER) LookUpForm(name, port, index, out paperW, out paperL);
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static void LookUpForm(string name, string? port, int paperIndex, out int? widthTenths, out int? lengthTenths)
    {
        widthTenths = lengthTenths = null;
        int count = NativeMethods.DeviceCapabilities(name, port, DC_PAPERS, IntPtr.Zero, IntPtr.Zero);
        if (count <= 0 || count > 4096) return;
        var papers = Marshal.AllocHGlobal(count * 2);
        var sizes = Marshal.AllocHGlobal(count * 8);
        try
        {
            if (NativeMethods.DeviceCapabilities(name, port, DC_PAPERS, papers, IntPtr.Zero) != count) return;
            if (NativeMethods.DeviceCapabilities(name, port, DC_PAPERSIZE, sizes, IntPtr.Zero) != count) return;
            for (int i = 0; i < count; i++)
            {
                if (Marshal.ReadInt16(papers, i * 2) != paperIndex) continue;
                int x = Marshal.ReadInt32(sizes, i * 8), y = Marshal.ReadInt32(sizes, i * 8 + 4); // POINT in 0.1 mm
                if (x > 0 && y > 0) { widthTenths = x; lengthTenths = y; }
                return;
            }
        }
        finally { Marshal.FreeHGlobal(papers); Marshal.FreeHGlobal(sizes); }
    }

    private static void ReadInfoContext(string name, out int? x, out int? y)
    {
        x = y = null;
        // An information context (CreateIC) can query capabilities but cannot draw or start a job.
        var dc = NativeMethods.CreateIC("WINSPOOL", name, null, IntPtr.Zero);
        if (dc == IntPtr.Zero) return;
        try
        {
            int gx = NativeMethods.GetDeviceCaps(dc, LOGPIXELSX), gy = NativeMethods.GetDeviceCaps(dc, LOGPIXELSY);
            if (gx > 0) x = gx;
            if (gy > 0) y = gy;
        }
        finally { NativeMethods.DeleteDC(dc); }
    }

    private static int? ReadSingleEnumResolution(string name, string? port)
    {
        int count = NativeMethods.DeviceCapabilities(name, port, DC_ENUMRESOLUTIONS, IntPtr.Zero, IntPtr.Zero);
        // Only a single listed resolution is unambiguous; a list of several says what the printer *can* do, not what it is.
        if (count != 1) return null;
        var buf = Marshal.AllocHGlobal(8);
        try
        {
            if (NativeMethods.DeviceCapabilities(name, port, DC_ENUMRESOLUTIONS, buf, IntPtr.Zero) != 1) return null;
            int v = Marshal.ReadInt32(buf, 0);
            return v > 0 ? v : null;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
