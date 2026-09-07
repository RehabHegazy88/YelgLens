using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using YelgLens.Intake.Model.Documents;
using YelgLens.Intake.Model.Mapping;

namespace YelgLens.Intake.DAL.Maps;

/// <summary>
/// يُخزَّن <see cref="FieldValue{T}"/> كنوع مملوك، فتنزل قيمته ومصدره وثقته
/// ونصه الخام في أعمدة الجدول نفسه. البديل — تخزين القيمة وحدها — كان يفقد
/// ما يميّز هذا النظام: أن كل رقم يحمل من أين جاء وبأي درجة يقين.
/// </summary>
internal static class FieldMapping
{
    public static void OwnText<TEntity>(
        EntityTypeBuilder<TEntity> entity,
        Expression<Func<TEntity, FieldValue<string>>> field,
        string column,
        int maxLength) where TEntity : class
    {
        entity.OwnsOne(field, b =>
        {
            b.Property(v => v.Value).HasColumnName(column).HasMaxLength(maxLength);
            Common(b, column);
        });
    }

    public static void OwnNumber<TEntity>(
        EntityTypeBuilder<TEntity> entity,
        Expression<Func<TEntity, FieldValue<decimal>>> field,
        string column) where TEntity : class
    {
        entity.OwnsOne(field, b =>
        {
            b.Property(v => v.Value).HasColumnName(column).HasPrecision(18, 4);
            Common(b, column);
        });
    }

    public static void OwnDate<TEntity>(
        EntityTypeBuilder<TEntity> entity,
        Expression<Func<TEntity, FieldValue<DateTime>>> field,
        string column) where TEntity : class
    {
        entity.OwnsOne(field, b =>
        {
            b.Property(v => v.Value).HasColumnName(column);
            Common(b, column);
        });
    }

    private static void Common<TOwner, TField>(OwnedNavigationBuilder<TOwner, TField> b, string column)
        where TOwner : class where TField : class
    {
        b.Property("Origin").HasColumnName($"{column}_Origin");
        b.Property("Confidence").HasColumnName($"{column}_Conf");
        b.Property("RawText").HasColumnName($"{column}_Raw").HasMaxLength(400);
        b.Property("HasValue").HasColumnName($"{column}_Has");
    }
}

public sealed class IntakeDocumentMap : IEntityMap
{
    public void Visit(ModelBuilder builder)
    {
        var e = builder.Entity<IntakeDocument>();

        e.ToTable("IntakeDocuments");
        e.Property(d => d.SourceFileName).HasMaxLength(260).IsRequired();
        e.Property(d => d.StoredFileName).HasMaxLength(120).IsRequired();
        e.Property(d => d.SourceSha256).HasMaxLength(64).IsRequired();
        e.Property(d => d.OdooCustomerOverride).HasMaxLength(300);
        e.Property(d => d.OdooOrderName).HasMaxLength(60);
        e.Property(d => d.OdooDatabase).HasMaxLength(100);
        e.Property(d => d.PublishError).HasMaxLength(1000);
        e.Ignore(d => d.IsPublished);
        e.Ignore(d => d.HasLocalFile);
        e.Ignore(d => d.SourceNotSent);
        e.Property(d => d.AttachError).HasMaxLength(1000);
        e.Property(d => d.ReviewNote).HasMaxLength(500);

        // البصمة تُفهرس لأنها مانع التكرار: المندوب يرفع الورقة مرتين، والنظام
        // لا يجوز أن ينشئ منها أوردرين.
        e.HasIndex(d => d.SourceSha256);
        e.HasIndex(d => d.Status);

        FieldMapping.OwnText(e, d => d.CustomerPoNumber, "CustomerPoNumber", 100);
        FieldMapping.OwnText(e, d => d.CustomerName, "CustomerName", 200);
        FieldMapping.OwnText(e, d => d.BranchLabel, "BranchLabel", 200);

        FieldMapping.OwnDate(e, d => d.OrderDate, "OrderDate");
        FieldMapping.OwnDate(e, d => d.DeliveryDate, "DeliveryDate");

        e.Property(d => d.OwnerDatabase).HasMaxLength(100);

        // مفهرس لأنه يدخل في كل استعلام على المستندات بعد اليوم: القوائم
        // ولوحة المتابعة والمراجعة كلها تُقصر على عميلٍ واحد.
        e.HasIndex(d => d.OwnerDatabase);

        e.HasOne(d => d.UploadedBy).WithMany()
         .HasForeignKey(d => d.UploadedByUserId).OnDelete(DeleteBehavior.Restrict);

        e.HasOne(d => d.ReviewedBy).WithMany()
         .HasForeignKey(d => d.ReviewedByUserId).OnDelete(DeleteBehavior.Restrict);

        e.HasOne(d => d.ReopenedBy).WithMany()
         .HasForeignKey(d => d.ReopenedByUserId).OnDelete(DeleteBehavior.Restrict);

        e.HasOne(d => d.PublishedBy).WithMany()
         .HasForeignKey(d => d.PublishedByUserId).OnDelete(DeleteBehavior.Restrict);

        e.HasOne(d => d.ArchivedBy).WithMany()
         .HasForeignKey(d => d.ArchivedByUserId).OnDelete(DeleteBehavior.Restrict);

        e.HasMany(d => d.Lines).WithOne(l => l.IntakeDocument)
         .HasForeignKey(l => l.IntakeDocumentId).OnDelete(DeleteBehavior.Cascade);

        e.HasMany(d => d.Issues).WithOne(i => i.IntakeDocument)
         .HasForeignKey(i => i.IntakeDocumentId).OnDelete(DeleteBehavior.Cascade);

        e.HasMany(d => d.Pages).WithOne(p => p.IntakeDocument)
         .HasForeignKey(p => p.IntakeDocumentId).OnDelete(DeleteBehavior.Cascade);

        // البديل يشير إلى المرفوض. لا حذف متتالٍ: إسقاط الأصل يمحو أثر أنه رُدّ.
        e.HasOne(d => d.Replaces).WithMany()
         .HasForeignKey(d => d.ReplacesDocumentId).OnDelete(DeleteBehavior.Restrict);

        e.Ignore(d => d.HasBlockingIssue);
        e.Ignore(d => d.IsDecided);
    }
}

public sealed class IntakeLineMap : IEntityMap
{
    public void Visit(ModelBuilder builder)
    {
        var e = builder.Entity<IntakeLine>();

        e.ToTable("IntakeLines");
        e.HasIndex(l => new { l.IntakeDocumentId, l.Sequence });

        FieldMapping.OwnText(e, l => l.Barcode, "Barcode", 40);
        FieldMapping.OwnText(e, l => l.SupplierSku, "SupplierSku", 60);
        FieldMapping.OwnText(e, l => l.Description, "Description", 400);

        FieldMapping.OwnNumber(e, l => l.OrderedQty, "OrderedQty");
        FieldMapping.OwnNumber(e, l => l.ReceivedQty, "ReceivedQty");
        FieldMapping.OwnNumber(e, l => l.DocumentUnitPrice, "DocumentUnitPrice");
        FieldMapping.OwnNumber(e, l => l.VatPercent, "VatPercent");

        e.Ignore(l => l.MinConfidence);
        e.Ignore(l => l.ShortfallQty);
    }
}

public sealed class IntakePageMap : IEntityMap
{
    public void Visit(ModelBuilder builder)
    {
        var e = builder.Entity<IntakePage>();

        e.ToTable("IntakePages");
        e.Property(p => p.SourceFileName).HasMaxLength(260).IsRequired();
        e.Property(p => p.StoredFileName).HasMaxLength(120).IsRequired();
        e.Property(p => p.Sha256).HasMaxLength(64).IsRequired();

        // البصمة مفهرسة لأنها مانع التكرار على مستوى الصفحة: المندوب قد يرفع
        // الورقة نفسها ضمن مستند آخر.
        e.HasIndex(p => p.Sha256);
        e.HasIndex(p => new { p.IntakeDocumentId, p.PageNumber });
    }
}

public sealed class IntakeIssueMap : IEntityMap
{
    public void Visit(ModelBuilder builder)
    {
        var e = builder.Entity<IntakeIssue>();

        e.ToTable("IntakeIssues");
        e.Property(i => i.Code).HasMaxLength(60).IsRequired();
        e.Property(i => i.Message).HasMaxLength(600).IsRequired();
        e.HasIndex(i => i.IntakeDocumentId);
    }
}


/// <summary>عملاؤنا — وحدة العزل في النظام كله.</summary>
public sealed class ClientMap : IEntityMap
{
    public void Visit(ModelBuilder builder)
    {
        var e = builder.Entity<YelgLens.Intake.Model.Settings.Client>();

        e.ToTable("Clients");
        e.Property(c => c.Name).HasMaxLength(150).IsRequired();
        e.Property(c => c.Code).HasMaxLength(30);
        e.Property(c => c.ContactName).HasMaxLength(150);
        e.Property(c => c.ContactEmail).HasMaxLength(150);
        e.Property(c => c.ContactPhone).HasMaxLength(50);
        e.Property(c => c.Note).HasMaxLength(600);

        e.HasIndex(c => c.Name).IsUnique();

        e.HasMany(c => c.Connections).WithOne(o => o.Client)
         .HasForeignKey(o => o.ClientId).OnDelete(DeleteBehavior.Restrict);

        e.Ignore(c => c.Display);
    }
}

/// <summary>إسناد المستخدمين إلى العملاء.</summary>
public sealed class ClientUserMap : IEntityMap
{
    public void Visit(ModelBuilder builder)
    {
        var e = builder.Entity<YelgLens.Intake.Model.Settings.ClientUser>();

        e.ToTable("ClientUsers");

        // الإسناد لا يتكرر: صفّان لنفس الاثنين يجعلان الإلغاء يترك أحدهما.
        e.HasIndex(cu => new { cu.ClientId, cu.UserId }).IsUnique();

        e.HasOne(cu => cu.Client).WithMany(c => c.Users)
         .HasForeignKey(cu => cu.ClientId).OnDelete(DeleteBehavior.Cascade);

        e.HasOne(cu => cu.User).WithMany()
         .HasForeignKey(cu => cu.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>
/// أوصال أودو المحفوظة. واحدٌ منها معمولٌ به، والباقي محفوظٌ للرجوع إليه.
/// </summary>
public sealed class OdooConnectionMap : IEntityMap
{
    public void Visit(ModelBuilder builder)
    {
        var e = builder.Entity<YelgLens.Intake.Model.Settings.OdooConnection>();

        e.ToTable("OdooConnections");
        e.Property(c => c.Name).HasMaxLength(80).IsRequired();
        e.Property(c => c.Url).HasMaxLength(300).IsRequired();
        e.Property(c => c.Database).HasMaxLength(100).IsRequired();
        e.Property(c => c.ServiceUser).HasMaxLength(150).IsRequired();
        e.Property(c => c.ApiKeyProtected).HasMaxLength(800);
        e.Property(c => c.Note).HasMaxLength(400);
        e.Property(c => c.LastTestResult).HasMaxLength(500);

        // الاسم مفرد داخل عميله لا في النظام: «إنتاج» اسمٌ مشروع عند كل عميل.
        e.HasIndex(c => new { c.ClientId, c.Name }).IsUnique();

        e.Ignore(c => c.HasKey);
    }
}

public sealed class BranchMappingMap : IEntityMap
{
    public void Visit(ModelBuilder builder)
    {
        var e = builder.Entity<BranchMapping>();

        e.ToTable("BranchMappings");
        e.Property(m => m.SourceLabel).HasMaxLength(300).IsRequired();
        e.Property(m => m.MatchKey).HasMaxLength(300).IsRequired();
        e.Property(m => m.OdooCustomer).HasMaxLength(300);
        e.Property(m => m.OdooInvoiceAddress).HasMaxLength(300);
        e.Property(m => m.OdooDeliveryAddress).HasMaxLength(300);
        e.Property(m => m.OdooPricelist).HasMaxLength(150);
        e.Property(m => m.Note).HasMaxLength(400);

        e.Property(m => m.OdooDatabase).HasMaxLength(100);

        // المفتاح مفهرس لأنه مدخل كل بحث، ومفرد داخل قاعدته لا مطلقاً: ربط
        // النص الواحد بفرعين في القاعدة نفسها يعيد الغموض الذي وُضع الجدول
        // لرفعه، أما نفس النص في قاعدةِ عميلٍ آخر فربطٌ آخر مشروع.
        e.HasIndex(m => new { m.OdooDatabase, m.MatchKey }).IsUnique();

        e.Ignore(m => m.IsComplete);
        e.Ignore(m => m.InvoiceAddressOrCustomer);
        e.Ignore(m => m.DeliveryAddressOrCustomer);
    }
}

/// <summary>
/// كشف عملاء أودو. الاسم مفهرس مفرداً لأنه مفتاح المطابقة عند الاستيراد،
/// ورقم المحل مفهرس لأنه مدخل الاقتراح ويتكرر بطبيعته.
/// </summary>
public sealed class OdooCustomerMap : IEntityMap
{
    public void Visit(ModelBuilder builder)
    {
        var e = builder.Entity<OdooCustomer>();

        e.ToTable("OdooCustomers");
        e.Property(c => c.Name).HasMaxLength(300).IsRequired();
        e.Property(c => c.DisplayName).HasMaxLength(400).IsRequired();
        e.Property(c => c.ParentName).HasMaxLength(200);
        e.Property(c => c.SearchKey).HasMaxLength(300).IsRequired();
        e.Property(c => c.Source).HasMaxLength(100).IsRequired();

        // الاسم مفرد داخل مصدره لا عبر المصادر: القاعدتان قد تحملان الاسم
        // نفسه، ومنعُ تكراره بينهما يجعل مزامنة إحداهما تُفشل الأخرى.
        e.HasIndex(c => new { c.Source, c.Name }).IsUnique();
        e.HasIndex(c => c.DisplayName);
        e.Ignore(c => c.HasParent);
        e.HasIndex(c => c.SearchKey);
        e.HasIndex(c => c.StoreNumber);
    }
}
