using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using YelgLens.Intake.DAL.Data;
using YelgLens.Intake.Model.Auth;
using YelgLens.Intake.Model.Settings;

namespace YelgLens.Intake.BLL.Services.Tenancy;

/// <summary>صورةٌ موجزة عن عميل — تُعرض في القائمة بلا استعلامٍ لكل صف.</summary>
public sealed record ClientSummary(
    Client Client,
    int Users,
    int Connections,
    OdooConnection? Active,
    int Documents,
    int Mappings,
    int Customers);

public interface IClientService
{
    Task<List<ClientSummary>> ListAsync(CancellationToken ct = default);

    Task<Client?> FindAsync(long id, CancellationToken ct = default);

    Task<ClientSummary?> SummaryAsync(long id, CancellationToken ct = default);

    Task<long> SaveAsync(Client input, long userId, CancellationToken ct = default);

    /// <summary>يعطّل عميلاً أو يعيده. ولا يُحذف: مستنداته دليل.</summary>
    Task SetActiveAsync(long id, bool active, long userId, CancellationToken ct = default);

    Task<List<User>> AssignedUsersAsync(long clientId, CancellationToken ct = default);

    Task<List<User>> UnassignedUsersAsync(long clientId, CancellationToken ct = default);

    Task AssignAsync(long clientId, long userId, long byUserId, CancellationToken ct = default);

    Task UnassignAsync(long clientId, long userId, CancellationToken ct = default);
}

/// <summary>
/// إدارة عملائنا: صفحةُ كلٍّ منهم، ومن يعمل عليه، وما له من أوصال.
///
/// والعدّادات في القائمة تُحسب هنا دفعةً واحدة لا صفاً صفاً: عشرة عملاء
/// بستة أرقامٍ لكلٍّ منهم تعني ستين استعلاماً لصفحةٍ واحدة.
/// </summary>
public sealed class ClientService : IClientService
{
    private readonly MainDbContext _db;
    private readonly ILogger<ClientService> _log;

    public ClientService(MainDbContext db, ILogger<ClientService> log)
    {
        _db = db;
        _log = log;
    }

    public async Task<List<ClientSummary>> ListAsync(CancellationToken ct = default)
    {
        var clients = await _db.Clients.Where(c => !c.Deleted)
            .OrderByDescending(c => c.IsActive).ThenBy(c => c.Name)
            .ToListAsync(ct);

        if (clients.Count == 0) return new List<ClientSummary>();

        var ids = clients.Select(c => c.Id).ToList();

        var connections = await _db.OdooConnections
            .Where(o => !o.Deleted && o.ClientId != null && ids.Contains(o.ClientId!.Value))
            .ToListAsync(ct);

        var users = await _db.ClientUsers
            .Where(u => !u.Deleted && ids.Contains(u.ClientId))
            .GroupBy(u => u.ClientId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);

        // العدّ على اسم القاعدة لا على معرّف العميل: المستندات والربطات
        // وكشف العملاء موسومةٌ بالقاعدة، وهي وحدة العزل الفعلية.
        var databases = connections.Select(c => c.Database).Distinct().ToList();

        var documents = await _db.IntakeDocuments
            .Where(d => !d.Deleted && d.OwnerDatabase != null && databases.Contains(d.OwnerDatabase))
            .GroupBy(d => d.OwnerDatabase!)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);

        var mappings = await _db.BranchMappings
            .Where(m => !m.Deleted && m.OdooDatabase != null && databases.Contains(m.OdooDatabase))
            .GroupBy(m => m.OdooDatabase!)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);

        var customers = await _db.OdooCustomers
            .Where(c => !c.Deleted && c.IsActive && databases.Contains(c.Source))
            .GroupBy(c => c.Source)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);

        return clients.Select(client =>
        {
            var mine = connections.Where(o => o.ClientId == client.Id).ToList();
            var active = mine.FirstOrDefault(o => o.IsActive);
            var database = active?.Database;

            return new ClientSummary(
                client,
                users.GetValueOrDefault(client.Id),
                mine.Count,
                active,
                database is null ? 0 : documents.GetValueOrDefault(database),
                database is null ? 0 : mappings.GetValueOrDefault(database),
                database is null ? 0 : customers.GetValueOrDefault(database));
        }).ToList();
    }

    public Task<Client?> FindAsync(long id, CancellationToken ct = default) =>
        _db.Clients.FirstOrDefaultAsync(c => !c.Deleted && c.Id == id, ct);

    public async Task<ClientSummary?> SummaryAsync(long id, CancellationToken ct = default) =>
        (await ListAsync(ct)).FirstOrDefault(s => s.Client.Id == id);

    public async Task<long> SaveAsync(Client input, long userId, CancellationToken ct = default)
    {
        var client = input.Id > 0
            ? await _db.Clients.FirstOrDefaultAsync(c => c.Id == input.Id, ct)
            : null;

        if (client is null)
        {
            client = new Client { AddedBy_UserId = userId };
            _db.Clients.Add(client);
        }

        client.Name = input.Name.Trim();
        client.Code = Blank(input.Code);
        client.ContactName = Blank(input.ContactName);
        client.ContactEmail = Blank(input.ContactEmail);
        client.ContactPhone = Blank(input.ContactPhone);
        client.Note = Blank(input.Note);
        client.ModifiedBy_UserId = userId;
        client.LastModifiedDate = DateTime.Now;

        await _db.SaveChangesAsync(ct);
        return client.Id;
    }

    public async Task SetActiveAsync(long id, bool active, long userId, CancellationToken ct = default)
    {
        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (client is null || client.IsActive == active) return;

        client.IsActive = active;
        client.ModifiedBy_UserId = userId;
        client.LastModifiedDate = DateTime.Now;

        await _db.SaveChangesAsync(ct);

        _log.LogWarning("{State} العميل {Name} بواسطة {User}.",
            active ? "فُعِّل" : "عُطِّل", client.Name, userId);
    }

    public Task<List<User>> AssignedUsersAsync(long clientId, CancellationToken ct = default) =>
        _db.ClientUsers.Where(cu => !cu.Deleted && cu.ClientId == clientId)
            .Select(cu => cu.User!)
            .Where(u => !u.Deleted)
            .OrderBy(u => u.FirstName)
            .ToListAsync(ct);

    public async Task<List<User>> UnassignedUsersAsync(long clientId, CancellationToken ct = default)
    {
        var assigned = await _db.ClientUsers
            .Where(cu => !cu.Deleted && cu.ClientId == clientId)
            .Select(cu => cu.UserId)
            .ToListAsync(ct);

        return await _db.Users
            .Where(u => !u.Deleted && u.IsActive && !assigned.Contains(u.Id))
            .OrderBy(u => u.FirstName)
            .ToListAsync(ct);
    }

    public async Task AssignAsync(long clientId, long userId, long byUserId, CancellationToken ct = default)
    {
        // الإسناد المُلغى يُحيا ولا يُنشأ ثانٍ: الفهرس مفرد، وصفٌّ ثانٍ يُفشل الحفظ.
        var existing = await _db.ClientUsers
            .FirstOrDefaultAsync(cu => cu.ClientId == clientId && cu.UserId == userId, ct);

        if (existing is not null)
        {
            if (!existing.Deleted) return;

            existing.Deleted = false;
            existing.DeleteDate = null;
            existing.ModifiedBy_UserId = byUserId;
        }
        else
        {
            _db.ClientUsers.Add(new ClientUser
            {
                ClientId = clientId, UserId = userId, AddedBy_UserId = byUserId
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task UnassignAsync(long clientId, long userId, CancellationToken ct = default)
    {
        var link = await _db.ClientUsers
            .FirstOrDefaultAsync(cu => cu.ClientId == clientId && cu.UserId == userId && !cu.Deleted, ct);

        if (link is null) return;

        link.Deleted = true;
        link.DeleteDate = DateTime.Now;

        // ومن كان يعمل عليه يُنقل عنه: تركُه مؤشِّراً إلى عميلٍ لم يعد له
        // يجعل شاشته فارغةً بلا سبب ظاهر.
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user?.CurrentClientId == clientId) user.CurrentClientId = null;

        await _db.SaveChangesAsync(ct);
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
