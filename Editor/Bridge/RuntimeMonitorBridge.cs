using System;
using System.Reflection;
using System.Text.Json;
using Sandbox.SecBox.Bridge.Dto;

namespace Sandbox.SecBox.Bridge;

// Reflective access to the BridgeProtocol v2 runtime-monitoring methods on
// Secbox.Core.SecboxApi. Mirrors SecboxCoreClient's pattern - methods are
// resolved lazily on first use, signatures are pinned by name.
//
// The adapter passes its own Action<string> sink to AttachRuntimeSensors;
// every AttributedFinding produced inside Secbox.Core flows through that
// delegate. Action<string> is in System, so the delegate type is identical
// across the editor's default ALC and the secbox-core ALC.
public static class RuntimeMonitorBridge
{
	const string ApiClassName = "Secbox.Core.SecboxApi";

	static MethodInfo _attach, _detach, _status;

	static readonly JsonSerializerOptions JsonOpts = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
	};

	static void EnsureResolved()
	{
		if (_attach != null && _detach != null && _status != null) return;
		var asm = SecboxCoreLoader.CoreAssembly
			?? throw new InvalidOperationException("Secbox.Core not loaded - call EnsureReadyAsync first.");
		var t = asm.GetType(ApiClassName)
			?? throw new InvalidOperationException($"{ApiClassName} not found.");

		_attach = t.GetMethod("AttachRuntimeSensors", new[] { typeof(string), typeof(Action<string>) });
		_detach = t.GetMethod("DetachRuntimeSensors", Type.EmptyTypes);
		_status = t.GetMethod("GetSensorStatus", Type.EmptyTypes);

		if (_attach == null || _detach == null || _status == null)
			throw new InvalidOperationException(
				"Loaded Secbox.Core does not expose runtime-monitoring methods. " +
				"BridgeProtocol v2 required (core version is too old).");
	}

	public static RuntimeSensorAttachResult Attach(RuntimeSensorOptions options, Action<string> eventSink)
	{
		EnsureResolved();
		var json = JsonSerializer.Serialize(options, JsonOpts);
		var resultJson = (string)_attach.Invoke(null, new object[] { json, eventSink });
		return JsonSerializer.Deserialize<RuntimeSensorAttachResult>(resultJson, JsonOpts)
			?? new RuntimeSensorAttachResult { Attached = false, Message = "(unparseable response)" };
	}

	public static void Detach()
	{
		EnsureResolved();
		_detach.Invoke(null, null);
	}

	public static System.Collections.Generic.List<SensorStatusInfo> GetStatus()
	{
		EnsureResolved();
		var json = (string)_status.Invoke(null, null);
		return JsonSerializer.Deserialize<System.Collections.Generic.List<SensorStatusInfo>>(json, JsonOpts)
			?? new System.Collections.Generic.List<SensorStatusInfo>();
	}
}
