using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using YelgLens.Intake.Model.Auth;

namespace YelgLens.Intake.PL.Pages.Users;

[Authorize(Policy = PermissionCodes.UserManage)]
public class IndexModel : PageModel
{
    private readonly UserManager<User> _users;
    private readonly RoleManager<Role> _roles;

    public IndexModel(UserManager<User> users, RoleManager<Role> roles)
    {
        _users = users;
        _roles = roles;
    }

    public List<Row> Items { get; private set; } = new();

    public List<string> Roles { get; private set; } = new();

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [TempData] public string? Feedback { get; set; }
    [TempData] public string? FeedbackKind { get; set; }

    /// <summary>كلمة المرور تُعرض مرة واحدة بعد الإنشاء ولا تُحفظ في مكان يمكن قراءته.</summary>
    [TempData] public string? IssuedPassword { get; set; }
    [TempData] public string? IssuedFor { get; set; }

    public sealed record Row(User User, string RoleName);

    public class InputModel
    {
        [Required(ErrorMessage = "أدخل البريد الإلكتروني.")]
        [EmailAddress(ErrorMessage = "صيغة البريد غير صحيحة.")]
        public string Email { get; set; } = "";

        [Required(ErrorMessage = "أدخل الاسم الأول.")]
        public string FirstName { get; set; } = "";

        public string? LastName { get; set; }

        public string? BranchCode { get; set; }

        [Required(ErrorMessage = "اختر الدور.")]
        public string RoleName { get; set; } = nameof(RoleType.Representative);
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostCreateAsync()
    {
        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }

        if (await _users.FindByEmailAsync(Input.Email) is not null)
        {
            Feedback = "هذا البريد مستعمل بالفعل.";
            FeedbackKind = "bad";
            return RedirectToPage();
        }

        var user = new User
        {
            UserName = Input.Email.Trim(),
            Email = Input.Email.Trim(),
            EmailConfirmed = true,
            FirstName = Input.FirstName.Trim(),
            LastName = Input.LastName?.Trim(),
            BranchCode = string.IsNullOrWhiteSpace(Input.BranchCode) ? null : Input.BranchCode.Trim(),
            IsActive = true
        };

        // كلمة المرور تُولَّد ولا تُطلب من المدير: كلمة يخترعها إنسان على عجل
        // تصير كلمة كل الحسابات.
        var password = Generate();

        var result = await _users.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            Feedback = string.Join(" | ", result.Errors.Select(e => e.Description));
            FeedbackKind = "bad";
            return RedirectToPage();
        }

        await _users.AddToRoleAsync(user, Input.RoleName);

        IssuedPassword = password;
        IssuedFor = user.Email;
        Feedback = "أُنشئ الحساب.";
        FeedbackKind = "ok";

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleAsync(long id)
    {
        var user = await _users.FindByIdAsync(id.ToString());
        if (user is null) return RedirectToPage();

        // لا يوقف المدير حسابه فيُغلق الباب على نفسه ولا يبقى من يفتحه.
        if (user.Id.ToString() == _users.GetUserId(User))
        {
            Feedback = "لا يمكنك إيقاف حسابك أنت.";
            FeedbackKind = "bad";
            return RedirectToPage();
        }

        user.IsActive = !user.IsActive;
        await _users.UpdateAsync(user);

        Feedback = user.IsActive ? "فُعّل الحساب." : "أُوقف الحساب.";
        FeedbackKind = "ok";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResetAsync(long id)
    {
        var user = await _users.FindByIdAsync(id.ToString());
        if (user is null) return RedirectToPage();

        var password = Generate();
        var token = await _users.GeneratePasswordResetTokenAsync(user);
        var result = await _users.ResetPasswordAsync(user, token, password);

        if (!result.Succeeded)
        {
            Feedback = string.Join(" | ", result.Errors.Select(e => e.Description));
            FeedbackKind = "bad";
            return RedirectToPage();
        }

        IssuedPassword = password;
        IssuedFor = user.Email;
        Feedback = "أُعيد ضبط كلمة المرور.";
        FeedbackKind = "ok";
        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        Roles = await _roles.Roles.Select(r => r.Name!).ToListAsync();

        var rows = new List<Row>();
        foreach (var user in await _users.Users.Where(u => !u.Deleted).OrderBy(u => u.Email).ToListAsync())
        {
            var names = await _users.GetRolesAsync(user);
            rows.Add(new Row(user, names.FirstOrDefault() ?? "—"));
        }

        Items = rows;
    }

    private static string Generate()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnpqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "!@#$%^&*";
        var all = upper + lower + digits + symbols;

        var chars = new List<char>
        {
            upper[RandomNumberGenerator.GetInt32(upper.Length)],
            lower[RandomNumberGenerator.GetInt32(lower.Length)],
            digits[RandomNumberGenerator.GetInt32(digits.Length)],
            symbols[RandomNumberGenerator.GetInt32(symbols.Length)]
        };

        while (chars.Count < 14)
            chars.Add(all[RandomNumberGenerator.GetInt32(all.Length)]);

        return new string(chars.OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue)).ToArray());
    }

    public static string RoleLabel(string role) => role switch
    {
        nameof(RoleType.SystemAdmin)    => "مدير النظام",
        nameof(RoleType.Auditor)        => "مراجع",
        nameof(RoleType.Representative) => "مندوب",
        nameof(RoleType.Viewer)         => "اطّلاع",
        _ => role
    };
}
