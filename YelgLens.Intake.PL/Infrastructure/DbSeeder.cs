using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using YelgLens.Intake.DAL.Data;
using YelgLens.Intake.Model.Auth;

namespace YelgLens.Intake.PL.Infrastructure;

/// <summary>
/// يهيّئ الأدوار والصلاحيات وحساب إدارة أولاً. يعمل عند كل إقلاع ولا يكرر:
/// كل خطوة تفحص وجود ما تنشئه قبل إنشائه.
/// </summary>
public static class DbSeeder
{
    private static readonly (string Code, string Name)[] Catalog =
    {
        (PermissionCodes.DocumentUpload,  "رفع مستند"),
        (PermissionCodes.DocumentReview,  "مراجعة مستند"),
        (PermissionCodes.DocumentApprove, "اعتماد مستند"),
        (PermissionCodes.DocumentExport,  "تصدير Excel"),
        (PermissionCodes.OdooPublish,     "الترحيل إلى أودو"),
        (PermissionCodes.UserManage,      "إدارة المستخدمين")
    };

    private static readonly Dictionary<RoleType, string[]> RoleGrants = new()
    {
        [RoleType.SystemAdmin] = Catalog.Select(c => c.Code).ToArray(),

        // المراجع يراجع ويعتمد ويصدّر ويرحّل — ولا يدير مستخدمين.
        [RoleType.Auditor] = new[]
        {
            PermissionCodes.DocumentUpload, PermissionCodes.DocumentReview,
            PermissionCodes.DocumentApprove, PermissionCodes.DocumentExport,
            PermissionCodes.OdooPublish
        },

        // المندوب يرفع فقط. لا يعتمد ما التقطه بنفسه.
        [RoleType.Representative] = new[] { PermissionCodes.DocumentUpload },

        [RoleType.Viewer] = new[] { PermissionCodes.DocumentReview }
    };

    public static async Task SeedAsync(IServiceProvider services, IConfiguration config, ILogger log)
    {
        var db = services.GetRequiredService<MainDbContext>();
        var userManager = services.GetRequiredService<UserManager<User>>();
        var roleManager = services.GetRequiredService<RoleManager<Role>>();

        await db.Database.MigrateAsync();

        foreach (var (code, name) in Catalog)
            if (!await db.Permissions.AnyAsync(p => p.Code == code))
                db.Permissions.Add(new Permission { Code = code, Name = name });
        await db.SaveChangesAsync();

        foreach (var type in Enum.GetValues<RoleType>())
        {
            var roleName = type.ToString();
            if (!await roleManager.RoleExistsAsync(roleName))
                await roleManager.CreateAsync(new Role(roleName) { Type = type });
        }

        foreach (var (type, codes) in RoleGrants)
        {
            var role = await roleManager.FindByNameAsync(type.ToString());
            if (role is null) continue;

            foreach (var code in codes)
            {
                var permission = await db.Permissions.FirstAsync(p => p.Code == code);
                var exists = await db.RolePermissions
                    .AnyAsync(rp => rp.RoleId == role.Id && rp.PermissionId == permission.Id);

                if (!exists)
                    db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permission.Id });
            }
        }
        await db.SaveChangesAsync();

        await SeedAdminAsync(userManager, config, log);
    }

    private static async Task SeedAdminAsync(UserManager<User> users, IConfiguration config, ILogger log)
    {
        var email = config["Seed:AdminEmail"];
        if (string.IsNullOrWhiteSpace(email)) email = "admin@yelglens.local";

        if (await users.FindByEmailAsync(email) is not null) return;

        // كلمة المرور تُقرأ من الإعدادات. إن غابت تُولَّد عشوائية وتُطبع مرة
        // واحدة — لأن كلمة ثابتة في الكود تصير كلمةَ كل تنصيب.
        var password = config["Seed:AdminPassword"];
        var generated = string.IsNullOrWhiteSpace(password);
        if (generated) password = GeneratePassword();

        var admin = new User
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            FirstName = "مدير",
            LastName = "النظام",
            IsActive = true
        };

        var result = await users.CreateAsync(admin, password!);
        if (!result.Succeeded)
        {
            log.LogError("تعذّر إنشاء حساب الإدارة: {Errors}",
                string.Join(" | ", result.Errors.Select(e => e.Description)));
            return;
        }

        await users.AddToRoleAsync(admin, RoleType.SystemAdmin.ToString());

        if (generated)
            log.LogWarning(
                "أُنشئ حساب إدارة أولي — البريد {Email} وكلمة المرور {Password}. " +
                "غيّرها فوراً، أو اضبط Seed:AdminPassword في الإعدادات.", email, password);
        else
            log.LogInformation("أُنشئ حساب الإدارة {Email} من الإعدادات.", email);
    }

    private static string GeneratePassword()
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

        while (chars.Count < 16)
            chars.Add(all[RandomNumberGenerator.GetInt32(all.Length)]);

        return new string(chars.OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue)).ToArray());
    }
}
