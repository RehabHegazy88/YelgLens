using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using YelgLens.Intake.DAL.Data;
using YelgLens.Intake.Model.Settings;

namespace YelgLens.Intake.BLL.Services.Odoo;

public interface IOdooConnectionStore
{
    /// <summary>أوصال عميلٍ بعينه.</summary>
    Task<List<OdooConnection>> ListAsync(long clientId, CancellationToken ct = default);

    /// <summary>الوصلة المعمول بها عند عميلٍ بعينه.</summary>
    Task<OdooConnection?> ActiveAsync(long clientId, CancellationToken ct = default);

    Task<OdooConnection?> FindAsync(long id, CancellationToken ct = default);

    /// <summary>يحفظ وصلاً — جديداً أو قائماً. المفتاح الفارغ يُبقي المحفوظ.</summary>
    Task<long> SaveAsync(OdooConnection input, string? apiKey, long userId, CancellationToken ct = default);

    /// <summary>يجعل هذا الوصل هو المعمول به، ويُطفئ ما عداه.</summary>
    Task ActivateAsync(long id, long userId, CancellationToken ct = default);

    Task RemoveAsync(long id, long userId, CancellationToken ct = default);

    Task RecordTestAsync(long id, string result, CancellationToken ct = default);

    /// <summary>يفتح الكتابة على وصلةٍ أو يقفلها، دون المساس ببقية إعداداتها.</summary>
    Task<bool> SetWritesAsync(long id, bool allow, long userId, CancellationToken ct = default);

    /// <summary>يفكّ تعمية المفتاح. لا يُستدعى إلا في الخادم عند بناء الإعدادات.</summary>
    string? RevealKey(OdooConnection connection);
}

/// <summary>
/// يحفظ أوصال أودو ويكشف المعمول به منها.
///
/// المفتاح يمرّ من هنا معمّى في الاتجاهين: يدخل نصاً من الشاشة فيُعمّى قبل
/// أن يلمس قاعدة البيانات، ولا يخرج إلا إلى العميل الذي ينادي أودو. ولا توجد
/// طريقةٌ لعرضه في شاشة — وهذا مقصود: من نسي المفتاح يكتب غيره، ولا يقرأ
/// القديم من الشاشة.
/// </summary>
public sealed class OdooConnectionStore : IOdooConnectionStore
{
    private const string Purpose = "YelgLens.Intake.Odoo.ApiKey.v1";

    private readonly MainDbContext _db;
    private readonly IDataProtector _protector;
    private readonly ILogger<OdooConnectionStore> _log;

    public OdooConnectionStore(
        MainDbContext db, IDataProtectionProvider protection, ILogger<OdooConnectionStore> log)
    {
        _db = db;
        _protector = protection.CreateProtector(Purpose);
        _log = log;
    }

    public Task<List<OdooConnection>> ListAsync(long clientId, CancellationToken ct = default) =>
        _db.OdooConnections.Where(c => !c.Deleted && c.ClientId == clientId)
            .OrderByDescending(c => c.IsActive).ThenBy(c => c.Name)
            .ToListAsync(ct);

    public Task<OdooConnection?> ActiveAsync(long clientId, CancellationToken ct = default) =>
        _db.OdooConnections.FirstOrDefaultAsync(
            c => !c.Deleted && c.ClientId == clientId && c.IsActive, ct);

    public Task<OdooConnection?> FindAsync(long id, CancellationToken ct = default) =>
        _db.OdooConnections.FirstOrDefaultAsync(c => !c.Deleted && c.Id == id, ct);

    public async Task<long> SaveAsync(
        OdooConnection input, string? apiKey, long userId, CancellationToken ct = default)
    {
        var connection = input.Id > 0
            ? await _db.OdooConnections.FirstOrDefaultAsync(c => c.Id == input.Id, ct)
            : null;

        if (connection is null)
        {
            connection = new OdooConnection { AddedBy_UserId = userId };
            _db.OdooConnections.Add(connection);
        }

        connection.ClientId = input.ClientId;
        connection.Name = input.Name.Trim();
        connection.Url = input.Url.Trim().TrimEnd('/');
        connection.Database = input.Database.Trim();
        connection.ServiceUser = input.ServiceUser.Trim();
        connection.AllowWrites = input.AllowWrites;
        connection.TimeoutSeconds = input.TimeoutSeconds > 0 ? input.TimeoutSeconds : 180;
        connection.AttachSourceOnPublish = input.AttachSourceOnPublish;
        connection.DeleteLocalAfterAttach = input.DeleteLocalAfterAttach;
        connection.IsProduction = input.IsProduction;
        connection.Note = input.Note;
        connection.ModifiedBy_UserId = userId;
        connection.LastModifiedDate = DateTime.Now;

        // المفتاح الفارغ من الشاشة يعني «لم أغيّره»، لا «امحه»: الشاشة لا
        // تعرض المحفوظ، فلو حُمل فراغُها على المحو لضاع المفتاح بكل حفظ.
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            connection.ApiKeyProtected = _protector.Protect(apiKey.Trim());
            connection.LastTestedDate = null;
            connection.LastTestResult = null;
        }

        await _db.SaveChangesAsync(ct);
        return connection.Id;
    }

    public async Task ActivateAsync(long id, long userId, CancellationToken ct = default)
    {
        var target = await _db.OdooConnections.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (target is null) return;

        // يُطفأ ما عداه عند عميله وحده: تفعيلُ إنتاج عميلٍ لا ينقل عميلاً آخر.
        var all = await _db.OdooConnections
            .Where(c => !c.Deleted && c.ClientId == target.ClientId)
            .ToListAsync(ct);

        foreach (var connection in all)
        {
            var wanted = connection.Id == id;
            if (connection.IsActive == wanted) continue;

            connection.IsActive = wanted;
            connection.ModifiedBy_UserId = userId;
            connection.LastModifiedDate = DateTime.Now;
        }

        await _db.SaveChangesAsync(ct);

        var active = all.FirstOrDefault(c => c.Id == id);

        _log.LogWarning("بُدِّل وصل أودو المعمول به إلى {Name} ({Database}, إنتاج={Production}) بواسطة {User}.",
            active?.Name, active?.Database, active?.IsProduction, userId);
    }

    public async Task RemoveAsync(long id, long userId, CancellationToken ct = default)
    {
        var connection = await _db.OdooConnections.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (connection is null) return;

        connection.Deleted = true;
        connection.DeleteDate = DateTime.Now;
        connection.IsActive = false;
        connection.ModifiedBy_UserId = userId;

        await _db.SaveChangesAsync(ct);
    }

    public async Task RecordTestAsync(long id, string result, CancellationToken ct = default)
    {
        var connection = await _db.OdooConnections.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (connection is null) return;

        connection.LastTestedDate = DateTime.Now;
        connection.LastTestResult = result.Length > 500 ? result[..500] : result;

        await _db.SaveChangesAsync(ct);
    }

    public async Task<bool> SetWritesAsync(
        long id, bool allow, long userId, CancellationToken ct = default)
    {
        var connection = await _db.OdooConnections.FirstOrDefaultAsync(c => !c.Deleted && c.Id == id, ct);
        if (connection is null) return false;

        connection.AllowWrites = allow;
        connection.ModifiedBy_UserId = userId;
        connection.LastModifiedDate = DateTime.Now;

        await _db.SaveChangesAsync(ct);

        // يُسجَّل بمستوى تحذير لا معلومة: فتحُ الكتابة هو اللحظة التي يصير
        // فيها النظام قادراً على إنشاء أوردرات في دفاتر شركة.
        _log.LogWarning("{State} الكتابة على وصلة {Name} ({Database}) بواسطة {User}.",
            allow ? "فُتحت" : "أُقفلت", connection.Name, connection.Database, userId);

        return true;
    }

    public string? RevealKey(OdooConnection connection)
    {
        if (string.IsNullOrWhiteSpace(connection.ApiKeyProtected)) return null;

        try
        {
            return _protector.Unprotect(connection.ApiKeyProtected);
        }
        catch (Exception ex)
        {
            // مفاتيح الحماية تُفقد إن نُقل التطبيق أو مُسح مجلد المفاتيح.
            // يُقال ذلك صراحةً: الوصل يبدو مضبوطاً وهو معطّل، وأسوأ ما يمكن
            // هنا أن يظهر عطلٌ في أودو وسببه عندنا.
            _log.LogError(ex, "تعذّر فكّ تعمية مفتاح الوصل {Name}. أعِد كتابة المفتاح.", connection.Name);
            return null;
        }
    }
}

/// <summary>
/// يبني إعدادات أودو من الوصل المعمول به في قاعدة البيانات.
///
/// وملف الإعدادات يبقى أساساً يُبنى فوقه: من لم يحفظ وصلاً في الشاشة يعمل
/// بما في الملف كما كان، فلا ينكسر شيءٌ قائم بمجرد وجود الشاشة.
/// </summary>
public sealed class OdooSettingsFromDatabase : IConfigureOptions<OdooSettings>
{
    private readonly IOdooConnectionStore _store;
    private readonly YelgLens.Intake.BLL.Services.Tenancy.IClientContext _clients;
    private readonly ILogger<OdooSettingsFromDatabase> _log;

    public OdooSettingsFromDatabase(
        IOdooConnectionStore store,
        YelgLens.Intake.BLL.Services.Tenancy.IClientContext clients,
        ILogger<OdooSettingsFromDatabase> log)
    {
        _store = store;
        _clients = clients;
        _log = log;
    }

    public void Configure(OdooSettings options)
    {
        OdooConnection? active;

        try
        {
            // وصلة العميل الذي يعمل عليه هذا المستخدم — لا وصلةٌ عامة.
            active = _clients.ActiveConnectionAsync().GetAwaiter().GetResult();

            // ولا مستخدمَ يعني سياقاً بلا عميل: ترحيلٌ أو أداة أو مهمة خلفية.
            // حينئذٍ يُعمل بما في ملف الإعدادات، ولا يُخمَّن عميل.
            if (active is null && _clients.UserId is not null)
            {
                // مستخدمٌ داخلٌ بلا عميلٍ مُسنَد: لا يُوصل بشيء عمداً.
                options.Url = "";
                options.Database = "";
                options.ApiKey = "";
                return;
            }
        }
        catch (Exception ex)
        {
            // قاعدةٌ غير مهيّأة بعد (قبل تطبيق الترحيل مثلاً) لا تُسقط التطبيق:
            // يُعمل بما في الملف ويُقال في السجل.
            _log.LogWarning(ex, "تعذّرت قراءة وصل أودو من قاعدة البيانات؛ يُعمل بما في ملف الإعدادات.");
            return;
        }

        if (active is null) return;

        options.Url = active.Url;
        options.Database = active.Database;
        options.ServiceUser = active.ServiceUser;
        options.AllowWrites = active.AllowWrites;
        options.TimeoutSeconds = active.TimeoutSeconds;
        options.AttachSourceOnPublish = active.AttachSourceOnPublish;
        options.DeleteLocalAfterAttach = active.DeleteLocalAfterAttach;

        if (_store.RevealKey(active) is { Length: > 0 } key) options.ApiKey = key;
    }
}
