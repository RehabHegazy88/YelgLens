using Microsoft.Extensions.Options;
using YelgLens.Intake.BLL.Services.Odoo;
using YelgLens.Intake.Model.Documents;
using YelgLens.Intake.Model.Enums;

namespace YelgLens.Intake.BLL.Services.Publishing;

public sealed record PublishResult(bool Success, string? OrderReference, string Message);

/// <summary>
/// الترحيل إلى نظام خارجي. الواجهة قائمة من الآن بشكلها النهائي حتى لا يستلزم
/// تفعيل أودو لاحقاً إعادة ترتيب المسار — يُبدَّل المنفذ وحده.
/// </summary>
public interface IErpPublisher
{
    bool IsConfigured { get; }
    Task<PublishResult> PublishAsync(ExtractedOrder order, CancellationToken ct = default);
}

/// <summary>
/// منفذ أودو عبر XML-RPC. غير مفعّل — ينتظر أربعة أمور من الفريق الفني:
///
///   ١. عنوان أودو واسم قاعدة البيانات
///   ٢. حساب خدمة مستقل (لا حساب شخصي) بصلاحيات محددة:
///      قراءة على res.partner و product.product و product.pricelist،
///      قراءة وإنشاء على sale.order، وإنشاء على ir.attachment
///   ٣. بيئة اختبار — لا يُجرَّب الترحيل على بيانات إنتاج
///   ٤. تثبيت أن رقم أمر الشراء يُكتب في client_order_ref
/// </summary>
public sealed class OdooXmlRpcPublisher : IErpPublisher
{
    private readonly OdooSettings _settings;

    public OdooXmlRpcPublisher(IOptionsSnapshot<OdooSettings> settings) => _settings = settings.Value;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_settings.Url) &&
        !string.IsNullOrWhiteSpace(_settings.Database) &&
        !string.IsNullOrWhiteSpace(_settings.ServiceUser);

    public Task<PublishResult> PublishAsync(ExtractedOrder order, CancellationToken ct = default)
    {
        if (!IsConfigured)
            return Task.FromResult(new PublishResult(false, null,
                "أودو غير مهيّأ بعد. المرحلة الحالية تنتهي بملف Excel."));

        // القاعدة التي تحكم هذه الدالة عند تفعيلها:
        // لا يُنشأ أمر بيع ما دام في المستند بند مانع.
        if (order.Issues.Any(i => i.Severity == IssueSeverity.Blocking))
            return Task.FromResult(new PublishResult(false, null,
                "يوجد بند مانع. لا يُنشأ أوردر ناقص."));

        throw new NotImplementedException(
            "التنفيذ مؤجل حتى تتوفر بيانات الاتصال وحساب الخدمة وبيئة الاختبار.");
    }
}
