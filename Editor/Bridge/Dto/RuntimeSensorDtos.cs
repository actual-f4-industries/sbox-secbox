using System.Collections.Generic;

namespace Sandbox.SecBox.Bridge.Dto;

// Mirror DTOs for the runtime-monitoring bridge (BridgeProtocol v2). The
// editor adapter cannot reference Secbox.Core types directly — the engine
// regenerates the adapter's csproj on save and would strip custom refs —
// so we re-declare the shapes locally and rely on JSON tolerance.

public sealed class RuntimeSensorOptions
{
	public bool EnableProfiler { get; set; } = true;
	public bool EnableEtw { get; set; } = false;
	public bool EnableManagedHook { get; set; } = true;
	// Bitmask, mirrors Secbox.Core.RuntimeSensors.SensorCapabilities.
	public int DesiredCapabilities { get; set; } = 0x7E; // managed+dyn+file+proc+net+nativeimg
	public bool CaptureStack { get; set; } = false;
	public List<string> PathAllowlist { get; set; }
	public EnforcementPolicyDto Enforcement { get; set; }
}

// Mirror of Secbox.Core.RuntimeSensors.EnforcementPolicy.
public sealed class EnforcementPolicyDto
{
	public bool BlockLibraryProcessStart { get; set; } = false;
}

public sealed class RuntimeSensorAttachResult
{
	public bool Attached { get; set; }
	public string Message { get; set; }
	public List<SensorStatusInfo> Sensors { get; set; } = new();
}

public sealed class SensorStatusInfo
{
	public string Id { get; set; }
	public string Status { get; set; }
	public int Capabilities { get; set; }
	public string LastError { get; set; }
}

// Wire shape of one AttributedFinding event delivered via the eventSink
// callback. The adapter consumes these as raw JSON strings — it does not
// strictly need to deserialize, but the structured DTO is here for UI use.
public sealed class RuntimeFinding
{
	public long Sequence { get; set; }
	public string Timestamp { get; set; }
	public string Kind { get; set; }
	public string Severity { get; set; }
	public List<string> SensorIds { get; set; } = new();
	public int Pid { get; set; }
	public int Tid { get; set; }
	public string Target { get; set; }
	public string CallerAssembly { get; set; }
	public string CallerMethod { get; set; }
	public string Note { get; set; }
}
