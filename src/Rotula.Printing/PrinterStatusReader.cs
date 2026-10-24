using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace Rotula.Printing;

/// <summary>
/// Sends <c>~HS</c> and reads the reply: over TCP on the printer's raw port, or best effort through a Windows queue with the
/// spooler's bidirectional <c>ReadPrinter</c>. Every failure is an unavailable status; nothing here throws for a printer problem and no
/// call waits longer than its timeout.
/// </summary>
public sealed class PrinterStatusReader : IPrinterStatusReader
{
    private static readonly byte[] Query = Encoding.ASCII.GetBytes(HostStatusParser.Command);

    /// <inheritdoc />
    public Task<PrinterStatus> ReadStatusAsync(PrinterTarget target, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        return target.Transport switch
        {
            PrinterTransport.Tcp => ReadTcpAsync(target.Host, target.Port, timeout, cancellationToken),
            PrinterTransport.Spooler => ReadSpoolerAsync(target.PrinterName, timeout, cancellationToken),
            _ => Task.FromResult(PrinterStatus.Unavailable(StatusUnavailableReason.NotSupported)),
        };
    }

    /// <summary>Connects, writes <c>~HS</c> and reads until the three strings arrived, the printer closes, or the timeout.</summary>
    internal static async Task<PrinterStatus> ReadTcpAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535) return PrinterStatus.Unavailable(StatusUnavailableReason.Unreachable);
        // One budget for connect + write + read; a linked source so a timeout can be told apart from the caller's cancel.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var client = new TcpClient { NoDelay = true };
        var received = new MemoryStream();
        bool connected = false;
        try
        {
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            connected = true;
            var stream = client.GetStream();
            await stream.WriteAsync(Query, cts.Token).ConfigureAwait(false);
            await stream.FlushAsync(cts.Token).ConfigureAwait(false);
            var buffer = new byte[512];
            while (HostStatusParser.CompleteFrames(received.GetBuffer().AsSpan(0, (int)received.Length)) < 3 && received.Length < 16 * 1024)
            {
                int n = await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
                if (n == 0) break; // the printer closed the connection
                received.Write(buffer, 0, n);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timed out while connecting: nobody answered at that address. Windows retries a refused connection for
            // about two seconds before failing, so a closed port often ends here rather than in a SocketException.
            if (!connected) return PrinterStatus.Unavailable(StatusUnavailableReason.Unreachable);
            // Timed out after connecting: a reply that already holds strings 1 and 2 is still worth decoding.
            return Decode(received, StatusUnavailableReason.NoReply);
        }
        catch (SocketException)
        {
            return received.Length > 0 ? Decode(received, StatusUnavailableReason.Unreachable) : PrinterStatus.Unavailable(StatusUnavailableReason.Unreachable);
        }
        catch (IOException)
        {
            return Decode(received, StatusUnavailableReason.Unreachable);
        }
        return Decode(received, StatusUnavailableReason.NoReply);
    }

    private static PrinterStatus Decode(MemoryStream received, StatusUnavailableReason whenEmpty) =>
        received.Length == 0 ? PrinterStatus.Unavailable(whenEmpty) : HostStatusParser.Parse(received.ToArray());

    /// <summary>
    /// The spooler path: a tiny RAW job with <c>~HS</c>, then <c>ReadPrinter</c> on the same handle. Many USB and network port monitors
    /// do not support reading, and a read can block inside the driver, so the work runs on its own thread and the caller stops waiting at
    /// the timeout; a read that returns later just closes its handle.
    /// </summary>
    private static async Task<PrinterStatus> ReadSpoolerAsync(string printerName, TimeSpan timeout, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return PrinterStatus.Unavailable(StatusUnavailableReason.NotSupported);
        if (string.IsNullOrWhiteSpace(printerName)) return PrinterStatus.Unavailable(StatusUnavailableReason.Unreachable);
        var deadline = DateTime.UtcNow + timeout;
        var work = Task.Factory.StartNew(() => SpoolerQuery(printerName, deadline), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            return await work.WaitAsync(timeout + TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return PrinterStatus.Unavailable(StatusUnavailableReason.NoReply);
        }
    }

    private static PrinterStatus SpoolerQuery(string printerName, DateTime deadline)
    {
        try
        {
            if (!NativeMethods.OpenPrinter(printerName, out var handle, IntPtr.Zero))
                return PrinterStatus.Unavailable(StatusUnavailableReason.Unreachable);
            try
            {
                // Same reason as printing: a job for an unplugged printer would sit in the queue for ever.
                if (WinspoolRawPrinter.IsQueueOffline(handle)) return PrinterStatus.Unavailable(StatusUnavailableReason.Offline);
                var doc = new NativeMethods.DOC_INFO_1 { pDocName = "Rotula status", pOutputFile = null, pDatatype = "RAW" };
                if (NativeMethods.StartDocPrinter(handle, 1, ref doc) == 0) return PrinterStatus.Unavailable(StatusUnavailableReason.Unreachable);
                bool written;
                try
                {
                    var pinned = GCHandle.Alloc(Query, GCHandleType.Pinned);
                    try { written = NativeMethods.WritePrinter(handle, pinned.AddrOfPinnedObject(), Query.Length, out int done) && done == Query.Length; }
                    finally { pinned.Free(); }
                }
                finally { NativeMethods.EndDocPrinter(handle); }
                if (!written) return PrinterStatus.Unavailable(StatusUnavailableReason.Unreachable);

                var received = new MemoryStream();
                var buffer = new byte[512];
                var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    while (DateTime.UtcNow < deadline && HostStatusParser.CompleteFrames(received.GetBuffer().AsSpan(0, (int)received.Length)) < 3)
                    {
                        if (!NativeMethods.ReadPrinter(handle, pin.AddrOfPinnedObject(), buffer.Length, out int read))
                        {
                            // ERROR_NOT_SUPPORTED and friends: the port monitor cannot read back. Whatever arrived is still decoded.
                            return received.Length == 0 ? PrinterStatus.Unavailable(StatusUnavailableReason.NotSupported) : HostStatusParser.Parse(received.ToArray());
                        }
                        if (read > 0) received.Write(buffer, 0, read);
                        else Thread.Sleep(100); // nothing yet; the reply may still be on its way
                    }
                }
                finally { pin.Free(); }
                return received.Length == 0 ? PrinterStatus.Unavailable(StatusUnavailableReason.NoReply) : HostStatusParser.Parse(received.ToArray());
            }
            finally { NativeMethods.ClosePrinter(handle); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            return PrinterStatus.Unavailable(StatusUnavailableReason.NotSupported);
        }
    }
}
