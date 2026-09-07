using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YelgLens.Intake.BLL.Services.Review;
using YelgLens.Intake.Model.Auth;
using YelgLens.Intake.Model.Enums;

namespace YelgLens.Intake.PL.Pages;

[Authorize(Policy = PermissionCodes.DocumentReview)]
public class DashboardModel : PageModel
{
    private readonly IIntakeDashboard _dashboard;

    public DashboardModel(IIntakeDashboard dashboard) => _dashboard = dashboard;

    public DashboardView View { get; private set; } = null!;

    public async Task OnGetAsync(CancellationToken ct) => View = await _dashboard.BuildAsync(ct);

    /// <summary>مراحل المسار بترتيبها الفعلي، لا بترتيب التعداد.</summary>
    public static readonly (IntakeStatus Status, string Label)[] Stages =
    {
        (IntakeStatus.AwaitingReview, "بانتظار المراجعة"),
        (IntakeStatus.UnderReview,    "قيد المراجعة"),
        (IntakeStatus.Approved,       "معتمد"),
        (IntakeStatus.Published,      "مُرحَّل إلى أودو"),
        (IntakeStatus.Rejected,       "مرفوض")
    };

    public static string StatusBadge(IntakeStatus status) => status switch
    {
        IntakeStatus.AwaitingReview => "badge-warning",
        IntakeStatus.UnderReview    => "badge-info",
        IntakeStatus.Approved       => "badge-success",
        IntakeStatus.Rejected       => "badge-error",
        IntakeStatus.Published      => "badge-neutral",
        _ => "badge-neutral"
    };

    public static string StatusLabel(IntakeStatus status) =>
        Stages.FirstOrDefault(s => s.Status == status).Label ?? status.ToString();
}
