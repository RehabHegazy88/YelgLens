using Microsoft.Extensions.Logging;
using YelgLens.Intake.Model.Settings;

namespace YelgLens.Intake.BLL.Services.Odoo;

public interface IOdooConnectionTester
{
    /// <summary>يجرّب وصلاً بعينه ويعيد وصف ما وجده — أو سبب فشله.</summary>
    Task<(bool Success, string Message)> TestAsync(
        OdooConnection connection, string apiKey, CancellationToken ct = default);
}

/// <summary>
/// يجرّب وصل أودو قبل الانتقال إليه.
///
/// ولا يكتفي بتسجيل الدخول: يقرأ عدد العملاء والمنتجات أيضاً. قاعدةٌ صحيحة
/// الاسم والمفتاح قد تكون فارغةً أو غير القاعدة المقصودة، والعدد هو ما يكشف
/// ذلك قبل أن تُرحَّل عليها أول ورقة — وقد وقع فعلاً أن قاعدة الاختبار لم
/// تكن نسخةً من الحيّة كما كان يُظنّ.
/// </summary>
public sealed class OdooConnectionTester : IOdooConnectionTester
{
    private readonly IHttpClientFactory _clients;
    private readonly ILogger<OdooClient> _clientLog;

    public OdooConnectionTester(IHttpClientFactory clients, ILogger<OdooClient> clientLog)
    {
        _clients = clients;
        _clientLog = clientLog;
    }

    public async Task<(bool Success, string Message)> TestAsync(
        OdooConnection connection, string apiKey, CancellationToken ct = default)
    {
        var settings = new OdooSettings
        {
            Url = connection.Url,
            Database = connection.Database,
            ServiceUser = connection.ServiceUser,
            ApiKey = apiKey,
            // التجربة لا تكتب مهما كانت صلاحيات الوصل: غرضها أن تُطمئن، لا أن
            // تترك أثراً في قاعدةٍ لم يُنتقل إليها بعد.
            AllowWrites = false,
            TimeoutSeconds = connection.TimeoutSeconds,
            RetryCount = 1
        };

        var client = OdooClient.For(
            _clients.CreateClient(nameof(OdooConnectionTester)), settings, _clientLog);

        try
        {
            var version = await client.PingAsync(ct);

            var partners = await client.SearchCountAsync("res.partner",
                new object[] { new object[] { "active", "=", true } }, ct);

            var products = await client.SearchCountAsync("product.product",
                Array.Empty<object>(), ct);

            var orders = await client.SearchCountAsync("sale.order",
                new object[] { new object[] { "state", "=", "draft" } }, ct);

            return (true, $"{version} · عملاء {partners} · منتجات {products} · عروض أسعار مسوّدة {orders}");
        }
        catch (OdooException ex)
        {
            return (false, ex.Message);
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
