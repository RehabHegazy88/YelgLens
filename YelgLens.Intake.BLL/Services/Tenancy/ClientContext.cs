using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using YelgLens.Intake.DAL.Data;
using YelgLens.Intake.Model.Auth;
using YelgLens.Intake.Model.Settings;

namespace YelgLens.Intake.BLL.Services.Tenancy;

public interface IClientContext
{
    /// <summary>هل ثمّة مستخدمٌ داخلٌ أصلاً؟</summary>
    long? UserId { get; }

    /// <summary>العميل الذي يعمل عليه المستخدم الآن، إن كان له عميل.</summary>
    Task<Client?> CurrentAsync(CancellationToken ct = default);

    /// <summary>العملاء المسموح لهذا المستخدم بالعمل عليهم.</summary>
    Task<List<Client>> AvailableAsync(CancellationToken ct = default);

    /// <summary>ينقل المستخدم إلى عميلٍ من عملائه. يرفض ما ليس منها.</summary>
    Task<bool> SwitchAsync(long clientId, CancellationToken ct = default);

    /// <summary>الوصلة المعمول بها عند العميل الحالي.</summary>
    Task<OdooConnection?> ActiveConnectionAsync(CancellationToken ct = default);
}

/// <summary>
/// يحدّد العميل الذي يعمل عليه المستخدم الداخل، ومنه تُشتقّ وصلة أودو.
///
/// وهذا هو مفصل العزل: قبله كانت الوصلة المفعَّلة واحدةً للنظام كله، فمن
/// بدّلها بدّلها على الجميع — وموظّفُ عميلٍ يرى شغل عميلٍ آخر لأن زميله نقل
/// الوصلة. صار الاختيار على المستخدم نفسه، ومقيّداً بمن أُسند إليهم.
///
/// ومن لا إسناد له لا يرى شيئاً. الافتراض المنع لا السماح: مستخدمٌ أُنشئ
/// ونُسي أن يُقيَّد يجب أن يعجز عن العمل، لا أن يرى كل العملاء.
///
/// ويُستثنى من ذلك حاملُ صلاحية إدارة المستخدمين وحده — وإلا لما استطاع أول
/// مسؤولٍ أن ينشئ العميل الأول ويُسند نفسه إليه.
/// </summary>
public sealed class ClientContext : IClientContext
{
    private readonly MainDbContext _db;
    private readonly IHttpContextAccessor _http;
    private readonly ILogger<ClientContext> _log;

    private Client? _resolved;
    private bool _tried;

    public ClientContext(MainDbContext db, IHttpContextAccessor http, ILogger<ClientContext> log)
    {
        _db = db;
        _http = http;
        _log = log;
    }

    public long? UserId
    {
        get
        {
            var value = _http.HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier);
            return long.TryParse(value, out var id) ? id : null;
        }
    }

    private bool IsAdministrator =>
        _http.HttpContext?.User?.HasClaim("permission", PermissionCodes.UserManage) == true;

    public async Task<List<Client>> AvailableAsync(CancellationToken ct = default)
    {
        if (UserId is not { } userId) return new List<Client>();

        var all = _db.Clients.Where(c => !c.Deleted && c.IsActive);

        if (IsAdministrator)
            return await all.OrderBy(c => c.Name).ToListAsync(ct);

        return await all
            .Where(c => c.Users.Any(u => u.UserId == userId && !u.Deleted))
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
    }

    public async Task<Client?> CurrentAsync(CancellationToken ct = default)
    {
        // يُحسب مرةً في الطلب الواحد: يُسأل عنه في كل استعلام على المستندات،
        // وسؤال قاعدة البيانات في كل مرة يضاعف الرحلات بلا فائدة.
        if (_tried) return _resolved;
        _tried = true;

        if (UserId is not { } userId) return _resolved = null;

        var available = await AvailableAsync(ct);
        if (available.Count == 0) return _resolved = null;

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);

        var chosen = available.FirstOrDefault(c => c.Id == user?.CurrentClientId);

        // ما اختاره سابقاً قد يكون عُطِّل أو سُحب إسناده. حينئذٍ يُنقل إلى أوّل
        // ما يملك ويُحفظ، فلا يبقى معلّقاً على عميلٍ لم يعد له.
        if (chosen is null && user is not null)
        {
            chosen = available[0];
            user.CurrentClientId = chosen.Id;
            await _db.SaveChangesAsync(ct);

            _log.LogInformation("نُقل المستخدم {User} إلى العميل {Client} تلقائياً.",
                userId, chosen.Name);
        }

        return _resolved = chosen;
    }

    public async Task<bool> SwitchAsync(long clientId, CancellationToken ct = default)
    {
        if (UserId is not { } userId) return false;

        var available = await AvailableAsync(ct);
        if (available.All(c => c.Id != clientId)) return false;

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return false;

        user.CurrentClientId = clientId;
        await _db.SaveChangesAsync(ct);

        _tried = false;
        _resolved = null;

        return true;
    }

    public async Task<OdooConnection?> ActiveConnectionAsync(CancellationToken ct = default)
    {
        if (await CurrentAsync(ct) is not { } client) return null;

        return await _db.OdooConnections
            .FirstOrDefaultAsync(c => !c.Deleted && c.ClientId == client.Id && c.IsActive, ct);
    }
}
