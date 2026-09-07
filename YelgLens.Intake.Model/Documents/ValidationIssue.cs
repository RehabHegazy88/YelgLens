using YelgLens.Intake.Model.Enums;

namespace YelgLens.Intake.Model.Documents;

public sealed class ValidationIssue
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public IssueSeverity Severity { get; init; } = IssueSeverity.Review;
    public int? LineSequence { get; init; }
}
