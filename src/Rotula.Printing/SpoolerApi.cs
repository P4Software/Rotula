using System.Runtime.InteropServices;

namespace Rotula.Printing;

/// <summary>
/// The few winspool calls a RAW print job needs, as a seam: the real implementation is <see cref="Win32SpoolerApi"/>, tests pass a fake
/// that records the order of the calls. Kept deliberately small and internal so it does not become public API.
/// </summary>
internal interface ISpoolerApi
{
    /// <summary>Opens the queue. False when it cannot be opened (read <see cref="LastError"/>).</summary>
    bool Open(string printerName, out IntPtr handle);

    /// <summary>True when Windows reports the open queue as offline; false when it is online or the state cannot be read.</summary>
    bool IsOffline(IntPtr handle);

    /// <summary>Starts a RAW document. False on failure.</summary>
    bool StartDoc(IntPtr handle, string documentName);

    /// <summary>Starts the page of the document. False on failure.</summary>
    bool StartPage(IntPtr handle);

    /// <summary>Writes <paramref name="count"/> bytes of <paramref name="data"/> from <paramref name="offset"/>; <paramref name="written"/> is how many were taken.</summary>
    bool Write(IntPtr handle, byte[] data, int offset, int count, out int written);

    /// <summary>Ends the page.</summary>
    bool EndPage(IntPtr handle);

    /// <summary>Ends the document, which commits it to the printer.</summary>
    bool EndDoc(IntPtr handle);

    /// <summary>Discards the document (and its spool file) instead of committing it.</summary>
    bool Abort(IntPtr handle);

    /// <summary>Closes the queue handle.</summary>
    void Close(IntPtr handle);

    /// <summary>The Win32 error code of the last failed call on this thread. Read it right after the failure, before another call.</summary>
    int LastError();
}

/// <summary>The real winspool.drv implementation of <see cref="ISpoolerApi"/>.</summary>
internal sealed class Win32SpoolerApi : ISpoolerApi
{
    /// <summary>The shared stateless instance.</summary>
    internal static readonly Win32SpoolerApi Instance = new();

    private Win32SpoolerApi() { }

    public bool Open(string printerName, out IntPtr handle) => NativeMethods.OpenPrinter(printerName, out handle, IntPtr.Zero);

    public bool IsOffline(IntPtr handle) => WinspoolRawPrinter.IsQueueOffline(handle);

    public bool StartDoc(IntPtr handle, string documentName)
    {
        var doc = new NativeMethods.DOC_INFO_1 { pDocName = documentName, pOutputFile = null, pDatatype = "RAW" };
        return NativeMethods.StartDocPrinter(handle, 1, ref doc) != 0;
    }

    public bool StartPage(IntPtr handle) => NativeMethods.StartPagePrinter(handle);

    public bool Write(IntPtr handle, byte[] data, int offset, int count, out int written)
    {
        // Pinned only for the duration of one write, so a long job does not hold the array against the garbage collector.
        var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        try { return NativeMethods.WritePrinter(handle, Marshal.UnsafeAddrOfPinnedArrayElement(data, offset), count, out written); }
        finally { pin.Free(); }
    }

    public bool EndPage(IntPtr handle) => NativeMethods.EndPagePrinter(handle);

    public bool EndDoc(IntPtr handle) => NativeMethods.EndDocPrinter(handle);

    public bool Abort(IntPtr handle) => NativeMethods.AbortPrinter(handle);

    public void Close(IntPtr handle) => NativeMethods.ClosePrinter(handle);

    public int LastError() => Marshal.GetLastWin32Error();
}
