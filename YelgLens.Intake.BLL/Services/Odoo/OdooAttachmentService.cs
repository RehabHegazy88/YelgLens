using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace YelgLens.Intake.BLL.Services.Odoo;

/// <summary>حصيلة رفع الأصل على أودو.</summary>
public sealed record AttachmentResult(bool Success, long? AttachmentId, long Bytes, string Message)
{
    public static AttachmentResult Fail(string message) => new(false, null, 0, message);
}

/// <summary>مرفقٌ كما هو في أودو.</summary>
public sealed record OdooAttachment(long Id, string Name, string MimeType, long Bytes);

public interface IOdooAttachmentService
{
    bool CanWrite { get; }

    /// <summary>يرفع ملفاً ويربطه بسجلٍ في أودو، ثم يتحقق من وصوله.</summary>
    Task<AttachmentResult> UploadAsync(
        string model, long recordId, string fileName, byte[] content, CancellationToken ct = default);

    /// <summary>يقرأ وصف المرفق دون محتواه — للتحقق من وجوده.</summary>
    Task<OdooAttachment?> DescribeAsync(long attachmentId, CancellationToken ct = default);

    /// <summary>ينزّل محتوى المرفق من أودو.</summary>
    Task<byte[]?> DownloadAsync(long attachmentId, CancellationToken ct = default);

    /// <summary>مرفقات سجلٍ في أودو — لتُعرض على من يقرّر حذف نسختنا.</summary>
    Task<List<OdooAttachment>> ListAsync(string model, long recordId, CancellationToken ct = default);
}

/// <summary>
/// يرفع أصل المستند على أمر البيع في أودو ويستردّه.
///
/// الغرض أن يصير أودو موضع الأصل فيُوفَّر قرصنا. ولذلك شرطٌ لا يُتنازل عنه:
/// <b>لا يُحذف الأصل عندنا إلا بعد قراءةِ المرفق من أودو والتأكد من حجمه</b>.
/// الرفع قد يُقبل ظاهراً ولا يصل، والحذف بعده لا رجعة فيه.
/// </summary>
public sealed class OdooAttachmentService : IOdooAttachmentService
{
    private readonly IOdooClient _odoo;
    private readonly ILogger<OdooAttachmentService> _log;

    public OdooAttachmentService(IOdooClient odoo, ILogger<OdooAttachmentService> log)
    {
        _odoo = odoo;
        _log = log;
    }

    public bool CanWrite => _odoo.CanWrite;

    /// <summary>أودو يستدلّ على نوع الملف بامتداده حين لا يُذكر النوع صراحةً.</summary>
    private static string MimeOf(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".pdf"  => "application/pdf",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png"  => "image/png",
        ".webp" => "image/webp",
        ".tif" or ".tiff" => "image/tiff",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".xlsm" => "application/vnd.ms-excel.sheet.macroEnabled.12",
        ".xls"  => "application/vnd.ms-excel",
        _ => "application/octet-stream"
    };

    public async Task<AttachmentResult> UploadAsync(
        string model, long recordId, string fileName, byte[] content, CancellationToken ct = default)
    {
        if (!_odoo.CanWrite)
            return AttachmentResult.Fail(
                "رفع المرفقات مغلق. يُفتح من «إعدادات أودو» بتعليم «اسمح بالكتابة في أودو» على الوصلة المعمول بها.");

        if (content.Length == 0)
            return AttachmentResult.Fail("الملف فارغ — لم يُرفع.");

        try
        {
            var values = new JsonObject
            {
                ["name"] = fileName,
                ["datas"] = Convert.ToBase64String(content),
                ["res_model"] = model,
                ["res_id"] = recordId,
                ["mimetype"] = MimeOf(fileName),
                ["type"] = "binary"
            };

            var id = await _odoo.CreateAsync("ir.attachment", values, ct);

            // التحقق بعد الرفع لا قبله: أودو يقبل الإنشاء ويعيد معرّفاً، وقد
            // لا يصل المحتوى كاملاً. وحذفُ الأصل بناءً على معرّفٍ وحده مقامرة.
            var stored = await DescribeAsync(id, ct);

            if (stored is null)
                return AttachmentResult.Fail(
                    $"أُنشئ المرفق {id} ولم يُعثر عليه عند القراءة. لم يُحذف الأصل عندنا.");

            if (stored.Bytes != content.Length)
                return AttachmentResult.Fail(
                    $"حجم المرفق في أودو {stored.Bytes} بايت والأصل {content.Length}. "
                    + "لم يُحذف الأصل عندنا.");

            _log.LogInformation("رُفع أصل المستند على {Model} {Record} بالمرفق {Attachment} ({Bytes} بايت).",
                model, recordId, id, content.Length);

            return new AttachmentResult(true, id, content.Length,
                $"رُفع الأصل على الأوردر ({content.Length / 1024} ك.ب) وتأكّد وصوله.");
        }
        catch (OdooException ex)
        {
            _log.LogWarning(ex, "تعذّر رفع مرفق المستند على {Model} {Record}.", model, recordId);
            return AttachmentResult.Fail($"تعذّر رفع الأصل على أودو: {ex.Message}");
        }
    }

    public async Task<OdooAttachment?> DescribeAsync(long attachmentId, CancellationToken ct = default)
    {
        try
        {
            // file_size يُقرأ دون المحتوى: التحقق من الوصول لا يستوجب جرّ
            // الملف كله عبر شبكةٍ بطيئة.
            var rows = await _odoo.ReadAsync("ir.attachment", new[] { attachmentId },
                new[] { "name", "mimetype", "file_size" }, ct);

            if (rows.Count == 0) return null;

            var row = rows[0];
            return new OdooAttachment(
                attachmentId,
                OdooValue.Text(row["name"]) ?? "",
                OdooValue.Text(row["mimetype"]) ?? "application/octet-stream",
                long.TryParse(OdooValue.Text(row["file_size"]), out var size) ? size : 0);
        }
        catch (OdooException)
        {
            return null;
        }
    }

    public async Task<List<OdooAttachment>> ListAsync(
        string model, long recordId, CancellationToken ct = default)
    {
        try
        {
            var rows = await _odoo.SearchReadAsync("ir.attachment",
                new object[]
                {
                    "&",
                    new object[] { "res_model", "=", model },
                    new object[] { "res_id", "=", recordId }
                },
                new[] { "id", "name", "mimetype", "file_size" }, order: "id", ct: ct);

            return rows.Select(r => new OdooAttachment(
                r["id"]?.GetValue<long>() ?? 0,
                OdooValue.Text(r["name"]) ?? "",
                OdooValue.Text(r["mimetype"]) ?? "application/octet-stream",
                long.TryParse(OdooValue.Text(r["file_size"]), out var size) ? size : 0))
                .Where(a => a.Id > 0)
                .ToList();
        }
        catch (OdooException)
        {
            return new List<OdooAttachment>();
        }
    }

    public async Task<byte[]?> DownloadAsync(long attachmentId, CancellationToken ct = default)
    {
        var rows = await _odoo.ReadAsync("ir.attachment", new[] { attachmentId },
            new[] { "datas" }, ct);

        if (rows.Count == 0) return null;

        var encoded = OdooValue.Text(rows[0]["datas"]);
        if (encoded is null) return null;

        try { return Convert.FromBase64String(encoded); }
        catch (FormatException) { return null; }
    }
}
