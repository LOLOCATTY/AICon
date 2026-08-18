#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace AICon.Shared
{
    // Extracts readable text from files the user attaches to the chat. Spreadsheets are converted
    // to CSV-ish text and INLINED into the message (no model reads .xlsx natively); images and PDFs
    // are not handled here — they travel as binary attachments to vision-capable providers.
    // Zero external dependencies: .xlsx is just a zip of XML.
    public static class FileTexts
    {
        /// <summary>Text for inlining, or null when the file type is not a text-extractable kind.</summary>
        public static string? TryReadAsText(string path, int maxChars = 24000)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".csv":
                case ".txt":
                case ".md":
                case ".json":
                    return Truncate(File.ReadAllText(path), maxChars);
                case ".xlsx":
                case ".xlsm":
                    return Truncate(ReadXlsx(path), maxChars);
                default:
                    return null;
            }
        }

        private static string Truncate(string s, int max) =>
            s.Length <= max ? s : s.Substring(0, max) + "\n… (truncated — file is longer)";

        // Minimal .xlsx reader: sharedStrings + first sheets, emitted as CSV lines per sheet.
        private static string ReadXlsx(string path)
        {
            using (ZipArchive zip = ZipFile.OpenRead(path))
            {
                // Shared strings (cell values of type "s" index into this list).
                var shared = new List<string>();
                ZipArchiveEntry? ss = zip.GetEntry("xl/sharedStrings.xml");
                if (ss != null)
                    using (Stream st = ss.Open())
                    {
                        XDocument d = XDocument.Load(st);
                        foreach (XElement si in d.Root!.Elements().Where(e => e.Name.LocalName == "si"))
                            shared.Add(string.Concat(si.Descendants()
                                .Where(e => e.Name.LocalName == "t").Select(e => (string)e)));
                    }

                // Sheet display names in workbook order; paired with sheetN.xml files by order.
                var sheetNames = new List<string>();
                ZipArchiveEntry? wb = zip.GetEntry("xl/workbook.xml");
                if (wb != null)
                    using (Stream st = wb.Open())
                    {
                        XDocument d = XDocument.Load(st);
                        foreach (XElement sh in d.Descendants().Where(e => e.Name.LocalName == "sheet"))
                            sheetNames.Add((string?)sh.Attribute("name") ?? "Sheet");
                    }

                var sheetEntries = zip.Entries
                    .Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)
                             && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(e => e.FullName.Length).ThenBy(e => e.FullName, StringComparer.Ordinal)
                    .Take(3)   // first sheets only — enough for chat context
                    .ToList();

                var sb = new StringBuilder();
                for (int s = 0; s < sheetEntries.Count; s++)
                {
                    string title = s < sheetNames.Count ? sheetNames[s] : sheetEntries[s].FullName;
                    sb.Append("=== Sheet: ").Append(title).Append(" ===\n");
                    List<string[]> rows = ReadSheetRows(sheetEntries[s], shared, 300, out bool truncated);
                    foreach (string[] fields in rows)
                        sb.Append(string.Join(",", fields.Select(CsvEscape))).Append('\n');
                    if (truncated) sb.Append("… (more rows not shown)\n");
                    sb.Append('\n');
                }
                return sb.Length > 0 ? sb.ToString() : "(the workbook appears to be empty)";
            }
        }

        /// <summary>Raw grid of the first worksheet — every row as an array of cell strings, no
        /// assumption that row 1 holds the headers. Real registers usually start with a letterhead
        /// block (logos, client/consultant names) before the actual table, so the caller has to find
        /// the header row itself. Returns null for non-spreadsheet files.</summary>
        public static List<string[]>? TryReadFirstSheetGrid(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".xlsx":
                case ".xlsm":
                    break;
                default:
                    return null;
            }

            using (ZipArchive zip = ZipFile.OpenRead(path))
            {
                var shared = new List<string>();
                ZipArchiveEntry? ss = zip.GetEntry("xl/sharedStrings.xml");
                if (ss != null)
                    using (Stream st = ss.Open())
                    {
                        XDocument d = XDocument.Load(st);
                        foreach (XElement si in d.Root!.Elements().Where(e => e.Name.LocalName == "si"))
                            shared.Add(string.Concat(si.Descendants()
                                .Where(e => e.Name.LocalName == "t").Select(e => (string)e)));
                    }

                ZipArchiveEntry? firstSheet = zip.Entries
                    .Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)
                             && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(e => e.FullName.Length).ThenBy(e => e.FullName, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (firstSheet == null) return null;

                return ReadSheetRows(firstSheet, shared, int.MaxValue, out _);
            }
        }

        /// <summary>Reads the first worksheet as header-keyed rows (row 1 = column names, trimmed).
        /// Returns null for non-.xlsx/.xlsm files or a workbook with fewer than 2 rows.</summary>
        public static List<Dictionary<string, string>>? TryReadFirstSheetAsTable(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".xlsx":
                case ".xlsm":
                    break;
                default:
                    return null;
            }

            using (ZipArchive zip = ZipFile.OpenRead(path))
            {
                var shared = new List<string>();
                ZipArchiveEntry? ss = zip.GetEntry("xl/sharedStrings.xml");
                if (ss != null)
                    using (Stream st = ss.Open())
                    {
                        XDocument d = XDocument.Load(st);
                        foreach (XElement si in d.Root!.Elements().Where(e => e.Name.LocalName == "si"))
                            shared.Add(string.Concat(si.Descendants()
                                .Where(e => e.Name.LocalName == "t").Select(e => (string)e)));
                    }

                ZipArchiveEntry? firstSheet = zip.Entries
                    .Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)
                             && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(e => e.FullName.Length).ThenBy(e => e.FullName, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (firstSheet == null) return null;

                List<string[]> rows = ReadSheetRows(firstSheet, shared, int.MaxValue, out _);
                if (rows.Count < 2) return null;

                string[] headers = rows[0].Select(h => h.Trim()).ToArray();
                var table = new List<Dictionary<string, string>>();
                for (int r = 1; r < rows.Count; r++)
                {
                    string[] fields = rows[r];
                    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (int c = 0; c < headers.Length && c < fields.Length; c++)
                        if (headers[c].Length > 0) dict[headers[c]] = fields[c];
                    if (dict.Values.Any(v => v.Trim().Length > 0)) table.Add(dict);
                }
                return table;
            }
        }

        // Shared low-level reader: one worksheet XML entry -> rows of cell text, in column order.
        // maxRows caps how many <row> XML elements are considered (matches the original chat-inlining
        // truncation semantics exactly); truncated is true when the cap was hit before the sheet ended.
        private static List<string[]> ReadSheetRows(ZipArchiveEntry sheetEntry, List<string> shared, int maxRows, out bool truncated)
        {
            var result = new List<string[]>();
            truncated = false;
            using (Stream st = sheetEntry.Open())
            {
                XDocument d = XDocument.Load(st);
                int rowCount = 0;
                foreach (XElement row in d.Descendants().Where(e => e.Name.LocalName == "row"))
                {
                    if (++rowCount > maxRows) { truncated = true; break; }
                    var cells = new SortedDictionary<int, string>();
                    foreach (XElement c in row.Elements().Where(e => e.Name.LocalName == "c"))
                        cells[ColumnIndex((string?)c.Attribute("r"))] = CellText(c, shared);
                    if (cells.Count == 0) continue;
                    int maxCol = cells.Keys.Max();
                    var fields = new string[maxCol + 1];
                    for (int i = 0; i <= maxCol; i++)
                        fields[i] = cells.TryGetValue(i, out string? v) ? v : "";
                    result.Add(fields);
                }
            }
            return result;
        }

        private static string CellText(XElement c, List<string> shared)
        {
            string type = (string?)c.Attribute("t") ?? "";
            XElement? v = c.Elements().FirstOrDefault(e => e.Name.LocalName == "v");
            switch (type)
            {
                case "s":
                    return v != null && int.TryParse(v.Value, out int idx) && idx >= 0 && idx < shared.Count
                        ? shared[idx] : "";
                case "inlineStr":
                    return string.Concat(c.Descendants().Where(e => e.Name.LocalName == "t").Select(e => (string)e));
                case "b":
                    return v?.Value == "1" ? "TRUE" : "FALSE";
                default:
                    return v?.Value ?? "";
            }
        }

        // "BC12" -> zero-based column 54; unknown/absent refs land in column 0.
        private static int ColumnIndex(string? cellRef)
        {
            if (string.IsNullOrEmpty(cellRef)) return 0;
            int col = 0;
            foreach (char ch in cellRef!)
            {
                if (ch >= 'A' && ch <= 'Z') col = col * 26 + (ch - 'A' + 1);
                else if (ch >= 'a' && ch <= 'z') col = col * 26 + (ch - 'a' + 1);
                else break;
            }
            return Math.Max(0, col - 1);
        }

        private static string CsvEscape(string s) =>
            s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
