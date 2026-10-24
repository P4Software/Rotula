using System.Security.Cryptography;
using System.Text;
using Rotula.Mcp.Settings;

namespace Rotula.Mcp.Security;

/// <summary>What the gate decided about one print request.</summary>
public enum GateDecision
{
    /// <summary>The request breaks a rule; nothing may be sent.</summary>
    Refused,
    /// <summary>The request is allowed in principle; show the job and hand out a confirmation token. Nothing may be sent yet.</summary>
    DryRun,
    /// <summary>A valid token for exactly this request was presented; one job may be sent now.</summary>
    Confirmed,
}

/// <summary>The gate's answer.</summary>
/// <param name="Decision">Refused, dry run or confirmed.</param>
/// <param name="Message">Plain-language explanation (always set for a refusal).</param>
/// <param name="Token">The one-time confirmation token of a dry run.</param>
/// <param name="ExpiresAt">When the token stops working.</param>
public sealed record GateResult(GateDecision Decision, string? Message, string? Token = null, DateTimeOffset? ExpiresAt = null);

/// <summary>
/// The state machine in front of printing. Printing needs the setting <c>allowPrinting</c>, the printer in
/// <c>allowedPrinters</c>, and two calls: the first never prints, it returns the job details and a random one-time token that
/// is bound to a fingerprint of the exact request (printer, copies and the final ZPL); the second must present that token
/// before it expires. A token is consumed the moment it is presented, whatever the outcome, so it cannot be replayed, and only
/// the most recent token is valid.
/// </summary>
public sealed class PrintGate
{
    private readonly McpSettings _settings;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();

    private string? _token;
    private string? _fingerprint;
    private DateTimeOffset _expires;

    /// <summary>Creates the gate.</summary>
    /// <param name="settings">The allow-list and limits.</param>
    /// <param name="clock">The time source (injected so tests can make a token expire without waiting).</param>
    public PrintGate(McpSettings settings, Func<DateTimeOffset>? clock = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Checks the rules that do not depend on the label: printing enabled, printer allowed, copies in range.</summary>
    /// <returns>A refusal, or null when the request may proceed.</returns>
    public GateResult? CheckPolicy(string? printer, int copies)
    {
        if (_settings.PrintingBlockedByPolicy)
            return Refuse("Printing is switched off by your organization's policy for this computer. Ask your IT department. Nothing was sent.");
        if (!_settings.AllowPrinting)
            return Refuse("Printing is switched off. The person who installed the server can allow it by setting allowPrinting to true " +
                          "and listing the printer in allowedPrinters in rotula-mcp.settings.json. Nothing was sent.");
        if (string.IsNullOrWhiteSpace(printer))
            return Refuse("A printer name is required. Use list_printers to see the installed printers. Nothing was sent.");
        if (!_settings.AllowedPrinters.Contains(printer.Trim(), StringComparer.OrdinalIgnoreCase))
            return Refuse($"The printer \"{printer}\" is not on the allowed list (allowedPrinters in rotula-mcp.settings.json). Nothing was sent.");
        if (copies < 1)
            return Refuse("Copies must be at least 1. Nothing was sent.");
        if (copies > _settings.MaxCopies)
            return Refuse($"At most {_settings.MaxCopies} copies per request are allowed (maxCopies in rotula-mcp.settings.json). Nothing was sent.");
        return null;
    }

    /// <summary>
    /// Evaluates one request. Without a token it issues a new one (a dry run); with a token it either confirms or refuses.
    /// </summary>
    /// <param name="printer">The Windows printer name.</param>
    /// <param name="copies">Number of labels.</param>
    /// <param name="fingerprint">A hash of everything the job consists of; see <see cref="Fingerprint"/>.</param>
    /// <param name="token">The token from an earlier dry run, or null.</param>
    public GateResult Evaluate(string? printer, int copies, string fingerprint, string? token)
    {
        var policy = CheckPolicy(printer, copies);
        if (policy is not null)
        {
            Forget();
            return policy;
        }

        lock (_gate)
        {
            var now = _clock();
            if (string.IsNullOrWhiteSpace(token))
            {
                _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
                _fingerprint = fingerprint;
                _expires = now.AddSeconds(_settings.ConfirmationSeconds);
                return new GateResult(GateDecision.DryRun, null, _token, _expires);
            }

            // Single use: whatever happens below, this token is gone.
            string? expectedToken = _token, expectedFingerprint = _fingerprint;
            DateTimeOffset expires = _expires;
            _token = null;
            _fingerprint = null;

            if (expectedToken is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expectedToken), Encoding.ASCII.GetBytes(token.Trim().ToLowerInvariant())))
                return Refuse("That confirmation code is not valid (unknown, already used, or replaced by a newer one). Nothing was sent. " +
                              "Call print_label again without a code to get a new preview.");
            if (now > expires)
                return Refuse("That confirmation code has expired. Nothing was sent. Call print_label again without a code to get a new preview.");
            if (!string.Equals(expectedFingerprint, fingerprint, StringComparison.Ordinal))
                return Refuse("The label, printer or copies changed since the preview, so the confirmation code no longer applies. Nothing was sent. " +
                              "Call print_label again without a code to get a new preview.");
            return new GateResult(GateDecision.Confirmed, null);
        }
    }

    /// <summary>Drops any outstanding token (called when a request is refused or the label changed).</summary>
    public void Forget()
    {
        lock (_gate)
        {
            _token = null;
            _fingerprint = null;
        }
    }

    /// <summary>Builds the fingerprint that binds a token to one exact job.</summary>
    public static string Fingerprint(string printer, int copies, string zpl)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(printer.Trim().ToLowerInvariant() + "\0" + copies + "\0" + zpl);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static GateResult Refuse(string message) => new(GateDecision.Refused, message);
}
