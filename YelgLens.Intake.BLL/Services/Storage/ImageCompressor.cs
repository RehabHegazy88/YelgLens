using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace YelgLens.Intake.BLL.Services.Storage;

/// <summary>إعدادات ضغط الصور. تُقرأ من قسم "Storage".</summary>
public sealed class StorageSettings
{
    /// <summary>هل تُضغط الصور المرفوعة؟</summary>
    public bool CompressImages { get; set; } = true;

    /// <summary>جودة JPEG من مئة.</summary>
    public int JpegQuality { get; set; } = 85;

    /// <summary>
    /// أطول ضلعٍ يُسمح به. صفرٌ يعني ألا تُصغَّر الصورة أبداً.
    ///
    /// والصفر هو الافتراضي عن قصد: التصغير يمحو البكسلات التي يُقرأ منها
    /// الباركود، والضغط وحده يكفي.
    /// </summary>
    public int MaxDimension { get; set; }

    /// <summary>لا تُمسّ الملفات الأصغر من هذا الحد.</summary>
    public int MinBytes { get; set; } = 200 * 1024;

    /// <summary>لا يُقبل الناتج إلا إن وفّر هذه النسبة على الأقل، من مئة.</summary>
    public int MinSavingPercent { get; set; } = 15;
}

/// <summary>ما جرى لملفٍ عُرض على الضاغط.</summary>
public sealed record CompressionOutcome(
    bool Compressed, byte[] Content, string FileName, long Before, long After, string Reason)
{
    public long Saved => Before - After;

    public int SavedPercent => Before == 0 ? 0 : (int)(100 * Saved / Before);
}

public interface IImageCompressor
{
    bool Enabled { get; }

    /// <summary>هل هذا الامتداد صورةً نقطية يمكن ضغطها؟</summary>
    bool Handles(string fileName);

    /// <summary>
    /// يضغط الصورة إن كان الضغط يستحق، وإلا أعاد الأصل كما هو.
    ///
    /// ولا يُعاد ناتجٌ أكبر من الأصل ولا ناتجٌ لا يوفّر شيئاً يُذكر: صورةٌ
    /// مضغوطةٌ من قبل تخرج من هنا كما دخلت.
    /// </summary>
    CompressionOutcome Compress(byte[] content, string fileName);
}

/// <summary>
/// يضغط صور المستندات قبل حفظها.
///
/// الصور تسعة أعشار ما نخزّنه — قِيس ٢١٫٤ م.ب من ٢٣ م.ب، وسبعة ملفاتٍ منها
/// وحدها ١٦٫٥ م.ب لأنها محفوظة بجودةٍ شبه معدومة الفقد. وإعادة الترميز عند
/// جودة ٨٥ دون تصغيرٍ للأبعاد توفّر نحو ثلاثة أرباع ذلك.
///
/// والأبعاد لا تُمسّ افتراضاً. الصورة عندنا دليلٌ يُقرأ منه باركودٌ من اثني
/// عشر رقماً، والتصغير يمحو البكسلات التي تُقرأ منها هذه الأرقام — بينما
/// إعادة الترميز تُبقيها. وقد قِيس ذلك على مستندات حقيقية قبل تشغيله.
/// </summary>
public sealed class ImageCompressor : IImageCompressor
{
    private static readonly string[] Raster =
        { ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".webp", ".bmp" };

    private readonly StorageSettings _settings;
    private readonly ILogger<ImageCompressor> _log;

    public ImageCompressor(IOptions<StorageSettings> settings, ILogger<ImageCompressor> log)
    {
        _settings = settings.Value;
        _log = log;
    }

    public bool Enabled => _settings.CompressImages;

    public bool Handles(string fileName) =>
        Raster.Contains(Path.GetExtension(fileName).ToLowerInvariant());

    public CompressionOutcome Compress(byte[] content, string fileName)
    {
        var unchanged = new CompressionOutcome(
            false, content, fileName, content.Length, content.Length, "");

        if (!Enabled) return unchanged with { Reason = "الضغط مغلق في الإعدادات." };
        if (!Handles(fileName)) return unchanged with { Reason = "ليس صورة." };

        if (content.Length < _settings.MinBytes)
            return unchanged with { Reason = "أصغر من حدّ الضغط." };

        try
        {
            using var original = SKBitmap.Decode(content);

            if (original is null)
                return unchanged with { Reason = "تعذّر فكّ الصورة، فتُركت كما هي." };

            using var prepared = Resize(original);

            // الشفافية تُسطَّح على أبيض: JPEG لا يحملها، وبدون التسطيح تخرج
            // المناطق الشفافة سوداء فيضيع نصفُ الورقة.
            using var flattened = Flatten(prepared ?? original);
            using var image = SKImage.FromBitmap(flattened);
            using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, _settings.JpegQuality);

            if (encoded is null)
                return unchanged with { Reason = "تعذّر ترميز الصورة، فتُركت كما هي." };

            var bytes = encoded.ToArray();
            var saved = 100L * (content.Length - bytes.Length) / content.Length;

            // ناتجٌ لا يوفّر شيئاً يُذكر لا يستحق أن يحلّ محلّ الأصل: كل إعادة
            // ترميزٍ تفقد شيئاً، فلا تُدفع الخسارة بلا مقابل.
            if (saved < _settings.MinSavingPercent)
                return unchanged with { Reason = $"الضغط لا يوفّر إلا {saved}%، فتُرك الأصل." };

            var name = Path.GetFileNameWithoutExtension(fileName) + ".jpeg";

            _log.LogInformation("ضُغطت {Name}: {Before} ← {After} بايت ({Saved}%).",
                fileName, content.Length, bytes.Length, saved);

            return new CompressionOutcome(true, bytes, name, content.Length, bytes.Length,
                $"ضُغطت من {content.Length / 1024} ك.ب إلى {bytes.Length / 1024} ك.ب ({saved}%).");
        }
        catch (Exception ex)
        {
            // الضغط تحسينٌ لا شرط: تعثّره يُبقي الأصل ولا يُفشل الرفع.
            _log.LogWarning(ex, "تعذّر ضغط {Name}، فحُفظت كما هي.", fileName);
            return unchanged with { Reason = "تعثّر الضغط، فحُفظت الصورة كما هي." };
        }
    }

    /// <summary>يصغّر الصورة إن تجاوزت الحد. ولا يكبّرها أبداً.</summary>
    private SKBitmap? Resize(SKBitmap source)
    {
        var limit = _settings.MaxDimension;
        var longest = Math.Max(source.Width, source.Height);

        if (limit <= 0 || longest <= limit) return null;

        var ratio = (double)limit / longest;
        var target = new SKImageInfo(
            (int)Math.Round(source.Width * ratio),
            (int)Math.Round(source.Height * ratio));

        return source.Resize(target, SKFilterQuality.High);
    }

    private static SKBitmap Flatten(SKBitmap source)
    {
        if (source.AlphaType == SKAlphaType.Opaque) return source.Copy();

        var flat = new SKBitmap(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);

        using var canvas = new SKCanvas(flat);
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(source, 0, 0);

        return flat;
    }
}
