using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Rotula.Printing;

/// <summary>
/// The Win32 calls the Windows-driver (picture) print path needs: winspool to read and adjust the driver's settings (DEVMODE), gdi32 to measure
/// the page and to draw pictures into a print job. Every signature and constant was checked against Microsoft Learn (the numeric values of the
/// constants come from wingdi.h / winerror.h, which Learn does not list).
/// <para>
/// <see cref="NativeMethods"/> (used by ZPL printing and printer detection) already declares OpenPrinter, DocumentProperties, CreateIC,
/// GetDeviceCaps and DeleteDC with raw <see cref="IntPtr"/> handles. They are declared again here on purpose: this path owns a printer handle and
/// up to two device contexts across a whole job, possibly hundreds of pages long, and an exception thrown by the caller's lazy page source in the
/// middle of it must never leak them. <see cref="SafeHandle"/>s make the release unconditional, and the ZPL path stays byte for byte unchanged.
/// </para>
/// <para>
/// <c>DllImport</c> with explicit <c>...W</c> entry points and <see cref="CharSet.Unicode"/>, as in <see cref="NativeMethods"/>:
/// <c>LibraryImport</c> would need <c>AllowUnsafeBlocks</c> in the project, which this library does not enable.
/// </para>
/// </summary>
internal static class GdiNative
{
    // GetDeviceCaps indexes (wingdi.h).
    /// <summary>Width of the printable area in device dots.</summary>
    internal const int HORZRES = 8;
    /// <summary>Height of the printable area in device dots.</summary>
    internal const int VERTRES = 10;
    /// <summary>Horizontal device dots per inch.</summary>
    internal const int LOGPIXELSX = 88;
    /// <summary>Vertical device dots per inch.</summary>
    internal const int LOGPIXELSY = 90;
    /// <summary>Width of the physical page in device dots.</summary>
    internal const int PHYSICALWIDTH = 110;
    /// <summary>Height of the physical page in device dots.</summary>
    internal const int PHYSICALHEIGHT = 111;
    /// <summary>Distance from the left edge of the physical page to the printable area, in device dots.</summary>
    internal const int PHYSICALOFFSETX = 112;
    /// <summary>Distance from the top edge of the physical page to the printable area, in device dots.</summary>
    internal const int PHYSICALOFFSETY = 113;

    // DocumentProperties modes (winspool.h).
    /// <summary>DM_OUT_BUFFER (DM_COPY): write the driver's current settings into the output DEVMODE.</summary>
    internal const int DM_OUT_BUFFER = 2;
    /// <summary>DM_IN_BUFFER (DM_MODIFY): merge the fields flagged in the input DEVMODE into the driver's settings.</summary>
    internal const int DM_IN_BUFFER = 8;

    // DEVMODE dmFields flags and values (wingdi.h).
    /// <summary>dmOrientation is valid.</summary>
    internal const uint DM_ORIENTATION = 0x00000001;
    /// <summary>dmPaperSize is valid.</summary>
    internal const uint DM_PAPERSIZE = 0x00000002;
    /// <summary>dmPaperLength is valid.</summary>
    internal const uint DM_PAPERLENGTH = 0x00000004;
    /// <summary>dmPaperWidth is valid.</summary>
    internal const uint DM_PAPERWIDTH = 0x00000008;
    /// <summary>dmCopies is valid.</summary>
    internal const uint DM_COPIES = 0x00000100;
    /// <summary>dmFormName is valid.</summary>
    internal const uint DM_FORMNAME = 0x00010000;
    /// <summary>Portrait orientation: the page is as wide as dmPaperWidth.</summary>
    internal const short DMORIENT_PORTRAIT = 1;
    /// <summary>A user-defined paper size whose dimensions are dmPaperWidth and dmPaperLength.</summary>
    internal const short DMPAPER_USER = 256;

    // Drawing (wingdi.h).
    /// <summary>Stretch mode that deletes or repeats whole rows and columns, never averaging pixels (STRETCH_DELETESCANS).</summary>
    internal const int COLORONCOLOR = 3;
    /// <summary>The colour table holds literal RGB values.</summary>
    internal const uint DIB_RGB_COLORS = 0;
    /// <summary>Raster operation: copy the source onto the destination.</summary>
    internal const uint SRCCOPY = 0x00CC0020;
    /// <summary>Uncompressed DIB.</summary>
    internal const uint BI_RGB = 0;
    /// <summary>GDI_ERROR (0xFFFFFFFF) as a signed int, a failure value of StretchDIBits.</summary>
    internal const int GDI_ERROR = -1;

    /// <summary>
    /// DOCINFOW: the job name and the optional output file passed to <see cref="StartDoc"/>. Layout per Learn: <c>int cbSize</c>, three
    /// <c>LPCWSTR</c> and a <c>DWORD fwType</c>, sequential with natural alignment.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DOCINFOW
    {
        /// <summary>Size of this structure in bytes.</summary>
        public int cbSize;
        /// <summary>Name of the job in the Windows print queue.</summary>
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszDocName;
        /// <summary>File the output goes to instead of the printer; null in production. Tests use it so "Microsoft Print to PDF" asks nothing.</summary>
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszOutput;
        /// <summary>Spool data type; null lets the spooler choose (Learn: the requested type might be ignored anyway).</summary>
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszDatatype;
        /// <summary>Must be zero (no application banding, no destination-reading raster operations).</summary>
        public uint fwType;
    }

    /// <summary>
    /// BITMAPINFOHEADER, 40 bytes, blittable. It is written in front of the 256-entry grey palette to form the BITMAPINFO that
    /// <see cref="StretchDIBits"/> reads (see <see cref="GdiBitmapInfo"/>).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        /// <summary>Size of this header (40), not counting the colour table.</summary>
        public uint biSize;
        /// <summary>Width in pixels.</summary>
        public int biWidth;
        /// <summary>Height in pixels; negative means the rows are stored from the top down.</summary>
        public int biHeight;
        /// <summary>Always 1.</summary>
        public ushort biPlanes;
        /// <summary>Bits per pixel (8: one palette index per pixel).</summary>
        public ushort biBitCount;
        /// <summary>BI_RGB (uncompressed).</summary>
        public uint biCompression;
        /// <summary>Image size in bytes; Learn allows 0 for BI_RGB.</summary>
        public uint biSizeImage;
        /// <summary>Horizontal pixels per metre (informative, unused by StretchDIBits).</summary>
        public int biXPelsPerMeter;
        /// <summary>Vertical pixels per metre (informative, unused by StretchDIBits).</summary>
        public int biYPelsPerMeter;
        /// <summary>Number of palette entries that follow the header.</summary>
        public uint biClrUsed;
        /// <summary>0: all colours are important.</summary>
        public uint biClrImportant;
    }

    /// <summary>
    /// Opens the printer so <see cref="DocumentProperties"/> can read and validate its settings. Failing here is how a missing printer
    /// (ERROR_INVALID_PRINTER_NAME) or a printer the user may not use (ERROR_ACCESS_DENIED) is told apart from a driver that refuses a page.
    /// </summary>
    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenPrinter(string pPrinterName, out SafePrinterHandle phPrinter, IntPtr pDefault);

    /// <summary>Closes a handle from <see cref="OpenPrinter"/>; called only by <see cref="SafePrinterHandle"/>.</summary>
    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ClosePrinter(IntPtr hPrinter);

    /// <summary>
    /// Reads and validates the driver's DEVMODE without printing: mode 0 returns the buffer size (public part plus the driver's private data),
    /// <see cref="DM_OUT_BUFFER"/> copies the current settings, and <see cref="DM_IN_BUFFER"/> | <see cref="DM_OUT_BUFFER"/> with the same buffer
    /// as input and output asks the driver to accept a changed paper size and returns what it really settled on (Learn, "To make changes to print
    /// settings that are local to an application"). Returns less than zero on failure.
    /// </summary>
    [DllImport("winspool.drv", EntryPoint = "DocumentPropertiesW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern int DocumentProperties(IntPtr hWnd, SafePrinterHandle hPrinter, string pDeviceName, IntPtr pDevModeOutput, IntPtr pDevModeInput, int fMode);

    /// <summary>
    /// Creates the printer device context the job draws into, from the DEVMODE the driver validated. Learn: GDI ignores the driver name for
    /// printers; "WINSPOOL" is passed as Windows itself and <see cref="Win32PrinterCapsReader"/> do. The port is ignored and must be null. Null on failure.
    /// </summary>
    [DllImport("gdi32.dll", EntryPoint = "CreateDCW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern SafeDcHandle CreateDC(string? pwszDriver, string pwszDevice, string? pszPort, IntPtr pdm);

    /// <summary>
    /// Creates an information context: the same answers as a device context from <see cref="GetDeviceCaps"/>, but it cannot draw or start a job.
    /// Used by the probe and by the paper negotiation, which must never print anything. Null on failure.
    /// </summary>
    [DllImport("gdi32.dll", EntryPoint = "CreateICW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern SafeDcHandle CreateIC(string? pszDriver, string pszDevice, string? pszPort, IntPtr pdm);

    /// <summary>
    /// Reads one device capability. The driver's real resolution (LOGPIXELSX/Y), the physical page (PHYSICALWIDTH/HEIGHT), where the printable
    /// area starts on it (PHYSICALOFFSETX/Y) and the printable area itself (HORZRES/VERTRES) decide the picture's resolution and its position.
    /// </summary>
    [DllImport("gdi32.dll")]
    internal static extern int GetDeviceCaps(SafeDcHandle hdc, int index);

    /// <summary>
    /// Sets how <see cref="StretchDIBits"/> resamples. The picture is drawn with <see cref="COLORONCOLOR"/> so a whole-factor stretch repeats
    /// pixels exactly and black bars keep hard edges. Returns the previous mode, or 0 on failure.
    /// </summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern int SetStretchBltMode(SafeDcHandle hdc, int mode);

    /// <summary>Starts the print job. Returns the job id (greater than zero), or zero or less on failure.</summary>
    [DllImport("gdi32.dll", EntryPoint = "StartDocW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern int StartDoc(SafeDcHandle hdc, ref DOCINFOW lpdi);

    /// <summary>Prepares the driver to accept one page (one label). Greater than zero on success.</summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern int StartPage(SafeDcHandle hdc);

    /// <summary>
    /// Copies the label picture onto the page at its destination rectangle. Both arrays are blittable, so the marshaller pins them for the call
    /// instead of copying: <paramref name="lpBits"/> are the DWORD-padded rows, <paramref name="lpbmi"/> the BITMAPINFO with its palette.
    /// Returns the number of scan lines copied; 0 or <see cref="GDI_ERROR"/> is a failure.
    /// </summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern int StretchDIBits(SafeDcHandle hdc, int xDest, int yDest, int destWidth, int destHeight, int xSrc, int ySrc,
        int srcWidth, int srcHeight, byte[] lpBits, byte[] lpbmi, uint iUsage, uint rop);

    /// <summary>
    /// Tells the driver the page is finished. A page counts as submitted only once this succeeds (greater than zero): from then on it may print
    /// even if the job is aborted later, so its serial number is never issued again.
    /// </summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern int EndPage(SafeDcHandle hdc);

    /// <summary>Ends a finished job so the spooler prints it. Greater than zero on success.</summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern int EndDoc(SafeDcHandle hdc);

    /// <summary>
    /// Stops a job that did not finish (failure, cancel, or an exception from the page source). Learn: with the spooler it erases the spool job;
    /// without it the data may already have reached the printer, which is why pages already ended stay counted.
    /// </summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern int AbortDoc(SafeDcHandle hdc);

    /// <summary>Deletes a device or information context; called only by <see cref="SafeDcHandle"/> (Learn: a printer DC is deleted, never released).</summary>
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteDC(IntPtr hdc);
}

/// <summary>A printer handle from <see cref="GdiNative.OpenPrinter"/>, closed with ClosePrinter when disposed (or finalized as a last resort).</summary>
internal sealed class SafePrinterHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Called by the interop marshaller.</summary>
    public SafePrinterHandle() : base(ownsHandle: true) { }

    /// <inheritdoc />
    protected override bool ReleaseHandle() => GdiNative.ClosePrinter(handle);
}

/// <summary>A device or information context from CreateDC/CreateIC, deleted with DeleteDC when disposed (or finalized as a last resort).</summary>
internal sealed class SafeDcHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Called by the interop marshaller.</summary>
    public SafeDcHandle() : base(ownsHandle: true) { }

    /// <inheritdoc />
    protected override bool ReleaseHandle() => GdiNative.DeleteDC(handle);
}
