using System;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Sandbox.SecBox.Bridge.Dto;

namespace Sandbox.SecBox.Bridge;

// Reflective invocation of Secbox.Core.SecboxApi. Method names + signatures
// are pinned by Secbox.Contracts.BridgeProtocol (mirrored here as constants
// since we can't reference Contracts at compile time).
public static class SecboxCoreClient
{
	const string ApiClassName = "Secbox.Core.SecboxApi";

	static readonly JsonSerializerOptions JsonOpts = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		Converters = { new JsonStringEnumConverter() },
		PropertyNameCaseInsensitive = true,
	};

	static MethodInfo _getInfo, _scanFolder, _scanAssembly, _scanSource;

	public static async Task EnsureReadyAsync()
	{
		// Short-circuit if already wired - repeat callers (RuntimeMonitor
		// fires once per assembly load) don't need to re-do the handshake.
		if (_getInfo != null && _scanFolder != null && _scanAssembly != null && _scanSource != null)
			return;

		DiagnosticsLog.Trace("SecboxCoreClient.EnsureReadyAsync: begin");
		var asm = await SecboxCoreLoader.EnsureLoadedAsync();
		var type = asm.GetType(ApiClassName);
		if (type == null)
		{
			DiagnosticsLog.Error($"{ApiClassName} not found in loaded core");
			throw new InvalidOperationException($"{ApiClassName} not found in loaded Secbox.Core.");
		}

		_getInfo      = type.GetMethod("GetInfo",      Type.EmptyTypes);
		_scanFolder   = type.GetMethod("ScanFolder",   new[] { typeof(string), typeof(string) });
		_scanAssembly = type.GetMethod("ScanAssembly", new[] { typeof(string), typeof(string) });
		_scanSource   = type.GetMethod("ScanSource",   new[] { typeof(string), typeof(string) });

		if (_getInfo == null || _scanFolder == null || _scanAssembly == null || _scanSource == null)
		{
			DiagnosticsLog.Error($"{ApiClassName} missing expected methods - protocol skew");
			throw new InvalidOperationException(
				$"{ApiClassName} is missing one or more expected methods (signature mismatch / protocol skew).");
		}

		ApiInfo info;
		try { info = GetInfo(); }
		catch (Exception ex)
		{
			DiagnosticsLog.Error("GetInfo handshake threw", ex);
			throw;
		}

		if (info.ProtocolVersion != CorePolicy.RequiredProtocolVersion)
		{
			DiagnosticsLog.Error($"protocol mismatch: adapter v{CorePolicy.RequiredProtocolVersion}, core v{info.ProtocolVersion}");
			throw new InvalidOperationException(
				$"Bridge protocol mismatch. Adapter expects v{CorePolicy.RequiredProtocolVersion}, "
				+ $"loaded core reports v{info.ProtocolVersion}.");
		}

		DiagnosticsLog.Info($"handshake ok: core v{info.ScannerVersion}, finders={string.Join(",", info.AvailableFinders)}, packs={info.AvailableRulePacks.Count}");
	}

	public static ApiInfo GetInfo()
	{
		var json = (string)_getInfo.Invoke(null, null);
		return JsonSerializer.Deserialize<ApiInfo>(json, JsonOpts);
	}

	public static ScanReport ScanFolder(string folderPath, string optionsJson = null)
	{
		DiagnosticsLog.Trace($"ScanFolder: {folderPath}");
		return Deserialize((string)_scanFolder.Invoke(null, new object[] { folderPath, optionsJson }));
	}

	public static ScanReport ScanAssembly(string dllPath, string optionsJson = null)
	{
		DiagnosticsLog.Trace($"ScanAssembly: {dllPath}");
		return Deserialize((string)_scanAssembly.Invoke(null, new object[] { dllPath, optionsJson }));
	}

	public static ScanReport ScanSource(string sourcePath, string optionsJson = null)
	{
		DiagnosticsLog.Trace($"ScanSource: {sourcePath}");
		return Deserialize((string)_scanSource.Invoke(null, new object[] { sourcePath, optionsJson }));
	}

	static ScanReport Deserialize(string json)
		=> JsonSerializer.Deserialize<ScanReport>(json, JsonOpts) ?? new ScanReport();
}
