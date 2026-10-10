using System.Text;
using Rotula.Core.Validation;

namespace Rotula.Core.Merge;

/// <summary>Options for <see cref="CsvReader"/>.</summary>
public sealed class CsvOptions
{
    /// <summary>Field delimiter, or null to auto-detect among comma, semicolon and tab.</summary>
    public char? Delimiter { get; set; }

    /// <summary>True when the first record holds column names; false generates <c>Column1</c>, <c>Column2</c>...</summary>
    public bool HasHeader { get; set; } = true;
}

/// <summary>
/// Dependency-free RFC 4180 CSV reader: quoted fields, doubled quotes, embedded delimiters and line breaks
/// (CRLF, LF or CR), optional UTF-8 BOM. Completely blank lines are skipped. Lenient by design: a malformed
/// file still yields a table plus an issue instead of an exception.
/// </summary>
public static class CsvReader
{
    private static readonly char[] Candidates = { ',', ';', '\t' };

    /// <summary>Parses CSV text.</summary>
    public static MergeTable Parse(string text, CsvOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Read(new StringReader(text), options);
    }

    /// <summary>Reads a CSV file (UTF-8; a BOM is honoured, UTF-16 BOMs are detected too).</summary>
    public static MergeTable ReadFile(string path, CsvOptions? options = null)
    {
        using var reader = new StreamReader(path, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return Read(reader, options);
    }

    /// <summary>Reads CSV from a stream (UTF-8, BOM honoured). The stream is left open.</summary>
    public static MergeTable Read(Stream stream, CsvOptions? options = null)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
        return Read(reader, options);
    }

    /// <summary>Reads CSV from a text reader.</summary>
    public static MergeTable Read(TextReader reader, CsvOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        options ??= new CsvOptions();
        string text = reader.ReadToEnd();
        // A BOM that survived string decoding would otherwise glue itself to the first header name.
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];

        char delimiter = options.Delimiter ?? DetectDelimiter(text);
        var records = Tokenize(text, delimiter, out bool unterminated);

        int width = records.Count == 0 ? 0 : records.Max(r => r.Count);
        List<string> header;
        int start = 0;
        if (options.HasHeader && records.Count > 0)
        {
            header = new List<string>(records[0]);
            start = 1;
        }
        else
        {
            header = new List<string>();
        }
        // Header shorter than the widest row: pad with blanks (named ColumnN) so no data is lost.
        while (header.Count < width) header.Add(string.Empty);

        var table = new MergeTable(header);
        for (int i = start; i < records.Count; i++) table.AddRow(records[i]);
        if (unterminated) table.AddIssue(new ValidationIssue(ValidationSeverity.Warning, "Merge.Csv.UnterminatedQuote"));
        return table;
    }

    /// <summary>
    /// Picks the delimiter that occurs most often outside quotes on the first non-empty line;
    /// ties and lines without any candidate fall back to comma (priority order: comma, semicolon, tab).
    /// </summary>
    public static char DetectDelimiter(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var counts = new int[Candidates.Length];
        bool inQuotes = false;
        bool sawContent = false;
        foreach (char c in text)
        {
            if (c == '﻿') continue;
            if (c == '"') { inQuotes = !inQuotes; sawContent = true; continue; }
            if (!inQuotes && (c == '\n' || c == '\r'))
            {
                if (sawContent) break;
                continue;
            }
            sawContent = true;
            if (inQuotes) continue;
            int idx = Array.IndexOf(Candidates, c);
            if (idx >= 0) counts[idx]++;
        }
        int best = 0;
        for (int i = 1; i < counts.Length; i++) if (counts[i] > counts[best]) best = i;
        return Candidates[best];
    }

    private static List<List<string>> Tokenize(string s, char delim, out bool unterminated)
    {
        var records = new List<List<string>>();
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false, fieldWasQuoted = false, any = false;
        bool openQuote = false;

        void EndField() { fields.Add(sb.ToString()); sb.Clear(); fieldWasQuoted = false; }
        void EndRecord()
        {
            EndField();
            // A record that is one empty unquoted field is a blank line; ignore it.
            bool blank = fields.Count == 1 && fields[0].Length == 0 && !any;
            if (!blank) records.Add(fields);
            fields = new List<string>();
            any = false;
        }

        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < s.Length && s[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
                continue;
            }
            if (c == '"' && sb.Length == 0 && !fieldWasQuoted) { inQuotes = true; fieldWasQuoted = true; any = true; }
            else if (c == delim) { any = true; EndField(); }
            else if (c == '\r') { if (i + 1 < s.Length && s[i + 1] == '\n') i++; EndRecord(); }
            else if (c == '\n') EndRecord();
            else { sb.Append(c); any = true; }
        }
        openQuote = inQuotes;
        if (sb.Length > 0 || fields.Count > 0 || fieldWasQuoted || any) EndRecord();
        unterminated = openQuote;
        return records;
    }
}
