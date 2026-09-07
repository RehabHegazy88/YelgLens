using SkiaSharp;

namespace YelgLens.Intake.BLL.Services.Extraction;

/// <summary>نطاق أفقي يمثّل عموداً واحداً من الجدول.</summary>
public sealed record ColumnBand(int Left, int Right)
{
    public int Width => Right - Left;
}

/// <summary>صفحة مهيّأة للتعرّف الضوئي: صورة ثنائية اللون ونطاقات أعمدتها.</summary>
public sealed class PreparedPage : IDisposable
{
    public required SKBitmap Image { get; init; }
    public required IReadOnlyList<ColumnBand> Columns { get; init; }
    public void Dispose() => Image.Dispose();
}

/// <summary>
/// تجهيز الصورة قبل التعرّف الضوئي. ثلاث خطوات، كل واحدة تعالج عطباً
/// لوحظ على مستندات حقيقية مصوّرة بالهاتف:
///
/// ١. التكبير: الحرف المطبوع في صورة صفحة كاملة لا يتجاوز عشر بكسلات،
///    ومحركات التعرّف الضوئي تحتاج ثلاثة أضعاف ذلك.
///
/// ٢. عتبة موضعية لا عامة: المستند يجمع خلايا بيضاء وأخرى مظللة بالرمادي.
///    العتبة العامة تحوّل العمود الرمادي كله إلى سواد فتبتلع أرقامه.
///    العتبة الموضعية تقارن كل بكسل بجيرانه فينجو العمودان معاً.
///
/// ٣. عزل الأعمدة: محرك التعرّف الضوئي يحلل تخطيط الصفحة بنفسه، وعمود
///    الكميات ضيق وملاصق لخط الجدول، فيُقرأ بقعةً واحدة معه ويُهمل.
///    تمرير كل عمود وحده يغنيه عن تحليل التخطيط أصلاً.
/// </summary>
public static class ImagePreprocessor
{
    /// <summary>معامل التباين في معادلة Sauvola.</summary>
    private const double SauvolaK = 0.2;

    /// <summary>مدى الانحراف المعياري المتوقع لصورة ثمانية البتات.</summary>
    private const double SauvolaRange = 128.0;

    /// <summary>
    /// سقف البكسلات التي تُعالَج. الصور التكاملية تحجز ثمانية بايتات لكل بكسل
    /// مرتين، فصورة هاتف حديثة مكبّرة ثلاثاً تتجاوز نصف جيجابايت. والصورة
    /// عالية الدقة لا تحتاج التكبير أصلاً، فالتقليص هنا لا يفقد شيئاً.
    /// </summary>
    private const long MaximumWorkingPixels = 12_000_000;

    public static PreparedPage Prepare(string filePath, int upscale, int window = 0)
    {
        using var source = SKBitmap.Decode(filePath)
            ?? throw new InvalidOperationException("تعذّر فتح الصورة.");

        var effective = EffectiveUpscale(source.Width, source.Height, upscale);

        var scaled = effective <= 1
            ? source.Copy()
            : source.Resize(
                new SKImageInfo(source.Width * effective, source.Height * effective),
                SKFilterQuality.High);

        if (scaled is null)
            throw new InvalidOperationException("تعذّر تكبير الصورة.");

        try
        {
            var width = scaled.Width;
            var height = scaled.Height;

            var ink = Binarize(scaled, width, height, window);

            // خطوط الجدول تُستبعد عند حساب الأعمدة فقط. الخط الرأسي يمتد على
            // كامل الارتفاع، فلو بقي لبدت الصفحة كلها عموداً واحداً متصلاً.
            var forLayout = (bool[])ink.Clone();
            EraseRules(forLayout, width, height);

            return new PreparedPage
            {
                Image = ToBitmap(ink, width, height),
                Columns = DetectColumns(forLayout, width, height)
            };
        }
        finally
        {
            scaled.Dispose();
        }
    }

    /// <summary>يخفض معامل التكبير حتى تدخل النتيجة تحت سقف البكسلات.</summary>
    private static int EffectiveUpscale(int width, int height, int requested)
    {
        var factor = Math.Max(1, requested);
        while (factor > 1 && (long)width * height * factor * factor > MaximumWorkingPixels)
            factor--;
        return factor;
    }

    /// <summary>
    /// عتبة Sauvola الموضعية: عتبة كل بكسل تُحسب من متوسط جيرانه وانحرافهم،
    /// فتصمد أمام الظل المتدرّج على الورقة المصوّرة وأمام الخلايا المظللة.
    /// </summary>
    private static bool[] Binarize(SKBitmap bitmap, int width, int height, int requested)
    {
        var pixels = bitmap.Pixels;
        var gray = new byte[width * height];
        for (var i = 0; i < gray.Length; i++)
            gray[i] = (byte)Math.Clamp(
                0.299 * pixels[i].Red + 0.587 * pixels[i].Green + 0.114 * pixels[i].Blue, 0, 255);

        // النافذة تُقاس بحجم الحرف لا بحجم الصفحة. ربطها بارتفاع الصورة يجعلها
        // تتسع في الصفحة الكاملة حتى تعمل عمل العتبة العامة فتبتلع النص الباهت،
        // وهو ما كان يفسد قراءة الأكواد في الصور الكاملة دون المقصوصة.
        var window = requested > 0
            ? requested | 1
            : Math.Clamp((height / 6) | 1, 25, 81) | 1;
        var radius = window / 2;

        // صور تكاملية: تجعل حساب متوسط أي نافذة عمليةً ثابتة الكلفة.
        var stride = width + 1;
        var sum = new double[stride * (height + 1)];
        var squares = new double[stride * (height + 1)];

        for (var y = 1; y <= height; y++)
            for (var x = 1; x <= width; x++)
            {
                double v = gray[(y - 1) * width + (x - 1)];
                sum[y * stride + x] = v + sum[(y - 1) * stride + x]
                    + sum[y * stride + x - 1] - sum[(y - 1) * stride + x - 1];
                squares[y * stride + x] = v * v + squares[(y - 1) * stride + x]
                    + squares[y * stride + x - 1] - squares[(y - 1) * stride + x - 1];
            }

        var ink = new bool[width * height];

        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var x0 = Math.Max(0, x - radius);
                var y0 = Math.Max(0, y - radius);
                var x1 = Math.Min(width - 1, x + radius);
                var y1 = Math.Min(height - 1, y + radius);

                var a = y0 * stride + x0;
                var b = y0 * stride + x1 + 1;
                var c = (y1 + 1) * stride + x0;
                var d = (y1 + 1) * stride + x1 + 1;

                double count = (x1 - x0 + 1) * (y1 - y0 + 1);
                var mean = (sum[d] - sum[b] - sum[c] + sum[a]) / count;
                var variance = Math.Max(0,
                    (squares[d] - squares[b] - squares[c] + squares[a]) / count - mean * mean);

                var threshold = mean * (1 + SauvolaK * (Math.Sqrt(variance) / SauvolaRange - 1));
                ink[y * width + x] = gray[y * width + x] <= threshold;
            }

        return ink;
    }

    /// <summary>يمحو الخطوط الممتدة على نصف الصفحة فأكثر — أفقيةً كانت أو رأسية.</summary>
    private static void EraseRules(bool[] ink, int width, int height)
    {
        var minVertical = (int)(height * 0.5);
        var minHorizontal = (int)(width * 0.5);

        for (var x = 0; x < width; x++)
        {
            var run = 0;
            for (var y = 0; y <= height; y++)
            {
                if (y < height && ink[y * width + x]) { run++; continue; }
                if (run >= minVertical)
                    for (var back = y - run; back < y; back++) ink[back * width + x] = false;
                run = 0;
            }
        }

        for (var y = 0; y < height; y++)
        {
            var run = 0;
            for (var x = 0; x <= width; x++)
            {
                if (x < width && ink[y * width + x]) { run++; continue; }
                if (run >= minHorizontal)
                    for (var back = x - run; back < x; back++) ink[y * width + back] = false;
                run = 0;
            }
        }
    }

    /// <summary>
    /// الأعمدة تُكتشف من الإسقاط الرأسي: العمود عمودٌ من الحبر، والفاصل بينها
    /// فراغ عريض. الفراغ الضيق داخل الكلمة الواحدة لا يُعد فاصلاً.
    /// </summary>
    private static List<ColumnBand> DetectColumns(bool[] ink, int width, int height)
    {
        var density = new int[width];
        for (var x = 0; x < width; x++)
        {
            var count = 0;
            for (var y = 0; y < height; y++)
                if (ink[y * width + x]) count++;
            density[x] = count;
        }

        // العتبة تُقاس نسبةً إلى الارتفاع: عمود نصٍّ حقيقي يحمل حبراً في
        // جزء معتبر من ارتفاعه، أما البكسلات المتناثرة فضوضاء تصوير.
        var inkFloor = Math.Max(2, (int)(height * 0.02));
        var minGap = Math.Max(6, (int)(width * 0.012));

        var bands = new List<ColumnBand>();
        var start = -1;
        var gap = 0;

        for (var x = 0; x <= width; x++)
        {
            var hasInk = x < width && density[x] > inkFloor;

            if (hasInk)
            {
                if (start < 0) start = x;
                gap = 0;
                continue;
            }

            if (start < 0) continue;

            gap++;
            if (gap < minGap && x < width) continue;

            bands.Add(new ColumnBand(start, x - gap));
            start = -1;
            gap = 0;
        }

        var minWidth = Math.Max(8, (int)(width * 0.008));
        return bands.Where(b => b.Width >= minWidth).ToList();
    }

    private static SKBitmap ToBitmap(bool[] ink, int width, int height)
    {
        var bitmap = new SKBitmap(width, height);
        var pixels = new SKColor[width * height];
        for (var i = 0; i < pixels.Length; i++)
        {
            var v = ink[i] ? (byte)0 : (byte)255;
            pixels[i] = new SKColor(v, v, v);
        }
        bitmap.Pixels = pixels;
        return bitmap;
    }
}
