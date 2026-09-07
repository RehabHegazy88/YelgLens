using System.ComponentModel.DataAnnotations;
using YelgLens.Intake.Model.Auth;
using YelgLens.Intake.Model.Basic;

namespace YelgLens.Intake.Model.Settings;

/// <summary>
/// عميلٌ من عملائنا — الشركة التي نستقبل مستنداتها ونرحّلها إلى أودو الخاص بها.
///
/// هو وحدة العزل في النظام كله: مستنداته وربطاته وكشف عملائه ومستخدموه، كلٌّ
/// منها له وحده. وكانت هذه الوحدة قبلاً «قاعدة أودو» — اسمُ نصٍّ يتكرر في
/// الجداول — فصارت كياناً له صفحته: يُسمّى، ويُعطَّل، ويُعرف من يعمل عليه، وله
/// وصلةٌ أو وصلتان (اختبارٌ وإنتاج) يُنتقل بينهما.
/// </summary>
public class Client : MainBaseEntity
{
    [Required, StringLength(150)]
    public string Name { get; set; } = "";

    /// <summary>رمزٌ قصير يميّزه في الشاشات الضيقة والسجلات.</summary>
    [StringLength(30)]
    public string? Code { get; set; }

    [StringLength(150)]
    public string? ContactName { get; set; }

    [StringLength(150)]
    public string? ContactEmail { get; set; }

    [StringLength(50)]
    public string? ContactPhone { get; set; }

    [StringLength(600)]
    public string? Note { get; set; }

    /// <summary>
    /// عميلٌ معطَّل لا يُعمل عليه ولا يُختار، ولا تُمسّ بياناته.
    ///
    /// التعطيل لا الحذف: مستنداته دليلٌ عند الخلاف، ومحوُها بضغطةٍ لأن العقد
    /// انتهى خطأٌ لا يُستدرك.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public virtual ICollection<OdooConnection> Connections { get; set; } = new List<OdooConnection>();

    public virtual ICollection<ClientUser> Users { get; set; } = new List<ClientUser>();

    public string Display => string.IsNullOrWhiteSpace(Code) ? Name : $"{Name} ({Code})";
}

/// <summary>
/// إسنادُ مستخدمٍ إلى عميل.
///
/// والمستخدم قد يُسنَد إلى أكثر من عميل — موظّفٌ عندنا يخدم شركتين — فيختار
/// بينها. أما من لا إسناد له فلا يرى شيئاً: الافتراض المنع، لأن الافتراض
/// المقابل يعني أن مستخدماً أُنشئ ونُسي أن يُقيَّد يرى مستندات كل العملاء.
/// </summary>
public class ClientUser : MainBaseEntity
{
    public long ClientId { get; set; }

    public long UserId { get; set; }

    public virtual Client? Client { get; set; }

    public virtual User? User { get; set; }
}
