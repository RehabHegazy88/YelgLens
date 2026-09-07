using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YelgLens.Intake.BLL.IRepository.Core;
using YelgLens.Intake.Model.Auth;
using YelgLens.Intake.Model.Documents;
using YelgLens.Intake.Model.Enums;

namespace YelgLens.Intake.PL.Pages;

/// <summary>
/// ما رفعه المندوب ومصيره.
///
/// بدون هذه الشاشة كان الرفع طريقاً في اتجاه واحد: المندوب يصوّر ويمضي، ولا
/// يعرف أن مستنده رُفض ولا لماذا — فتضيع الطلبية ولا يُسأل عنها أحد.
/// </summary>
[Authorize(Policy = PermissionCodes.DocumentUpload)]
public class MyDocumentsModel : PageModel
{
    private readonly IIntakeDocumentRepository _documents;
    private readonly UserManager<User> _users;

    public MyDocumentsModel(IIntakeDocumentRepository documents, UserManager<User> users)
    {
        _documents = documents;
        _users = users;
    }

    public List<IntakeDocument> Items { get; private set; } = new();

    public int RejectedCount => Items.Count(d => d.Status == IntakeStatus.Rejected);

    public async Task OnGetAsync()
    {
        var userId = long.Parse(_users.GetUserId(User)!);
        Items = await _documents.GetByUploaderAsync(userId);
    }

    public static string StatusLabel(IntakeStatus status) => status switch
    {
        IntakeStatus.AwaitingReview => "بانتظار المراجعة",
        IntakeStatus.UnderReview    => "قيد المراجعة",
        IntakeStatus.Approved       => "معتمد",
        IntakeStatus.Rejected       => "مرفوض — يحتاج إعادة",
        IntakeStatus.Published      => "مُرحَّل",
        _ => status.ToString()
    };

    /// <summary>
    /// الحالة تُقرأ باللون والنص معاً — اللون وحده لا يكفي لمن لا يميّزه.
    /// </summary>
    public static string StatusBadge(IntakeStatus status) => status switch
    {
        IntakeStatus.AwaitingReview => "badge-warning",
        IntakeStatus.UnderReview    => "badge-info",
        IntakeStatus.Approved       => "badge-success",
        IntakeStatus.Rejected       => "badge-error",
        IntakeStatus.Published      => "badge-neutral",
        _ => "badge-neutral"
    };

}
