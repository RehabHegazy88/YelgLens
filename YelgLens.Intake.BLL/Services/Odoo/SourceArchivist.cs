using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using YelgLens.Intake.Model.Documents;

namespace YelgLens.Intake.BLL.Services.Odoo;

/// <summary>ما جرى للأصل بعد الترحيل.</summary>
public sealed record ArchiveOutcome(bool Attached, bool Removed, long? AttachmentId, string Message);

public interface ISourceArchivist
{
    bool Enabled { get; }
    bool RemovesLocalCopy { get; }

    /// <summary>
    /// يرفع أصل المستند على أمر البيع، ثم يحذفه من القرص إن أُذن بذلك وتأكّد
    /// وصوله.
    /// </summary>
    Task<ArchiveOutcome> ArchiveAsync(IntakeDocument document, string storageRoot, CancellationToken ct = default);

    /// <summary>
    /// يحذف نسختنا بعد أن يقرّ إنسانٌ أنه رأى المرفق في أودو.
    ///
    /// ويُعاد التحقق هنا رغم إقراره: قد يمرّ وقتٌ بين نظره والضغطة، وقد يُحذف
    /// المرفق في تلك الأثناء. الإقرار شرطٌ لا يُغني عن الفحص.
    /// </summary>
    Task<ArchiveOutcome> ReleaseLocalAsync(IntakeDocument document, string storageRoot, CancellationToken ct = default);
}

/// <summary>
/// ينقل أصل المستند من قرصنا إلى أودو.
///
/// الغاية توفير المساحة: قِيس أن متوسط الملف ٢٨٤ ك.ب وأن الصور تسعة أعشار
/// الحجم، فمئة مستندٍ يومياً تعني نحو سبعة جيجابايت في السنة.
///
/// والترتيب لا يُعكس أبداً: يُرفع، ثم يُقرأ من أودو ويُقارن حجمه، ثم — وحينئذٍ
/// فقط — يُحذف. وأي تعثّرٍ في أيٍّ من الخطوتين يُبقي الأصل عندنا: ملفٌ زائد
/// على القرص أهون من دليلٍ ضائع.
/// </summary>
public sealed class SourceArchivist : ISourceArchivist
{
    private readonly IOdooAttachmentService _attachments;
    private readonly OdooSettings _settings;
    private readonly ILogger<SourceArchivist> _log;

    public SourceArchivist(
        IOdooAttachmentService attachments,
        IOptionsSnapshot<OdooSettings> settings,
        ILogger<SourceArchivist> log)
    {
        _attachments = attachments;
        _settings = settings.Value;
        _log = log;
    }

    public bool Enabled => _settings.AttachSourceOnPublish && _attachments.CanWrite;

    public bool RemovesLocalCopy => Enabled && _settings.DeleteLocalAfterAttach;

    public async Task<ArchiveOutcome> ArchiveAsync(
        IntakeDocument document, string storageRoot, CancellationToken ct = default)
    {
        if (!Enabled)
            return new ArchiveOutcome(false, false, null, "");

        if (document.OdooOrderId is not { } orderId)
            return new ArchiveOutcome(false, false, null, "لا أوردر في أودو لِيُرفع عليه الأصل.");

        if (!document.BelongsToDatabase(_settings.Database))
            return new ArchiveOutcome(false, false, null,
                $"هذا المستند مُرحَّل على «{document.OdooDatabase}» والنظام متصل بـ «{_settings.Database}». "
                + "لا يُرفع الأصل على أوردرٍ في قاعدةٍ أخرى.");

        if (document.OdooAttachmentId is not null)
            return new ArchiveOutcome(true, !document.HasLocalFile, document.OdooAttachmentId,
                "الأصل مرفوعٌ على الأوردر بالفعل.");

        // الصفحات تُرفع كلها لا الأولى وحدها: الورقة قد تكون ثلاثاً، ودليلٌ
        // ناقص صفحةً ليس دليلاً.
        var files = FilesOf(document, storageRoot).ToList();

        if (files.Count == 0)
            return new ArchiveOutcome(false, false, null, "لم يُعثر على الأصل على القرص.");

        long? first = null;
        var uploaded = new List<(string Path, long Id)>();

        foreach (var (path, name, page) in files)
        {
            byte[] content;
            try { content = await File.ReadAllBytesAsync(path, ct); }
            catch (IOException ex)
            {
                return new ArchiveOutcome(false, false, null, $"تعذّرت قراءة الأصل: {ex.Message}");
            }

            var result = await _attachments.UploadAsync("sale.order", orderId, name, content, ct);

            if (!result.Success)
                return new ArchiveOutcome(false, false, first, result.Message);

            first ??= result.AttachmentId;
            uploaded.Add((path, result.AttachmentId!.Value));

            // يُسجَّل مرفق كل صفحة على صفحتها: العارض يطلب صفحةً بعينها،
            // ومعرّفٌ واحدٌ للمستند كله يجعله يعرض الأولى دائماً.
            if (page is not null) page.OdooAttachmentId = result.AttachmentId;
        }

        document.OdooAttachmentId = first;
        document.SourceSizeBytes ??= files.Sum(f => new FileInfo(f.Path).Length);

        if (!RemovesLocalCopy)
            return new ArchiveOutcome(true, false, first,
                $"رُفع الأصل على الأوردر ({uploaded.Count} ملف). النسخة عندنا باقية.");

        // الحذف آخر خطوة، وقد سبقه تحققٌ من الحجم داخل خدمة المرفقات.
        var removed = 0;
        foreach (var (path, _) in uploaded)
        {
            try
            {
                File.Delete(path);
                removed++;
            }
            catch (IOException ex)
            {
                _log.LogWarning(ex, "رُفع أصل المستند {Id} ولم يُحذف {Path}.", document.Id, path);
            }
        }

        if (removed == uploaded.Count) document.FileRemovedDate = DateTime.Now;

        _log.LogInformation("رُفع أصل المستند {Id} على الأوردر {Order} وحُذف {Removed} من {Total} محلياً.",
            document.Id, orderId, removed, uploaded.Count);

        return new ArchiveOutcome(true, removed == uploaded.Count, first,
            removed == uploaded.Count
                ? $"رُفع الأصل على الأوردر وحُذف من قرصنا ({uploaded.Count} ملف). العرض يأتي من أودو."
                : $"رُفع الأصل، وتعذّر حذف {uploaded.Count - removed} ملف محلياً — سيبقى.");
    }

    public async Task<ArchiveOutcome> ReleaseLocalAsync(
        IntakeDocument document, string storageRoot, CancellationToken ct = default)
    {
        if (!document.HasLocalFile)
            return new ArchiveOutcome(true, true, document.OdooAttachmentId,
                "النسخة عندنا محذوفةٌ بالفعل.");

        if (document.OdooOrderId is not { } orderId)
            return new ArchiveOutcome(false, false, null, "لا أوردر في أودو.");

        // وهنا أشدّ: الحذف لا رجعة فيه. مقارنةُ الأحجام بمرفقات قاعدةٍ أخرى
        // قد تُصادف حجماً مطابقاً فتُجيز محو الدليل من غير أن يكون محفوظاً.
        if (!document.BelongsToDatabase(_settings.Database))
            return new ArchiveOutcome(false, false, document.OdooAttachmentId,
                $"أصل هذا المستند على «{document.OdooDatabase}» والنظام متصل بـ «{_settings.Database}». "
                + "لن تُحذف نسختنا قبل الرجوع إلى قاعدته.");

        if (document.OdooAttachmentId is null)
            return new ArchiveOutcome(false, false, null,
                "الأصل غير مرفوع على أودو. ارفعه أولاً.");

        // يُعدّ المرفوع ويُقارن بما عندنا: صفحةٌ ناقصة هناك تعني دليلاً ناقصاً
        // بعد الحذف، ولا يُكتشف بعده.
        var remote = await _attachments.ListAsync("sale.order", orderId, ct);
        var files = FilesOf(document, storageRoot).ToList();

        if (files.Count == 0)
            return new ArchiveOutcome(true, true, document.OdooAttachmentId,
                "لم يبق ملفٌ على قرصنا.");

        if (remote.Count < files.Count)
            return new ArchiveOutcome(false, false, document.OdooAttachmentId,
                $"في أودو {remote.Count} مرفق وعندنا {files.Count} ملف. لم يُحذف شيء.");

        // ولا يكفي العدد: يُطابق حجم كل ملفٍ بمرفقٍ يقابله.
        var sizes = remote.Select(a => a.Bytes).ToList();

        foreach (var (path, _, _) in files)
        {
            var length = new FileInfo(path).Length;

            if (!sizes.Remove(length))
                return new ArchiveOutcome(false, false, document.OdooAttachmentId,
                    $"لا مرفق في أودو بحجم {length} بايت يقابل «{Path.GetFileName(path)}». لم يُحذف شيء.");
        }

        var removed = 0;
        foreach (var (path, _, _) in files)
        {
            try { File.Delete(path); removed++; }
            catch (IOException ex)
            {
                _log.LogWarning(ex, "تعذّر حذف {Path} للمستند {Id}.", path, document.Id);
            }
        }

        if (removed == files.Count) document.FileRemovedDate = DateTime.Now;

        _log.LogInformation("حُذفت نسخة المستند {Id} محلياً بإقرارٍ بشري ({Removed} ملف).",
            document.Id, removed);

        return new ArchiveOutcome(true, removed == files.Count, document.OdooAttachmentId,
            removed == files.Count
                ? $"حُذفت نسختنا ({removed} ملف). العرض يأتي من أودو من الآن."
                : $"تعذّر حذف {files.Count - removed} ملف — سيبقى.");
    }

    /// <summary>ملفات المستند: صفحاته إن كانت، وإلا ملفه الواحد.</summary>
    private static IEnumerable<(string Path, string Name, IntakePage? Page)> FilesOf(
        IntakeDocument document, string root)
    {
        var pages = document.Pages.OrderBy(p => p.PageNumber).ToList();

        if (pages.Count > 0)
        {
            foreach (var page in pages)
            {
                var path = Path.Combine(root, page.StoredFileName);
                if (File.Exists(path))
                    yield return (path, pages.Count == 1
                        ? page.SourceFileName
                        : $"{Path.GetFileNameWithoutExtension(page.SourceFileName)} " +
                          $"(صفحة {page.PageNumber}){Path.GetExtension(page.SourceFileName)}",
                        page);
            }

            yield break;
        }

        var single = Path.Combine(root, document.StoredFileName);
        if (File.Exists(single)) yield return (single, document.SourceFileName, null);
    }
}
