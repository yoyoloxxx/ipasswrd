using System.Text;

namespace IPasswrd.Core.Import;

/// <summary>Minimal RFC 4180 CSV reader: quoted fields, escaped quotes (""), commas and
/// newlines inside quotes, CRLF or LF line endings, optional UTF-8 BOM. Good enough for
/// browser / password-manager exports where passwords may contain commas and quotes.</summary>
public static class Csv
{
    /// <summary>
    /// Обратная операция к <see cref="Parse"/>, по тем же правилам RFC 4180: поле берётся
    /// в кавычки, если в нём есть запятая, кавычка или перевод строки — в паролях всё это
    /// встречается регулярно. Конец строки — CRLF: так пишут браузеры, и так ждёт Excel.
    /// </summary>
    public static string Write(IEnumerable<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder();
        foreach (IReadOnlyList<string> row in rows)
        {
            for (int i = 0; i < row.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Field(row[i]));
            }
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    private static string Field(string? value)
    {
        string s = value ?? "";
        if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
        return '"' + s.Replace("\"", "\"\"") + '"';
    }

    public static List<List<string>> Parse(string text)
    {
        var rows = new List<List<string>>();
        var field = new StringBuilder();
        var row = new List<string>();
        bool inQuotes = false;
        bool rowHasData = false;
        int i = 0, n = text.Length;
        if (n > 0 && text[0] == '﻿') i = 1;   // strip UTF-8 BOM

        void EndField() { row.Add(field.ToString()); field.Clear(); }
        void EndRow()
        {
            EndField();
            if (rowHasData || row.Count > 1) rows.Add(row);
            row = new List<string>();
            rowHasData = false;
        }

        while (i < n)
        {
            char c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < n && text[i + 1] == '"') { field.Append('"'); i += 2; continue; }
                    inQuotes = false; i++; continue;
                }
                field.Append(c); i++; continue;
            }
            switch (c)
            {
                case '"': inQuotes = true; rowHasData = true; i++; break;
                case ',': EndField(); rowHasData = true; i++; break;
                case '\r': i++; break;
                case '\n': EndRow(); i++; break;
                default: field.Append(c); rowHasData = true; i++; break;
            }
        }
        EndRow();   // flush trailing field/row
        return rows;
    }
}
