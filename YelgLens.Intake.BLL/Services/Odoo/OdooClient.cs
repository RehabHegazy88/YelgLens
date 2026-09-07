using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace YelgLens.Intake.BLL.Services.Odoo;

/// <summary>خطأٌ ردّه أودو نفسه — يُميَّز عن خطأ الشبكة لأن علاجهما مختلف.</summary>
public sealed class OdooException : Exception
{
    public OdooException(string message, string? detail = null) : base(message) => Detail = detail;

    public string? Detail { get; }
}

public interface IOdooClient
{
    bool IsConfigured { get; }

    /// <summary>اسم القاعدة المتصل بها — يُوسم به كل ما يُقرأ منها.</summary>
    string Database { get; }

    /// <summary>يتحقق من الاتصال والهوية، ويعيد وصفاً لما وجده.</summary>
    Task<string> PingAsync(CancellationToken ct = default);

    /// <summary>يقرأ سجلات نموذج بشرطٍ وحقول. القراءة وحدها لا تحتاج إذناً خاصاً.</summary>
    Task<List<JsonObject>> SearchReadAsync(
        string model,
        object[] domain,
        string[] fields,
        int offset = 0,
        int limit = 0,
        string? order = null,
        CancellationToken ct = default);

    Task<int> SearchCountAsync(string model, object[] domain, CancellationToken ct = default);

    /// <summary>يجمع في الخادم: يعيد صفاً لكل مجموعة ومعه عدّادها.</summary>
    Task<List<JsonObject>> ReadGroupAsync(
        string model, object[] domain, string[] fields, string[] groupBy, CancellationToken ct = default);

    /// <summary>هل فُتح الإذن بالكتابة؟ الافتراض المنع.</summary>
    bool CanWrite { get; }

    /// <summary>
    /// ينشئ سجلاً ويعيد معرّفه. لا يعمل ما لم يُفتح <c>AllowWrites</c> صراحةً.
    /// </summary>
    Task<long> CreateAsync(string model, JsonObject values, CancellationToken ct = default);

    /// <summary>يقرأ حقولاً من سجلاتٍ بمعرّفاتها.</summary>
    Task<List<JsonObject>> ReadAsync(string model, long[] ids, string[] fields, CancellationToken ct = default);

    /// <summary>
    /// يعدّل سجلاً قائماً. لا يعمل ما لم يُفتح <c>AllowWrites</c> صراحةً.
    /// </summary>
    Task WriteAsync(string model, long id, JsonObject values, CancellationToken ct = default);
}

/// <summary>
/// موصّل أودو عبر JSON-RPC.
///
/// اختير JSON-RPC على XML-RPC لأن .NET يقرأ JSON بلا مكتبة، وXML-RPC يلزمه
/// ترميزٌ يدوي لكل نوع — وشيفرةٌ تُكتب باليد لبروتوكولٍ قديم مصدر أخطاء لا
/// تُكتشف إلا في الإنتاج.
///
/// والكتابة فيه مغلقة بالافتراض: تمرّ من <see cref="CreateAsync"/> وحدها،
/// وترفض ما لم يُفتح <c>AllowWrites</c> في الإعدادات — لأن أمر بيعٍ يُنشأ في
/// دفاتر شركة فعلٌ لا رجعة فيه بضغطة.
/// </summary>
public class OdooClient : IOdooClient
{
    private readonly HttpClient _http;
    private readonly OdooSettings _settings;
    private readonly ILogger<OdooClient> _log;

    private int? _uid;

    public OdooClient(HttpClient http, IOptionsSnapshot<OdooSettings> settings, ILogger<OdooClient> log)
        : this(http, settings.Value, log) { }

    /// <summary>
    /// يبني عميلاً على إعداداتٍ بعينها لا على المعمول به.
    ///
    /// يلزم هذا لتجربة وصلٍ قبل تفعيله: التجربة على الوصل المفعَّل تقول إن
    /// القائم يعمل، ولا تقول شيئاً عن الذي نهمّ بالانتقال إليه.
    ///
    /// ويُبنى بدالةٍ ساكنة لا بمُنشئٍ عام: مُنشئان يقبلان
    /// <c>(HttpClient, ?, ILogger)</c> يجعلان حاوية الخدمات تعجز عن اختيار
    /// أيهما فيسقط كل نداءٍ لأودو. وقد سقط فعلاً.
    /// </summary>
    public static OdooClient For(HttpClient http, OdooSettings settings, ILogger<OdooClient> log) =>
        new(http, settings, log);

    private OdooClient(HttpClient http, OdooSettings settings, ILogger<OdooClient> log)
    {
        _settings = settings;
        _log = log;
        _http = http;

        if (_settings.IsConfigured)
        {
            _http.BaseAddress = new Uri(_settings.Url.TrimEnd('/') + "/");
            _http.Timeout = TimeSpan.FromSeconds(_settings.TimeoutSeconds);
        }
    }

    public bool IsConfigured => _settings.IsConfigured;

    public string Database => _settings.Database;

    public async Task<string> PingAsync(CancellationToken ct = default)
    {
        var version = await CallAsync("common", "version", Array.Empty<object>(), ct);
        var uid = await LoginAsync(ct);

        var serie = version?["server_serie"]?.GetValue<string>() ?? "?";
        return $"أودو {serie} — {_settings.Describe()} (uid {uid})";
    }

    public async Task<List<JsonObject>> SearchReadAsync(
        string model,
        object[] domain,
        string[] fields,
        int offset = 0,
        int limit = 0,
        string? order = null,
        CancellationToken ct = default)
    {
        var uid = await LoginAsync(ct);

        var options = new JsonObject
        {
            ["fields"] = JsonSerializer.SerializeToNode(fields),
            ["offset"] = offset
        };

        if (limit > 0) options["limit"] = limit;
        if (order is not null) options["order"] = order;

        var result = await ExecuteAsync(uid, model, "search_read",
            new object[] { new object[] { domain } }, options, ct);

        return result is JsonArray array
            ? array.OfType<JsonObject>().ToList()
            : new List<JsonObject>();
    }

    public async Task<int> SearchCountAsync(string model, object[] domain, CancellationToken ct = default)
    {
        var uid = await LoginAsync(ct);

        var result = await ExecuteAsync(uid, model, "search_count",
            new object[] { new object[] { domain } }, null, ct);

        return result?.GetValue<int>() ?? 0;
    }

    public bool CanWrite => _settings.IsConfigured && _settings.AllowWrites;

    /// <summary>
    /// الكتابة تمرّ من بابٍ واحد يفحص الإذن قبلها.
    ///
    /// القراءة تُخطئ فتُعرض قائمةٌ قديمة، والكتابة تُخطئ فيُنشأ أمر بيعٍ في
    /// دفاتر شركة. فلا تُفتح إلا بقرارٍ مكتوب في الإعدادات، ويُقال هنا صراحةً
    /// حين تكون مغلقة بدل أن يُظنّ العطبُ في الشبكة.
    /// </summary>
    public async Task<long> CreateAsync(string model, JsonObject values, CancellationToken ct = default)
    {
        if (!_settings.AllowWrites)
            throw new OdooException(
                "الكتابة في أودو مغلقة. تُفتح من «إعدادات أودو» بتعليم «اسمح بالكتابة في أودو» "
                + "وعلى قاعدة اختبارٍ أولاً.");

        var uid = await LoginAsync(ct);

        var result = await ExecuteAsync(uid, model, "create",
            new object[] { new object[] { values } }, null, ct);

        var id = result?.GetValueKind() == JsonValueKind.Number ? result.GetValue<long>() : 0;

        if (id == 0)
            throw new OdooException($"أنشأ أودو السجل في {model} ولم يُعِد معرّفه.");

        _log.LogInformation("أُنشئ سجل في {Model} بالمعرّف {Id} على قاعدة {Database}.",
            model, id, _settings.Database);

        return id;
    }

    /// <summary>
    /// يعدّل سجلاً قائماً في أودو.
    ///
    /// يمرّ من بوابة الإذن نفسها التي يمرّ منها الإنشاء: تعديل أمر بيعٍ في
    /// دفاتر شركة ليس أهون من إنشائه.
    /// </summary>
    public async Task WriteAsync(string model, long id, JsonObject values, CancellationToken ct = default)
    {
        if (!_settings.AllowWrites)
            throw new OdooException(
                "الكتابة في أودو مغلقة. تُفتح من «إعدادات أودو» بتعليم «اسمح بالكتابة في أودو» "
                + "وعلى قاعدة اختبارٍ أولاً.");

        var uid = await LoginAsync(ct);

        await ExecuteAsync(uid, model, "write",
            new object[] { new object[] { new object[] { id }, values } }, null, ct);

        _log.LogInformation("عُدِّل السجل {Id} في {Model} على قاعدة {Database}.",
            id, model, _settings.Database);
    }

    public async Task<List<JsonObject>> ReadAsync(
        string model, long[] ids, string[] fields, CancellationToken ct = default)
    {
        if (ids.Length == 0) return new List<JsonObject>();

        var uid = await LoginAsync(ct);

        var options = new JsonObject { ["fields"] = JsonSerializer.SerializeToNode(fields) };

        var result = await ExecuteAsync(uid, model, "read",
            new object[] { new object[] { ids } }, options, ct);

        return result is JsonArray array ? array.OfType<JsonObject>().ToList() : new List<JsonObject>();
    }

    public async Task<List<JsonObject>> ReadGroupAsync(
        string model, object[] domain, string[] fields, string[] groupBy, CancellationToken ct = default)
    {
        var uid = await LoginAsync(ct);

        var options = new JsonObject
        {
            ["fields"] = JsonSerializer.SerializeToNode(fields),
            ["groupby"] = JsonSerializer.SerializeToNode(groupBy),
            ["lazy"] = false
        };

        var result = await ExecuteAsync(uid, model, "read_group",
            new object[] { new object[] { domain } }, options, ct);

        return result is JsonArray array ? array.OfType<JsonObject>().ToList() : new List<JsonObject>();
    }

    /// <summary>
    /// الهوية تُطلب مرة وتُحفظ لعمر الطلب: كل نداء يعيد تسجيل الدخول يضاعف
    /// الرحلات على خادمٍ بطيء أصلاً.
    /// </summary>
    private async Task<int> LoginAsync(CancellationToken ct)
    {
        if (_uid is { } cached) return cached;

        if (!_settings.IsConfigured)
            throw new OdooException("إعدادات أودو ناقصة. اضبط العنوان وقاعدة البيانات والمستخدم والمفتاح.");

        var result = await CallAsync("common", "login",
            new object[] { _settings.Database, _settings.ServiceUser, _settings.ApiKey }, ct);

        // أودو يردّ false لا خطأً حين تفشل الهوية، فيُترجَم هنا إلى خطأ مفهوم.
        var uid = result is null || result.GetValueKind() == JsonValueKind.False
            ? 0
            : result.GetValue<int>();

        if (uid == 0)
            throw new OdooException(
                $"رُفضت الهوية على قاعدة «{_settings.Database}». "
                + "تأكد أن المفتاح يخصّ هذا المستخدم وهذه القاعدة — المفتاح الواحد لا يعمل على كل القواعد.");

        _uid = uid;
        return uid;
    }

    private Task<JsonNode?> ExecuteAsync(
        int uid, string model, string method, object[] args, JsonObject? kwargs, CancellationToken ct)
    {
        var payload = new List<object>
        {
            _settings.Database, uid, _settings.ApiKey, model, method
        };

        payload.AddRange(args);
        if (kwargs is not null) payload.Add(kwargs);

        return CallAsync("object", "execute_kw", payload.ToArray(), ct);
    }

    private async Task<JsonNode?> CallAsync(string service, string method, object[] args, CancellationToken ct)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "call",
            ["params"] = new JsonObject
            {
                ["service"] = service,
                ["method"] = method,
                ["args"] = JsonSerializer.SerializeToNode(args)
            },
            ["id"] = 1
        };

        // خادمهم يتقلّب: النداء الواحد يعود في ثوانٍ مرة، ولا يردّ خمس دقائق
        // مرة. قِيس هذا لا افتُرض. فتُعاد المحاولة بمهلٍ متزايدة بدل أن تفشل
        // مزامنةٌ كاملة لأن نداءً واحداً صادف الخادم مشغولاً.
        HttpResponseMessage? response = null;
        Exception? last = null;

        for (var attempt = 1; attempt <= _settings.RetryCount; attempt++)
        {
            try
            {
                response = await _http.PostAsJsonAsync("jsonrpc", request, ct);
                break;
            }
            catch (Exception ex) when (ex is TaskCanceledException or HttpRequestException
                                       && !ct.IsCancellationRequested)
            {
                last = ex;

                if (attempt == _settings.RetryCount) break;

                var wait = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                _log.LogWarning("تعذّر نداء أودو {Service}.{Method} (محاولة {Attempt} من {Total}). "
                                + "إعادة بعد {Wait} ثانية.",
                    service, method, attempt, _settings.RetryCount, wait.TotalSeconds);

                await Task.Delay(wait, ct);
            }
        }

        if (response is null)
            throw new OdooException(
                last is TaskCanceledException
                    ? $"لم يردّ خادم أودو خلال {_settings.TimeoutSeconds} ثانية في "
                      + $"{_settings.RetryCount} محاولات. الخادم بطيء أو متوقف — أعد المحاولة لاحقاً."
                    : $"تعذّر الوصول إلى خادم أودو: {last?.Message}");

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct)
                   ?? throw new OdooException("ردّ أودو بجسمٍ فارغ.");

        if (body["error"] is JsonObject error)
        {
            // رسالة أودو مفيدة، وأثر الاستدعاء طويلٌ لا يُعرض للمستخدم لكنه
            // يُسجَّل: بلا الأثر لا يُعرف أي حقلٍ رفضه الخادم.
            var message = error["data"]?["message"]?.GetValue<string>()
                          ?? error["message"]?.GetValue<string>()
                          ?? "خطأ غير معروف من أودو.";

            var trace = error["data"]?["debug"]?.GetValue<string>();
            _log.LogWarning("أودو ردّ بخطأ على {Service}.{Method}: {Message}\n{Trace}",
                service, method, message, trace);

            throw new OdooException(message, trace);
        }

        return body["result"];
    }
}
