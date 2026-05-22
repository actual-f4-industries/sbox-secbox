using System.Collections.Generic;
using System.Linq;
using Sandbox.SecBox.Bridge.Dto;

namespace Sandbox.SecBox.Lifecycle;

// One library's manual-scan outcome, produced by BootAudit.ScanAllLibraries and
// rendered by ScanResultsWindow. Carries enough to drive the per-library card
// and to hand off into the full ReviewDialog (findings + hash).
public sealed class LibraryScanResult
{
	public string PackageIdent { get; set; }
	public string Folder { get; set; }
	public string ContentHash { get; set; }

	// Current trust decision (preserved from the store), not changed by a scan.
	public Decision Decision { get; set; } = Decision.NotReviewed;

	public List<Finding> Findings { get; set; } = new();

	public int CriticalCount { get; set; }
	public int HighCount { get; set; }
	public int MediumCount { get; set; }
	public int LowCount { get; set; }

	// Set when the scan itself failed for this library; Findings is then empty.
	public string Error { get; set; }

	public int TotalFindings => Findings?.Count ?? 0;
	public bool HasError => !string.IsNullOrEmpty(Error);

	public Severity MaxSeverity => Findings == null || Findings.Count == 0
		? Severity.Info
		: Findings.Max(f => f.Severity);
}
