using System.ComponentModel.DataAnnotations.Schema;

namespace YelgLens.Intake.Model.Basic;

/// <summary>
/// أساس كل كيان محفوظ: مفتاح، وتواريخ إنشاء وتعديل، وحذف منطقي.
/// الحذف لا يمحو السجل بل يعلّمه — مستندات الموردين دليلٌ عند الخلاف
/// فلا يجوز أن يزيلها ضغط زر.
/// </summary>
public abstract class BaseEntity<TKey>
{
    protected BaseEntity()
    {
        AddedDate = LastModifiedDate = DateTime.Now;
    }

    [Column(Order = 1)]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public virtual TKey Id { get; set; } = default!;

    public DateTime AddedDate { get; set; }

    public DateTime LastModifiedDate { get; set; }

    public bool Deleted { get; set; }

    public DateTime? DeleteDate { get; set; }
}

/// <summary>كيان يحمل أثر من أنشأه ومن عدّله.</summary>
public abstract class MainBaseEntity : BaseEntity<long>
{
    public long AddedBy_UserId { get; set; }

    public long? ModifiedBy_UserId { get; set; }
}
