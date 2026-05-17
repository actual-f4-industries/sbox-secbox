namespace Sandbox.SecBox;

public sealed record Finding(
	Severity Severity,
	string RuleId,
	string Message,
	string Location
)
{
	public override string ToString() => $"[{Severity}] {RuleId} @ {Location}: {Message}";
}
