using System.Collections.Generic;

namespace Sandbox.SecBox.Bridge.Dto;

public sealed class ApiInfo
{
	public int ProtocolVersion { get; set; }
	public string ScannerVersion { get; set; }
	public List<string> AvailableFinders { get; set; } = new();
	public List<RulePackInfo> AvailableRulePacks { get; set; } = new();
	public string BuildDate { get; set; }
}
