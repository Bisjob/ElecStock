using System.Text;

namespace ElecStock.Csv;

/// <summary>
/// Lenient CSV, as written by hand in the stock files: a double quote is only special at the
/// start of a field, so free text such as <c>R82 "µ1J63"</c> is kept as-is.
/// </summary>
public static class CsvText
{
    /// <summary>Rows of a CSV text, header included; blank lines are skipped.</summary>
    public static List<List<string>> Parse(string text)
    {
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        return text.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(ParseLine)
            .ToList();
    }

    /// <summary>Data rows as dictionaries keyed by the (trimmed, case-insensitive) header names.</summary>
    public static List<Dictionary<string, string>> ParseRecords(string text)
    {
        var rows = Parse(text);
        if (rows.Count == 0) return [];
        var header = rows[0].Select(h => h.Trim()).ToList();
        var records = new List<Dictionary<string, string>>();
        foreach (var row in rows.Skip(1))
        {
            var record = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < header.Count; i++)
                record[header[i]] = i < row.Count ? row[i].Trim() : "";
            records.Add(record);
        }
        return records;
    }

    public static List<string> ParseLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        int i = 0;
        while (true)
        {
            sb.Clear();
            if (i < line.Length && line[i] == '"')
            {
                i++;
                while (i < line.Length)
                {
                    if (line[i] == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i += 2; }
                        else { i++; break; }
                    }
                    else sb.Append(line[i++]);
                }
                while (i < line.Length && line[i] != ',') sb.Append(line[i++]);
            }
            else
            {
                while (i < line.Length && line[i] != ',') sb.Append(line[i++]);
            }
            fields.Add(sb.ToString());
            if (i >= line.Length) break;
            i++; // skip the comma
        }
        return fields;
    }

    public static string Escape(string? field)
    {
        field ??= "";
        return field.Contains(',') || field.Contains('\n') || field.Contains('\r') || field.StartsWith('"')
            ? "\"" + field.Replace("\"", "\"\"") + "\""
            : field;
    }

    /// <summary>CSV text with a header line, LF line endings and a trailing newline.</summary>
    public static string Write(IEnumerable<string> header, IEnumerable<IEnumerable<string?>> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(",", header.Select(Escape))).Append('\n');
        foreach (var row in rows)
            sb.Append(string.Join(",", row.Select(Escape))).Append('\n');
        return sb.ToString();
    }
}
