using System;
using System.Collections.Generic;

namespace Sandbox.SecBox.Bridge.Dto;

public sealed class ScanReport
{
	public string Target { get; set; }
	public DateTimeOffset StartedAt { get; set; }
	public DateTimeOffset CompletedAt { get; set; }
	public List<Finding> Findings { get; set; } = new();
	public List<RulePackInfo> RulePacksUsed { get; set; } = new();
	public string ScannerVersion { get; set; }
	public int ProtocolVersion { get; set; }
	public Decision Overall { get; set; } = Decision.NotReviewed;
}
