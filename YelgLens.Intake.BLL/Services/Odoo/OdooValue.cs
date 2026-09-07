using System.Text.Json;
using System.Text.Json.Nodes;

namespace YelgLens.Intake.BLL.Services.Odoo;

/// <summary>
/// يقرأ حقول أودو على ما هي عليه لا على ما نتمناه.
///
/// أودو لا يكتب <c>null</c> للحقل الفارغ بل <c>false</c> — وهذا في كل نوع:
/// النص الفارغ <c>false</c>، والعلاقة الفارغة <c>false</c>، والتاريخ الفارغ
/// <c>false</c>. وقراءة ذلك بـ<c>GetValue&lt;string&gt;()</c> ترمي استثناءً
/// يوقف العملية كلها لأن حقلاً واحداً كان فارغاً — وقد وقع هذا فعلاً على
/// <c>barcode</c> وهو فارغ في كل منتجاتهم.
/// </summary>
public static class OdooValue
{
    /// <summary>نصُّ الحقل، أو عدمٌ إن كان فارغاً — والفارغ عندهم <c>false</c>.</summary>
    public static string? Text(JsonNode? node)
    {
        if (node is not JsonValue value) return null;

        return value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>() is { Length: > 0 } text ? text : null,
            JsonValueKind.Number => value.ToString(),
            _ => null
        };
    }

    /// <summary>
    /// الطرف الثاني من علاقةٍ يردّها أودو مصفوفةً <c>[معرّف، اسم]</c>، أو عدمٌ
    /// إن كانت العلاقة فارغة فردّها <c>false</c>.
    /// </summary>
    public static string? RelationName(JsonNode? node) =>
        node is JsonArray { Count: 2 } pair ? Text(pair[1]) : null;

    public static long? RelationId(JsonNode? node) =>
        node is JsonArray { Count: 2 } pair && pair[0] is JsonValue id
        && id.GetValueKind() == JsonValueKind.Number
            ? id.GetValue<long>()
            : null;
}
