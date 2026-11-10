using System.Net.Sockets;

namespace Rotula.Printing;

/// <summary>Direct TCP/IP printing (port 9100 by default) with connect and send timeouts and cancellation.</summary>
public sealed class TcpRawPrinter : INetworkPrinter
{
    /// <inheritdoc />
    public async Task<PrintResult> SendAsync(string host, int port, byte[] data, NetworkPrinterOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535)
            return PrintResult.Fail(PrintErrorKind.HostUnreachable, host ?? string.Empty, port.ToString());

        // Bytes that were handed to the socket or may have been: the chunk being written counts too. A cancel or a timeout in the
        // middle of a chunk leaves part of it in the kernel buffer, and a half-sent label can still print.
        var reached = new System.Runtime.CompilerServices.StrongBox<int>(0);
        // Why two linked sources: a timeout and a caller cancel both surface as OperationCanceledException,
        // and the result must tell them apart (timeout vs. cancelled by the user).
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(options.ConnectTimeout);
        using var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(host, port, connectCts.Token).ConfigureAwait(false);

            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            sendCts.CancelAfter(options.SendTimeout);
            var stream = client.GetStream();
            try
            {
                await WriteAllAsync(stream, data, Chunk, reached, sendCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return PrintResult.Fail(PrintError.Create(PrintErrorKind.Timeout, host, port.ToString()), reached.Value);
            }

            // Half-close so the printer sees a clean end of stream before the socket is torn down;
            // an abrupt close can make some stacks discard unsent data.
            try { client.Client.Shutdown(SocketShutdown.Send); } catch (SocketException) { }
            return PrintResult.Ok(data.Length);
        }
        catch (OperationCanceledException)
        {
            return cancellationToken.IsCancellationRequested
                ? PrintResult.Fail(PrintError.Create(PrintErrorKind.Cancelled), reached.Value)
                : PrintResult.Fail(PrintError.Create(PrintErrorKind.Timeout, host, port.ToString()), reached.Value);
        }
        catch (SocketException ex)
        {
            return PrintResult.Fail(Map(ex, host, port), reached.Value);
        }
        catch (IOException ex) when (ex.InnerException is SocketException se)
        {
            return PrintResult.Fail(Map(se, host, port), reached.Value);
        }
    }

    private const int Chunk = 16 * 1024;

    /// <summary>
    /// Writes <paramref name="data"/> in chunks so the byte count reflects real progress when the printer stops accepting data.
    /// <paramref name="reached"/> is updated BEFORE each write with the number of bytes handed over so far including the chunk being
    /// written: when the write is cut short, that chunk may be partly in the socket, so the caller must treat those bytes as sent
    /// (counting only finished chunks would report 0 for a cancel during the first chunk and let serial numbers be handed out twice).
    /// </summary>
    internal static async Task WriteAllAsync(Stream stream, byte[] data, int chunkSize, System.Runtime.CompilerServices.StrongBox<int> reached, CancellationToken ct)
    {
        int sent = 0;
        while (sent < data.Length)
        {
            // Checked between chunks only: nothing is in flight here, so the count stays exact when the cancel arrives at this point.
            ct.ThrowIfCancellationRequested();
            int n = Math.Min(chunkSize, data.Length - sent);
            reached.Value = sent + n;
            await stream.WriteAsync(data.AsMemory(sent, n), ct).ConfigureAwait(false);
            sent += n;
        }
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    internal static PrintError Map(SocketException ex, string host, int port) => ex.SocketErrorCode switch
    {
        SocketError.ConnectionRefused => PrintError.Create(PrintErrorKind.ConnectionRefused, host, port.ToString()),
        SocketError.TimedOut => PrintError.Create(PrintErrorKind.Timeout, host, port.ToString()),
        SocketError.HostNotFound or SocketError.HostUnreachable or SocketError.NetworkUnreachable
            or SocketError.HostDown or SocketError.NoData or SocketError.TryAgain
            => PrintError.Create(PrintErrorKind.HostUnreachable, host, port.ToString()),
        _ => PrintError.Create(PrintErrorKind.Unknown, host, port.ToString(), ex.SocketErrorCode.ToString()),
    };
}
