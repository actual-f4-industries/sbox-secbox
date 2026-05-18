using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sandbox.SecBox.Bridge;

// User-global secbox configuration. Persisted to
// %LOCALAPPDATA%/secbox/config.json. Hand-editable; menu items also write
// here. Distinct from the per-project TrustStore (which holds package
// decisions) — this controls bridge / loader behaviour.
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
	// Off by default — every caught exception in the entire editor process
	// would land in the log, flooding it. Turn on only when chasing a specific
	// "why did this throw silently" bug.
	public bool VerboseDiagnostics { get; set; } = false;

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
