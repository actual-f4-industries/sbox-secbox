using System;
using Sandbox.SecBox.Bridge.Dto;

namespace Sandbox.SecBox;

// One trust record. Identity is (PackageIdent, Version, ContentHash). Any of
// those changing means the user must re-review. ContentHash is the
// authoritative identity — version bumps without content changes are still
// trusted, content changes without a version bump still re-prompt.
public sealed class TrustEntry
{
	public string PackageIdent { get; set; }
	public string Version { get; set; }
	public string ContentHash { get; set; }
	public Decision Decision { get; set; } = Decision.NotReviewed;
	public DateTime ReviewedAt { get; set; }

	public int CriticalCount { get; set; }
	public int HighCount { get; set; }
	public int MediumCount { get; set; }
	public int LowCount { get; set; }

	public string Notes { get; set; }
}
