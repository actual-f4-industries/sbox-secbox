namespace Sandbox.SecBox.Bridge.Dto;

// Mirror of Secbox.Contracts.Finding for JSON deserialization. Nullable
// strings match the source record (extra fields are tolerated; missing
// optional fields default to null).
public sealed class Finding
{
	public Severity Severity { get; set; }
	public string RuleId { get; set; }
	public string Message { get; set; }
	public string Location { get; set; }
	public string Evidence { get; set; }
	public string FixHint { get; set; }
	public string FinderId { get; set; }

	public override string ToString() =>
		$"[{Severity}] {RuleId} @ {Location}: {Message}";
}
