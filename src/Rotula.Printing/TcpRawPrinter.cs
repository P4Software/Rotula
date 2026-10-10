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

        int sent = 0;
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
                // Chunked so BytesSent reports real progress when the printer stops accepting data.
                const int chunk = 16 * 1024;
                while (sent < data.Length)
                {
                    int n = Math.Min(chunk, data.Length - sent);
                    await stream.WriteAsync(data.AsMemory(sent, n), sendCts.Token).ConfigureAwait(false);
                    sent += n;
                }
                await stream.FlushAsync(sendCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return PrintResult.Fail(PrintError.Create(PrintErrorKind.Timeout, host, port.ToString()), sent);
            }

            // Half-close so the printer sees a clean end of stream before the socket is torn down;
            // an abrupt close can make some stacks discard unsent data.
            try { client.Client.Shutdown(SocketShutdown.Send); } catch (SocketException) { }
            return PrintResult.Ok(sent);
        }
        catch (OperationCanceledException)
        {
            return cancellationToken.IsCancellationRequested
                ? PrintResult.Fail(PrintError.Create(PrintErrorKind.Cancelled), sent)
                : PrintResult.Fail(PrintError.Create(PrintErrorKind.Timeout, host, port.ToString()), sent);
        }
        catch (SocketException ex)
        {
            return PrintResult.Fail(Map(ex, host, port), sent);
        }
        catch (IOException ex) when (ex.InnerException is SocketException se)
        {
            return PrintResult.Fail(Map(se, host, port), sent);
        }
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
