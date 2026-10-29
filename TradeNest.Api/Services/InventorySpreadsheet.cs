using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace TradeNest.Api.Services;

// Read a bounded XLSX worksheet without evaluating formulas, macros, external
// links, or XML entities. The API's downloadable templates use this exact shape.
public static class InventorySpreadsheet
{
    public const int MaxFileBytes = 2 * 1024 * 1024;
    public static IReadOnlyList<Dictionary<string, string>> Read(byte[] bytes)
    {
        if (bytes.Length is 0 or > MaxFileBytes) throw new BusinessRequestException(413, "Upload an XLSX file no larger than 2 MB");
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            if (zip.Entries.Count > 100 || zip.Entries.Sum(e => e.Length) > 8 * 1024 * 1024 || zip.Entries.Any(e => e.FullName.Contains("vbaProject", StringComparison.OrdinalIgnoreCase)))
                throw new BusinessRequestException(400, "Workbook is too large or contains unsupported macros");
            XDocument Load(string path)
            {
                var entry = zip.GetEntry(path) ?? throw new BusinessRequestException(400, "Invalid workbook structure");
                using var stream = entry.Open();
                using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
                return XDocument.Load(reader);
            }
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            XNamespace rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            var sheets = Load("xl/workbook.xml").Descendants(ns + "sheet").ToArray();
            if (sheets.Length != 1) throw new BusinessRequestException(400, "Use one worksheet per import");
            var relation = Load("xl/_rels/workbook.xml.rels").Root!.Elements().SingleOrDefault(e => (string?)e.Attribute("Id") == (string?)sheets[0].Attribute(rel + "id"));
            var target = (string?)relation?.Attribute("Target") ?? "";
            var path = target.StartsWith('/') ? target[1..] : "xl/" + target;
            if ((string?)relation?.Attribute("TargetMode") == "External" || !path.StartsWith("xl/worksheets/", StringComparison.Ordinal) || path.Contains("..") || path.Contains('\\'))
                throw new BusinessRequestException(400, "External or unsafe worksheet references are unsupported");
            var shared = zip.GetEntry("xl/sharedStrings.xml") is null ? [] : Load("xl/sharedStrings.xml").Descendants(ns + "si").Select(e => string.Concat(e.Descendants(ns + "t").Select(t => t.Value))).ToArray();
            var xml = Load(path);
            if (xml.Descendants(ns + "f").Any()) throw new BusinessRequestException(400, "Replace spreadsheet formulas with their values before importing");
            var rows = xml.Descendants(ns + "row").Where(r => r.Elements(ns + "c").Any(c => c.Descendants(ns + "v").Any() || c.Descendants(ns + "t").Any())).ToList();
            if (rows.Count is < 2 or > 1001) throw new BusinessRequestException(400, "Provide one header row and 1-1000 data rows");
            List<string> Cells(XElement row)
            {
                var values = new List<string>();
                foreach (var c in row.Elements(ns + "c"))
                {
                    var reference = (string?)c.Attribute("r"); var column = values.Count;
                    if (reference is not null)
                    {
                        column = 0;
                        foreach (var ch in reference.TakeWhile(char.IsLetter))
                        { if (ch is < 'A' or > 'Z') throw new BusinessRequestException(400, "Invalid cell reference"); column = checked(column * 26 + ch - 'A' + 1); }
                        column--;
                    }
                    if (column < values.Count || column >= 30) throw new BusinessRequestException(400, "Invalid or excessive worksheet columns");
                    while (values.Count < column) values.Add("");
                    var type = (string?)c.Attribute("t"); var text = c.Element(ns + "v")?.Value ?? "";
                    if (type == "s")
                    { if (!int.TryParse(text, out var index) || index < 0 || index >= shared.Length) throw new BusinessRequestException(400, "Invalid shared-string reference"); text = shared[index]; }
                    else if (type == "inlineStr") text = string.Concat(c.Descendants(ns + "t").Select(t => t.Value));
                    else if (type == "b") text = text == "1" ? "true" : "false";
                    else if (type == "e") throw new BusinessRequestException(400, "Resolve spreadsheet error cells before importing");
                    if (text.Length > 500) throw new BusinessRequestException(400, "Cell values cannot exceed 500 characters");
                    values.Add(text.Trim());
                }
                return values;
            }
            var headers = Cells(rows[0]);
            if (headers.Any(string.IsNullOrEmpty) || headers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != headers.Count)
                throw new BusinessRequestException(400, "Column headers must be nonempty and unique");
            return rows.Skip(1).Select(row => { var cells = Cells(row); if (cells.Count > headers.Count) throw new BusinessRequestException(400, "Data extends beyond the column headers");
                return headers.Select((h, i) => new { h, value = i < cells.Count ? cells[i] : "" }).ToDictionary(x => x.h, x => x.value, StringComparer.OrdinalIgnoreCase); }).ToList();
        }
        catch (Exception e) when (e is InvalidDataException or XmlException or OverflowException or ArgumentException)
        { throw new BusinessRequestException(400, "Invalid or unsupported XLSX workbook"); }
    }
    internal static decimal Number(string value, bool optional = false)
    {
        if (optional && value.Length == 0) return 0;
        if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
            throw new BusinessRequestException(400, "Numbers must use a decimal point, without thousands separators");
        return number;
    }
    internal static DateOnly Date(string value)
    {
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date;
        // Excel normally stores a date cell as an OLE serial number. Restrict the
        // supported range and reject fractional dates instead of losing time data.
        if (decimal.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var serial) && serial >= 61 && serial <= 2958465 && decimal.Truncate(serial) == serial)
            return DateOnly.FromDateTime(DateTime.FromOADate((double)serial));
        throw new BusinessRequestException(400, "Dates must use yyyy-MM-dd or an Excel date cell");
    }
}
