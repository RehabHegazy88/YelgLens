using System.Net;
using System.Text;
using ClosedXML.Excel;

namespace YelgLens.Intake.BLL.Services.Extraction;

/// <summary>
/// يحوّل جدول البيانات إلى جدول HTML للعرض في شاشة المراجعة.
///
/// المتصفح يعرض الـ PDF والصور بنفسه ولا يعرض xlsx، فبغير هذا يبقى المراجع
/// يعتمد ملفاً لا يراه — وهي الحالة التي بُنيت هذه الشاشة لمنعها.
/// </summary>
public sealed class SpreadsheetPreview
{
    /// <summary>سقف الصفوف المعروضة، منعاً لصفحة لا تنتهي.</summary>
    private const int MaximumRows = 400;

    /// <summary>
    /// يعرض جدولاً وصل بايتاتٍ لا مساراً.
    ///
    /// الأصل قد يكون محذوفاً من قرصنا ومجلوباً من أودو، فلا مسار له. والمنطق
    /// واحدٌ في الحالين، فيُفصل عن مصدر الملف.
    /// </summary>
    public string ToHtml(byte[] content)
    {
        using var stream = new MemoryStream(content);
        using var workbook = new XLWorkbook(stream);
        return Render(workbook);
    }

    /// <summary>يبني جدول HTML من مصنّفٍ مفتوح — منطقٌ واحدٌ لكل المصادر.</summary>
    private string Render(XLWorkbook workbook)
    {
        var html = new StringBuilder();

        html.Append("""
            <!doctype html>
            <html lang="ar" dir="rtl"><head><meta charset="utf-8" />
            <style>
              body { margin:0; padding:12px; background:#F7F8FA;
                     font-family:'Segoe UI', Tahoma, sans-serif; font-size:12.5px; color:#16202E; }
              h4 { margin:0 0 8px; font-size:12px; color:#64748B; font-weight:500; }
              table { border-collapse:collapse; background:#fff; width:100%; }
              th, td { border:1px solid #E2E8F0; padding:5px 8px; text-align:right; white-space:nowrap; }
              thead th { background:#1F3864; color:#fff; position:sticky; top:0; font-weight:500; }
              tbody tr:nth-child(even) td { background:#FAFBFC; }
              td.n { font-family:Consolas, monospace; text-align:center; direction:ltr; }
              .more { margin-top:10px; color:#B45309; font-size:12px; }
            </style></head><body>
            """);


        foreach (var sheet in workbook.Worksheets)
        {
            var rows = sheet.RowsUsed().ToList();
            if (rows.Count == 0) continue;

            html.Append("<h4>").Append(Escape(sheet.Name)).Append("</h4><table>");

            var lastColumn = rows.Max(r => r.LastCellUsed()?.Address.ColumnNumber ?? 0);
            var shown = 0;
            var first = true;

            foreach (var row in rows.Take(MaximumRows))
            {
                html.Append(first ? "<thead><tr>" : "<tr>");

                for (var column = 1; column <= lastColumn; column++)
                {
                    var cell = row.Cell(column);
                    var text = Escape(cell.GetString().Trim());
                    var numeric = cell.DataType == XLDataType.Number;

                    html.Append(first ? "<th>" : numeric ? "<td class=\"n\">" : "<td>")
                        .Append(text)
                        .Append(first ? "</th>" : "</td>");
                }

                html.Append(first ? "</tr></thead><tbody>" : "</tr>");
                first = false;
                shown++;
            }

            html.Append("</tbody></table>");

            if (rows.Count > shown)
                html.Append("<div class=\"more\">عُرض ")
                    .Append(shown).Append(" صفاً من ").Append(rows.Count)
                    .Append(". نزّل الأصل لرؤية الباقي.</div>");
        }

        html.Append("</body></html>");
        return html.ToString();
    }

    public string ToHtml(string filePath)
    {
        using var workbook = new XLWorkbook(filePath);
        return Render(workbook);
    }

    private static string Escape(string value) => WebUtility.HtmlEncode(value);
}
