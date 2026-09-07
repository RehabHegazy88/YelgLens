using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using YelgLens.Intake.BLL.Services.Export;
using YelgLens.Intake.BLL.Services.Extraction;
using YelgLens.Intake.BLL.Services.Pipeline;
using YelgLens.Intake.BLL.IRepository.Core;
using YelgLens.Intake.BLL.Repository.Core;
using YelgLens.Intake.BLL.Services.Publishing;
using YelgLens.Intake.BLL.Services.Mapping;
using Microsoft.Extensions.Options;
using YelgLens.Intake.BLL.Services.Odoo;
using YelgLens.Intake.BLL.Services.Review;
using YelgLens.Intake.BLL.Services.Validation;
using YelgLens.Intake.DAL.Data;
using YelgLens.Intake.Model.Auth;
using YelgLens.Intake.PL.Infrastructure;

// التواريخ في المشروع كلها DateTime.Now بتوقيت محلي، وهي تُقرأ وتُعرض محلية.
// Npgsql يربط DateTime افتراضياً بـ timestamptz ويرفض ما ليس UTC، فيُرَدّ إلى
// timestamp بلا منطقة — وهو عين ما كان عليه datetime2 في SQL Server، فلا يتغيّر
// معنى عمودٍ واحد ولا قيمةُ صفٍّ قائم. يُضبط قبل فتح أي وصلة.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);

// ---------- الوصول إلى البيانات ----------
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

builder.Services.AddDbContext<MainDbContext>(o => o.UseNpgsql(connectionString));

builder.Services.AddScoped<IUnitOfWork<MainDbContext>>(ctx =>
    new EFUnitOfWork<MainDbContext>(ctx.GetRequiredService<MainDbContext>()));

// ---------- مفاتيح حماية البيانات ----------
// بها تُشفَّر كعكة الدخول ورمز الـ antiforgery. ومستقرّها الافتراضي مجلد
// المستخدم، وهو محجوبٌ عن الخدمة على الخادم بـ ProtectHome. فلو تُركت هناك
// وُلّدت مفاتيح جديدة عند كل إقلاع: يخرج كل داخلٍ من جلسته وترمي الاستمارات
// خطأ antiforgery — عطبٌ لا يظهر إلا بعد إعادة تشغيل، فيُبحث عن سببه بعيداً.
// يُضبط المسار في Keys:Path، وإن غاب فمجلدٌ جنب المحتوى.
var keyRing = builder.Configuration["Keys:Path"];
if (string.IsNullOrWhiteSpace(keyRing))
    keyRing = Path.Combine(builder.Environment.ContentRootPath, "keys");
Directory.CreateDirectory(keyRing);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keyRing))
    .SetApplicationName("YelgLens.Intake");

// ---------- الهوية والصلاحيات ----------
builder.Services.AddAntiforgery(o => o.HeaderName = "XSRF-TOKEN");

builder.Services.AddIdentity<User, Role>(opt =>
{
    opt.Password.RequiredLength = 8;
    opt.Password.RequireNonAlphanumeric = true;
    opt.Password.RequireUppercase = true;
    opt.User.RequireUniqueEmail = true;

    // لا خادم بريد مهيّأ، والحسابات ينشئها مدير لا يسجّلها أصحابها.
    // تفعيل التأكيد قبل تهيئة البريد يقفل الدخول على الجميع.
    opt.SignIn.RequireConfirmedEmail = false;

    opt.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromHours(8);
    opt.Lockout.MaxFailedAccessAttempts = 5;
    opt.Lockout.AllowedForNewUsers = true;
})
.AddEntityFrameworkStores<MainDbContext>()
.AddClaimsPrincipalFactory<PermissionClaimsFactory>()
.AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/Account/Login";
    o.LogoutPath = "/Account/Logout";
    o.AccessDeniedPath = "/Account/AccessDenied";
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.SlidingExpiration = true;
});

var claim = PermissionClaimsFactory.PermissionClaimType;

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(PermissionCodes.DocumentUpload,  p => p.RequireClaim(claim, PermissionCodes.DocumentUpload))
    .AddPolicy(PermissionCodes.DocumentReview,  p => p.RequireClaim(claim, PermissionCodes.DocumentReview))
    .AddPolicy(PermissionCodes.DocumentApprove, p => p.RequireClaim(claim, PermissionCodes.DocumentApprove))
    .AddPolicy(PermissionCodes.DocumentExport,  p => p.RequireClaim(claim, PermissionCodes.DocumentExport))
    .AddPolicy(PermissionCodes.OdooPublish,     p => p.RequireClaim(claim, PermissionCodes.OdooPublish))
    .AddPolicy(PermissionCodes.UserManage,      p => p.RequireClaim(claim, PermissionCodes.UserManage));

// ---------- العرض ----------
builder.Services.AddRazorPages(options =>
{
    // الأصل المنع، والاستثناء صفحات الحساب. الصفحة التي تُضاف غداً تكون محمية
    // بحكم الأصل، لا بحكم أن أحداً تذكّر حمايتها.
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToFolder("/Account");
    options.Conventions.AuthorizePage("/Index", PermissionCodes.DocumentUpload);
});

builder.Services.AddMemoryCache();

// ---------- خدمات الاستقبال ----------
builder.Services.AddSingleton<DocumentClassifier>();
builder.Services.AddSingleton<PdfTableExtractor>();
builder.Services.AddSingleton<ExcelTableExtractor>();
builder.Services.AddSingleton<SpreadsheetPreview>();
builder.Services.AddSingleton<OrderValidator>();
builder.Services.AddSingleton<ExcelExporter>();
builder.Services.AddSingleton<OdooImportExporter>();

// محرك التعرّف الضوئي يُختار من الإعدادات. إن طُلب محرك ونقصت ملفات لغته،
// يسقط التسجيل إلى غير المهيّأ ويُسجَّل السبب — ولا يُترك التطبيق يدّعي قدرةً
// ثم يفشل عند أول مستند.
var ocrSettings = builder.Configuration.GetSection("Ocr").Get<OcrSettings>() ?? new OcrSettings();
builder.Services.AddSingleton(ocrSettings);

builder.Services.AddSingleton<IOcrEngine>(sp =>
{
    var factory = sp.GetRequiredService<ILoggerFactory>();
    var log = factory.CreateLogger("Ocr");

    // المحلي أولاً: مجاني ولا يُخرج المستند من الجهاز.
    IOcrEngine local = new UnconfiguredOcrEngine();
    if (string.Equals(ocrSettings.Engine, "tesseract", StringComparison.OrdinalIgnoreCase))
    {
        var tesseract = new TesseractOcrEngine(ocrSettings, factory.CreateLogger<TesseractOcrEngine>());
        if (tesseract.IsAvailable) local = tesseract;
        else
            log.LogWarning(
                "طُلب محرك tesseract وملفات اللغة ناقصة ({Missing}) في {Path} — يُعطَّل المحلي.",
                string.Join(", ", tesseract.MissingLanguages()), ocrSettings.TessDataPath);
    }

    // البديل السحابي: يُستدعى فقط حين لا يخرج المحلي ببند.
    var claude = new ClaudeOcrEngine(ocrSettings.Claude, factory.CreateLogger<ClaudeOcrEngine>());
    if (!claude.IsAvailable)
    {
        if (ocrSettings.Claude.Enabled) log.LogWarning("البديل السحابي معطّل: {Reason}", claude.UnavailableReason);
        return local;
    }

    log.LogInformation("البديل السحابي مفعّل بنموذج {Model}.", ocrSettings.Claude.Model);
    return new FallbackOcrEngine(local, claude, factory.CreateLogger<FallbackOcrEngine>());
});
builder.Services.AddSingleton<ImageExtractor>();

// الترحيل لأودو: الواجهة قائمة والمنفذ غير مهيّأ.
builder.Services.AddScoped<IErpPublisher, OdooXmlRpcPublisher>();

builder.Services.AddScoped<IntakePipeline>();
builder.Services.AddScoped<IIntakeDocumentRepository, IntakeDocumentRepository>();
builder.Services.AddScoped<IIntakeReviewService, IntakeReviewService>();
builder.Services.AddScoped<IBranchMappingService, BranchMappingService>();
builder.Services.AddScoped<IOdooCustomerService, OdooCustomerService>();

// أودو: الإعدادات من الملف، والمفتاح من البيئة إن لم يكن فيه.
builder.Services.Configure<OdooSettings>(builder.Configuration.GetSection(OdooSettings.SectionName));
builder.Services.PostConfigure<OdooSettings>(settings =>
{
    if (string.IsNullOrWhiteSpace(settings.ApiKey))
        settings.ApiKey = Environment.GetEnvironmentVariable("ODOO_API_KEY") ?? "";
});

// الوصل المحفوظ في قاعدة البيانات يعلو على ملف الإعدادات، ويُقرأ لكل طلب —
// فتبديل القاعدة من الشاشة يسري فوراً بلا إعادة تشغيل. ولذلك يقرأ عملاء أودو
// الإعدادات بـ IOptionsSnapshot لا IOptions.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<YelgLens.Intake.BLL.Services.Tenancy.IClientContext,
    YelgLens.Intake.BLL.Services.Tenancy.ClientContext>();
builder.Services.AddScoped<YelgLens.Intake.BLL.Services.Tenancy.IClientService,
    YelgLens.Intake.BLL.Services.Tenancy.ClientService>();
builder.Services.AddScoped<IOdooConnectionStore, OdooConnectionStore>();
builder.Services.AddScoped<IConfigureOptions<OdooSettings>, OdooSettingsFromDatabase>();
builder.Services.AddScoped<IOdooConnectionTester, OdooConnectionTester>();
builder.Services.AddScoped<IMappingAudit, MappingAudit>();

builder.Services.AddHttpClient<IOdooClient, OdooClient>();
builder.Services.AddScoped<IOdooPreflight, OdooPreflight>();
builder.Services.AddScoped<IOdooOrderPublisher, OdooOrderPublisher>();
builder.Services.AddScoped<IOdooQuotationReader, OdooQuotationReader>();
builder.Services.AddScoped<IOdooProductReader, OdooProductReader>();
builder.Services.AddScoped<IOdooAttachmentService, OdooAttachmentService>();
builder.Services.AddScoped<ISourceArchivist, SourceArchivist>();
builder.Services.AddScoped<ICustomerGate, CustomerGate>();
builder.Services.AddScoped<ILineVerifier, LineVerifier>();
builder.Services.AddScoped<IIntakeDashboard, IntakeDashboard>();

builder.Services.Configure<YelgLens.Intake.BLL.Services.Storage.StorageSettings>(
    builder.Configuration.GetSection("Storage"));
builder.Services.AddSingleton<YelgLens.Intake.BLL.Services.Storage.IImageCompressor,
    YelgLens.Intake.BLL.Services.Storage.ImageCompressor>();

builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 30 * 1024 * 1024; // ٣٠ ميجابايت
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();

Directory.CreateDirectory(Path.Combine(app.Environment.ContentRootPath, "Storage"));

using (var scope = app.Services.CreateScope())
{
    var log = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Seed");
    await DbSeeder.SeedAsync(scope.ServiceProvider, app.Configuration, log);
}

app.Run();
