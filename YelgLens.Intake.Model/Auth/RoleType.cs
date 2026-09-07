namespace YelgLens.Intake.Model.Auth;

/// <summary>
/// أدوار هذا النظام. الفصل بين المندوب والمراجع ليس تنظيمياً فحسب:
/// قيم التعرّف الضوئي احتمالية، فلا يجوز أن يعتمدها من التقطها.
/// </summary>
public enum RoleType
{
    /// <summary>إدارة النظام والمستخدمين.</summary>
    SystemAdmin,

    /// <summary>المراجع: يفحص المستخرَج ويعتمده أو يرفضه.</summary>
    Auditor,

    /// <summary>المندوب: يرفع صور المستندات من الميدان.</summary>
    Representative,

    /// <summary>اطّلاع فقط بلا تعديل ولا اعتماد.</summary>
    Viewer
}
