using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using YelgLens.Intake.DAL.Data;
using YelgLens.Intake.Model.Auth;

namespace YelgLens.Intake.PL.Infrastructure;

/// <summary>
/// يضيف صلاحيات المستخدم إلى هويته عند الدخول، فيُفحص الفعل بصلاحيته لا بدوره.
/// تغيير سياسة الوصول يصبح تعديلاً في جدول الصلاحيات لا في كود الصفحات.
/// </summary>
public sealed class PermissionClaimsFactory
    : UserClaimsPrincipalFactory<User, Role>
{
    public const string PermissionClaimType = "permission";

    private readonly MainDbContext _db;

    public PermissionClaimsFactory(
        UserManager<User> users,
        RoleManager<Role> roles,
        IOptions<IdentityOptions> options,
        MainDbContext db)
        : base(users, roles, options) => _db = db;

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(User user)
    {
        var identity = await base.GenerateClaimsAsync(user);

        var roleIds = await _db.UserRoles
            .Where(ur => ur.UserId == user.Id)
            .Select(ur => ur.RoleId)
            .ToListAsync();

        var codes = await _db.RolePermissions
            .Where(rp => roleIds.Contains(rp.RoleId))
            .Select(rp => rp.Permission.Code)
            .Distinct()
            .ToListAsync();

        foreach (var code in codes)
            identity.AddClaim(new Claim(PermissionClaimType, code));

        if (!string.IsNullOrWhiteSpace(user.FullName))
            identity.AddClaim(new Claim(ClaimTypes.GivenName, user.FullName));

        return identity;
    }
}
