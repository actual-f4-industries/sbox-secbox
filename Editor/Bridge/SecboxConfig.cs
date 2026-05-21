using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sandbox.SecBox.Bridge;

// User-global secbox configuration. Persisted to
// %LOCALAPPDATA%/secbox/config.json. Hand-editable; menu items also write
// here. Distinct from the per-project TrustStore (which holds package
// decisions) - this controls bridge / loader behaviour.
public sealed class SecboxConfig
{
	static readonly JsonSerializerOptions JsonOpts = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		Converters = { new JsonStringEnumConverter() },
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	// When true, SecboxCoreLoader skips download + SHA-256 verification and
	// loads Secbox.Core from DevPath (or CorePolicy.DevDefaultPath if blank).
	public bool DevMode { get; set; } = false;

	// Optional override path. If null/empty + DevMode is true, the loader
	// uses CorePolicy.DevDefaultPath ("%LOCALAPPDATA%/secbox/core/dev").
	public string DevPath { get; set; }

	// Allow CorePolicy to fetch + cache new Secbox.Core versions on demand.
	// Off = adapter only uses what's already in cache; useful for offline /
	// audit-locked machines.
	public bool AutoUpdate { get; set; } = true;

	// Enable per-exception trace logging via AppDomain.FirstChanceException.
	// Off by default - every caught exception in the entire editor process
	// would land in the log, flooding it. Turn on only when chasing a specific
	// "why did this throw silently" bug.
	public bool VerboseDiagnostics { get; set; } = false;

	// Runtime monitoring (Tier E managed-call enforcement). Force-enabled -
	// installs no drivers, no services; just attaches the in-editor Harmony
	// hook that intercepts library Process.Start. Always on: this getter is a
	// constant, so a "runtimeMonitoringEnabled": false in config.json is ignored
	// (no setter for STJ to write into). To stop the hook for one session use
	// secbox > Runtime Monitoring > Detach Now; it re-attaches on next boot.
	public bool RuntimeMonitoringEnabled => true;

	// Tier E enforcement - when a library-attributed Process.Start is
	// detected, refuse to let it run. The Harmony prefix in ManagedCallSensor
	// returns false; the original Process.Start never executes; library code
	// sees a null result (and usually NREs at the next member access).
	//
	// Force-enabled: active prevention is always on. Like RuntimeMonitoringEnabled
	// this getter is a constant, so a "blockLibraryProcessStart": false in
	// config.json is ignored (no setter for STJ to write into).
	public bool BlockLibraryProcessStart => true;

	// Pop a modal dialog whenever a Critical-severity finding arrives.
	// On by default - the whole point of Critical is "user must see this".
	// Disable for headless / batch use where attended UI isn't practical.
	public bool ShowDetectionDialog { get; set; } = true;

	// Welcome dialogue - when true, suppress the first-install welcome
	// across every project this user opens. Set by the "don't show again"
	// checkbox in the welcome dialogue. Per-install suppression is handled
	// separately by a marker file at <libraryRoot>/.welcome-shown, which
	// vanishes automatically when the user uninstalls the library.
	public bool WelcomeDialogueDismissedGlobally { get; set; } = false;

	public static string FilePath =>
		Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"secbox", "config.json");

	public static SecboxConfig Load()
	{
		try
		{
			if (File.Exists(FilePath))
			{
				var json = File.ReadAllText(FilePath);
				return JsonSerializer.Deserialize<SecboxConfig>(json, JsonOpts) ?? new SecboxConfig();
			}
		}
		catch (Exception ex)
		{
			global::Sandbox.Internal.GlobalGameNamespace.Log.Warning(
				$"[secbox] config unreadable, using defaults: {ex.Message}");
		}
		return new SecboxConfig();
	}

	public void Save()
	{
		try
		{
			var dir = Path.GetDirectoryName(FilePath);
			if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
				Directory.CreateDirectory(dir);
			File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
		}
		catch (Exception ex)
		{
			global::Sandbox.Internal.GlobalGameNamespace.Log.Error(
				$"[secbox] could not save config: {ex.Message}");
		}
	}
}
