namespace Sandbox.SecBox.Bridge.Dto;

/// <summary>
/// Human-readable concern category aggregated from raw findings.
/// Used by the Default tab of the review panel to present
/// actionable, non-technical information to users.
/// </summary>
public sealed class Concern
{
	/// <summary>Internal category key (e.g. "filesystem", "process").</summary>
	public string Category { get; set; }

	/// <summary>Human-readable statement (e.g. "This library wants to read and write files").</summary>
	public string Statement { get; set; }

	/// <summary>Number of findings in this category.</summary>
	public int FindingCount { get; set; }

	/// <summary>Worst severity among all findings in this category.</summary>
	public Severity HighestSeverity { get; set; }

	/// <summary>Contributing rule IDs.</summary>
	public string[] RuleIds { get; set; }

	/// <summary>User's Yes/No selection. Default is "Yes" if Critical/High, "No" otherwise.</summary>
	public bool Selected { get; set; }
}
