using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using SkiaSharp;

namespace YelgLens.Intake.BLL.Services.Extraction;

/// <summary>إعدادات بديل التعرّف الضوئي السحابي.</summary>
public sealed class ClaudeOcrSettings
{
    public bool Enabled { get; set; }

    public string Model { get; set; } = "claude-opus-5";

    /// <summary>
    /// يُترك فارغاً عادةً. المفتاح يُقرأ من متغير البيئة ANTHROPIC_API_KEY،
    /// فلا يدخل في ملف إعدادات قد يُدفع إلى مستودع.
    /// </summary>
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// مساحة العمل التي يعمل فيها الطلب. تلزم حين يكون المفتاح مرتبطاً بهوية
    /// (identity-linked)، وبدونها ترد الخدمة بخطأ صريح يطلبها.
    /// تُقرأ من هنا أو من متغير البيئة ANTHROPIC_WORKSPACE_ID.
    /// </summary>
    public string WorkspaceId { get; set; } = "";

    /// <summary>أطول ضلع تُرسل به الصورة. التكبير فوق هذا لا يزيد دقة القراءة ويزيد الكلفة.</summary>
    public int MaxEdgePixels { get; set; } = 1568;
}

/// <summary>
/// بديل التعرّف الضوئي حين يعجز المحرك المحلي.
///
/// المحرك المحلي يقرأ الأرقام اللاتينية جيداً ويعجز عن أمرين: الوصف العربي
/// (لا تتوفر بيانات لغته)، والصفحة الكاملة المصوّرة من بعيد. هذا البديل يعالج
/// الاثنين، لكنه يُرسل صورة المستند خارج الجهاز — فلا يُستدعى إلا بعد فشل
/// المحلي، وبتفعيل صريح في الإعدادات.
///
/// القيم العائدة منه احتمالية كغيرها من التعرّف الضوئي، ولا تُعامل معاملة اليقين.
/// </summary>
public sealed class ClaudeOcrEngine : IOcrEngine
{
    private const string Instruction = """
        You are reading a supplier purchase-order or delivery-note table from a
        photographed or scanned document. Extract ONE entry per physical table row.

        Rules:
        - "code" is the item barcode or article number as printed (digits only).
        - "description" is the item description exactly as printed. Keep Arabic
          text in Arabic; do not translate or transliterate.
        - "quantity" is the ordered quantity for that row, or null if absent.
        - "confidence" is your own certainty for that row, 0.0 to 1.0.
        - Skip header rows, totals, footers, and signature blocks.
        - Do NOT invent rows. If a value is unreadable, omit the row rather than
          guessing. A missing row is recoverable; a wrong number is not.
        """;

    private readonly ClaudeOcrSettings _settings;
    private readonly ILogger<ClaudeOcrEngine> _log;

    public ClaudeOcrEngine(ClaudeOcrSettings settings, ILogger<ClaudeOcrEngine> log)
    {
        _settings = settings;
        _log = log;
    }

    private AnthropicClient? _client;

    /// <summary>
    /// العميل يُبنى مرة واحدة. إنشاء HttpClient لكل نداء يستنزف المنافذ،
    /// والإعدادات لا تتغير أثناء التشغيل.
    /// </summary>
    private AnthropicClient Client(string key)
    {
        if (_client is not null) return _client;

        var workspace = ResolveWorkspace();

        if (workspace is null)
        {
            _client = new AnthropicClient { ApiKey = key };
            return _client;
        }

        var http = new HttpClient(new WorkspaceHeaderHandler(workspace))
        {
            // القراءة الضوئية لصورة كاملة قد تتجاوز المهلة الافتراضية.
            Timeout = TimeSpan.FromMinutes(5)
        };

        _client = new AnthropicClient { ApiKey = key, HttpClient = http };
        return _client;
    }

    /// <summary>
    /// يضيف ترويسة مساحة العمل إلى كل طلب. المفتاح المرتبط بهوية يلزمه تعيينها
    /// صراحةً، والـ SDK لا يعرضها كخيار، فتُحقن عند النقل.
    /// </summary>
    private sealed class WorkspaceHeaderHandler(string workspaceId)
        : DelegatingHandler(new HttpClientHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            request.Headers.Remove("anthropic-workspace-id");
            request.Headers.Add("anthropic-workspace-id", workspaceId);
            return base.SendAsync(request, ct);
        }
    }

    private string? ResolveWorkspace()
    {
        if (!string.IsNullOrWhiteSpace(_settings.WorkspaceId)) return _settings.WorkspaceId;

        var fromEnvironment = Environment.GetEnvironmentVariable("ANTHROPIC_WORKSPACE_ID");
        return string.IsNullOrWhiteSpace(fromEnvironment) ? null : fromEnvironment;
    }

    private string? ResolveKey()
    {
        if (!string.IsNullOrWhiteSpace(_settings.ApiKey)) return _settings.ApiKey;

        var fromEnvironment = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        return string.IsNullOrWhiteSpace(fromEnvironment) ? null : fromEnvironment;
    }

    public bool IsAvailable => _settings.Enabled && ResolveKey() is not null;

    /// <summary>سبب التعطّل — للرسالة التي تُعرض على المستخدم.</summary>
    public string UnavailableReason =>
        !_settings.Enabled
            ? "البديل السحابي غير مفعّل في الإعدادات."
            : "لم يُعثر على مفتاح ANTHROPIC_API_KEY في متغيرات البيئة.";

    public async Task<IReadOnlyList<OcrLine>> ReadTableAsync(
        string imagePath, CancellationToken ct = default)
    {
        var key = ResolveKey();
        if (key is null) return Array.Empty<OcrLine>();

        var (bytes, mediaType) = PrepareImage(imagePath);

        Message response;
        try
        {
            response = await Send(Client(key), bytes, mediaType, ct);
        }
        catch (Exception ex)
        {
            // الفشل هنا لا يُبلَع: المحلي عجز أصلاً، فلو ابتُلع هذا أيضاً ظهر
            // المستند كأنه بلا بنود، وهي رسالة تدل على الصورة لا على الخدمة.
            _log.LogError(ex, "فشل نداء البديل السحابي.");

            // الخدمة تسمّي سبب الرفض، لكن اسم الإعداد الذي يعالجه في هذا
            // التطبيق لا تعرفه، فيُضاف هنا حتى لا يبحث عنه المستخدم.
            var hint = ex.Message.Contains("workspace", StringComparison.OrdinalIgnoreCase)
                ? " — المفتاح مرتبط بهوية ويلزمه معرّف مساحة العمل: ضعه في " +
                  "Ocr:Claude:WorkspaceId بملف الإعدادات أو في متغير البيئة " +
                  "ANTHROPIC_WORKSPACE_ID."
                : "";

            throw new InvalidOperationException(
                $"تعذّر نداء البديل السحابي للتعرّف الضوئي: {ex.Message}{hint}", ex);
        }

        // الرفض حالة نجاح على مستوى النقل، فلا بد من فحصها قبل قراءة المحتوى.
        if (response.StopReason == "refusal")
        {
            _log.LogWarning("رفض النموذج قراءة المستند: {Reason}",
                response.StopDetails?.Explanation ?? "بلا تفصيل");
            return Array.Empty<OcrLine>();
        }

        var json = string.Concat(response.Content
            .Select(b => b.Value)
            .OfType<TextBlock>()
            .Select(t => t.Text));

        return Parse(json);
    }

    private Task<Message> Send(
        AnthropicClient client, byte[] bytes, string mediaType, CancellationToken ct) =>
        client.Messages.Create(new MessageCreateParams
        {
            Model = _settings.Model,
            MaxTokens = 16000,
            Thinking = new ThinkingConfigAdaptive(),
            System = Instruction,
            OutputConfig = new OutputConfig { Format = TableFormat() },
            Messages =
            [
                new()
                {
                    Role = Role.User,
                    Content = new List<ContentBlockParam>
                    {
                        new ImageBlockParam
                        {
                            Source = new Base64ImageSource
                            {
                                Data = Convert.ToBase64String(bytes),
                                MediaType = mediaType
                            }
                        },
                        new TextBlockParam { Text = "Extract the item table from this document." }
                    }
                }
            ]
        }, ct);

    /// <summary>
    /// مخطط المخرجات يُفرض على النموذج، فلا يعود بنصٍّ حر يحتاج تخميناً عند
    /// تحليله. الكمية تقبل العدم صراحةً: البند بلا كمية حالة قائمة في هذه
    /// المستندات، والتصريح بها أسلم من إخراج صفر يُقرأ كمية حقيقية.
    ///
    /// لا يحمل المخطط حدّي الثقة (minimum/maximum): المخرجات المهيكلة لا تقبلهما
    /// على الأنواع الرقمية، والحصر يجري عند التحليل في <see cref="Parse"/>.
    /// </summary>
    private const string SchemaJson = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["lines"],
          "properties": {
            "lines": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["code", "description", "quantity", "confidence"],
                "properties": {
                  "code":        { "type": "string" },
                  "description": { "type": "string" },
                  "quantity":    { "type": ["number", "null"] },
                  "confidence":  { "type": "number" }
                }
              }
            }
          }
        }
        """;

    private static JsonOutputFormat TableFormat() => new()
    {
        Schema = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(SchemaJson)!
    };

    private static IReadOnlyList<OcrLine> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<OcrLine>();

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("lines", out var lines))
            return Array.Empty<OcrLine>();

        var result = new List<OcrLine>();

        foreach (var line in lines.EnumerateArray())
        {
            var code = line.TryGetProperty("code", out var c) ? c.GetString() : null;
            if (string.IsNullOrWhiteSpace(code)) continue;

            var description = line.TryGetProperty("description", out var d)
                ? d.GetString() ?? "" : "";

            decimal? quantity = line.TryGetProperty("quantity", out var q)
                && q.ValueKind == JsonValueKind.Number
                    ? q.GetDecimal()
                    : null;

            var confidence = line.TryGetProperty("confidence", out var f)
                && f.ValueKind == JsonValueKind.Number
                    ? Math.Clamp(f.GetDouble(), 0, 1)
                    : 0.5;

            result.Add(new OcrLine(code!, description, quantity, confidence));
        }

        return result;
    }

    /// <summary>
    /// تُصغَّر الصورة إلى الحد الذي يقرأ عنده النموذج بلا زيادة. ما فوقه
    /// يزيد الكلفة والزمن ولا يزيد دقة.
    /// </summary>
    private (byte[] Bytes, string MediaType) PrepareImage(string path)
    {
        using var source = SKBitmap.Decode(path)
            ?? throw new InvalidOperationException("تعذّر فتح الصورة.");

        var longest = Math.Max(source.Width, source.Height);
        SKBitmap prepared;

        if (longest <= _settings.MaxEdgePixels)
        {
            prepared = source.Copy();
        }
        else
        {
            var scale = (double)_settings.MaxEdgePixels / longest;
            prepared = source.Resize(
                new SKImageInfo((int)(source.Width * scale), (int)(source.Height * scale)),
                SKFilterQuality.High);
        }

        try
        {
            using var image = SKImage.FromBitmap(prepared);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            return (encoded.ToArray(), "image/png");
        }
        finally
        {
            prepared.Dispose();
        }
    }
}

/// <summary>
/// يجرّب المحرك المحلي أولاً، ولا ينتقل إلى البديل إلا إذا لم يخرج المحلي ببند.
///
/// الترتيب مقصود: المحلي مجاني ولا يُخرج المستند من الجهاز، والبديل مدفوع
/// ويرسل صورة المستند إلى خدمة خارجية. فلا يُدفع الثمنان إلا عند الحاجة.
///
/// الشرط هو خلوّ النتيجة من البنود، لا نقصانها. المحلي يقرأ الأكواد والكميات
/// ويترك الوصف العربي فارغاً، وهذا يُعدّ نجاحاً فلا يُستدعى البديل بعده —
/// قرارٌ متعمَّد: المطابقة تقوم على الكود وجدول المرادفات لا على نص الوصف،
/// فلا يستحق الوصفُ وحده كلفةَ نداءٍ خارجي لكل مستند.
/// </summary>
public sealed class FallbackOcrEngine : IOcrEngine
{
    private readonly IOcrEngine _primary;
    private readonly IOcrEngine _fallback;
    private readonly ILogger<FallbackOcrEngine> _log;

    public FallbackOcrEngine(IOcrEngine primary, IOcrEngine fallback, ILogger<FallbackOcrEngine> log)
    {
        _primary = primary;
        _fallback = fallback;
        _log = log;
    }

    public bool IsAvailable => _primary.IsAvailable || _fallback.IsAvailable;

    public async Task<IReadOnlyList<OcrLine>> ReadTableAsync(
        string imagePath, CancellationToken ct = default)
    {
        if (_primary.IsAvailable)
        {
            var local = await _primary.ReadTableAsync(imagePath, ct);
            if (local.Count > 0)
            {
                _log.LogInformation("المحرك المحلي قرأ {Count} بند.", local.Count);
                return local;
            }

            _log.LogInformation("المحرك المحلي لم يخرج ببند — يُجرَّب البديل السحابي.");
        }

        if (!_fallback.IsAvailable) return Array.Empty<OcrLine>();

        var remote = await _fallback.ReadTableAsync(imagePath, ct);
        _log.LogInformation("البديل السحابي قرأ {Count} بند.", remote.Count);
        return remote;
    }
}
