using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YelgLens.Intake.BLL.Services.Tenancy;
using YelgLens.Intake.Model.Auth;
using YelgLens.Intake.Model.Settings;

namespace YelgLens.Intake.PL.Pages.Settings;

/// <summary>
/// إدارة عملائنا — صفحةُ كلٍّ منهم ومن يعمل عليه.
///
/// العميل هنا وحدة العزل: مستنداته وربطاته وكشف عملائه ومستخدموه. وما يُسنَد
/// من مستخدمين هو ما يقرّر من يرى ماذا، فلا يبقى الأمر معلّقاً على «الوصلة
/// المفعَّلة» التي كان يبدّلها أيٌّ كان فينتقل الجميع معه.
/// </summary>
[Authorize(Policy = PermissionCodes.UserManage)]
public class ClientsModel : PageModel
{
    private readonly IClientService _clients;
    private readonly IClientContext _context;
    private readonly UserManager<User> _users;

    public ClientsModel(IClientService clients, IClientContext context, UserManager<User> users)
    {
        _clients = clients;
        _context = context;
        _users = users;
    }

    public List<ClientSummary> Items { get; private set; } = new();

    /// <summary>العميل المفتوحة صفحته، إن فُتحت.</summary>
    public ClientSummary? Opened { get; private set; }

    public List<User> Assigned { get; private set; } = new();

    public List<User> Unassigned { get; private set; } = new();

    /// <summary>العميل الذي يعمل عليه المسؤول نفسه الآن.</summary>
    public long? CurrentClientId { get; private set; }

    [BindProperty] public InputModel Input { get; set; } = new();

    [TempData] public string? Feedback { get; set; }
    [TempData] public string? FeedbackKind { get; set; }

    public class InputModel
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string? Code { get; set; }
        public string? ContactName { get; set; }
        public string? ContactEmail { get; set; }
        public string? ContactPhone { get; set; }
        public string? Note { get; set; }
    }

    public async Task OnGetAsync(long? open, CancellationToken ct) => await LoadAsync(open, ct);

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Input.Name))
        {
            Feedback = "اسم العميل إلزامي.";
            FeedbackKind = "bad";
            return RedirectToPage();
        }

        var id = await _clients.SaveAsync(new Client
        {
            Id = Input.Id,
            Name = Input.Name,
            Code = Input.Code,
            ContactName = Input.ContactName,
            ContactEmail = Input.ContactEmail,
            ContactPhone = Input.ContactPhone,
            Note = Input.Note
        }, UserId(), ct);

        Feedback = Input.Id > 0 ? "حُفظ التعديل." : "أُضيف العميل. أسند إليه مستخدمين واضبط وصلة أودو.";
        FeedbackKind = "ok";

        return RedirectToPage(new { open = id });
    }

    public async Task<IActionResult> OnPostSetActiveAsync(long id, bool active, CancellationToken ct)
    {
        await _clients.SetActiveAsync(id, active, UserId(), ct);

        Feedback = active ? "أُعيد تفعيل العميل." : "عُطِّل العميل. بياناته باقية ولم تُمسّ.";
        FeedbackKind = "ok";

        return RedirectToPage(new { open = id });
    }

    public async Task<IActionResult> OnPostAssignAsync(long id, long userId, CancellationToken ct)
    {
        await _clients.AssignAsync(id, userId, UserId(), ct);

        Feedback = "أُسند المستخدم إلى هذا العميل.";
        FeedbackKind = "ok";

        return RedirectToPage(new { open = id });
    }

    public async Task<IActionResult> OnPostUnassignAsync(long id, long userId, CancellationToken ct)
    {
        await _clients.UnassignAsync(id, userId, ct);

        Feedback = "سُحب إسناد المستخدم.";
        FeedbackKind = "ok";

        return RedirectToPage(new { open = id });
    }

    /// <summary>ينقل المسؤول نفسه للعمل على هذا العميل.</summary>
    public async Task<IActionResult> OnPostSwitchAsync(long id, CancellationToken ct)
    {
        if (await _context.SwitchAsync(id, ct))
        {
            Feedback = "انتقلت إلى هذا العميل. كل الشاشات تعرض بياناته الآن.";
            FeedbackKind = "ok";
        }
        else
        {
            Feedback = "تعذّر الانتقال — العميل غير متاح لك.";
            FeedbackKind = "bad";
        }

        return RedirectToPage(new { open = id });
    }

    private async Task LoadAsync(long? open, CancellationToken ct)
    {
        Items = await _clients.ListAsync(ct);
        CurrentClientId = (await _context.CurrentAsync(ct))?.Id;

        if (open is not { } id) return;

        Opened = Items.FirstOrDefault(s => s.Client.Id == id);
        if (Opened is null) return;

        Assigned = await _clients.AssignedUsersAsync(id, ct);
        Unassigned = await _clients.UnassignedUsersAsync(id, ct);

        Input = new InputModel
        {
            Id = Opened.Client.Id,
            Name = Opened.Client.Name,
            Code = Opened.Client.Code,
            ContactName = Opened.Client.ContactName,
            ContactEmail = Opened.Client.ContactEmail,
            ContactPhone = Opened.Client.ContactPhone,
            Note = Opened.Client.Note
        };
    }

    private long UserId() => long.Parse(_users.GetUserId(User)!);
}
