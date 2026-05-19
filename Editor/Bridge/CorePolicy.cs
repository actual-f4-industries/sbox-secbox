using System;
using System.IO;

namespace Sandbox.SecBox.Bridge;

// Pinned configuration for downloading + verifying Secbox.Core.dll.
//
// SHA-256 hashes are baked in at adapter compile time — refusing any
// downloaded blob whose hash doesn't match. To bump to a new core version:
//   1. Push a vX.Y.Z tag — `.github/workflows/release.yml` builds every
//      artifact and attaches them (with a `hashes.txt` manifest) to a new
//      GitHub Release at https://github.com/actual-f4-industries/secbox.
//   2. Copy the SHA-256s from that hashes.txt into CoreFiles below.
//   3. Bump CoreVersion to match the tag (with the leading `v`).
//   4. Re-publish the adapter to the s&box Library Manager.
//
// Users who want to opt out of network can hand-place Secbox.Core.dll under
// LocalCachePath and disable AutoUpdate.
public static class CorePolicy
{
	public const int RequiredProtocolVersion = 2;

	// The version string is informational; identity is the SHA-256 hash.
	// MUST match the GitHub Release tag exactly — the URL construction in
	// SecboxCoreLoader builds {BaseUrl}/{CoreVersion}/{filename}, which
	// resolves to GitHub's canonical release-asset URL:
	//   https://github.com/<org>/<repo>/releases/download/<tag>/<filename>
	public const string CoreVersion = "v0.1.0-dev";

	// GitHub Releases serve assets at
	//   https://github.com/<org>/<repo>/releases/download/<tag>/<filename>
	// SecboxCoreLoader appends "/{CoreVersion}/{filename}" so BaseUrl is the
	// download prefix without the tag segment.
	public const string BaseUrl =
		"https://github.com/actual-f4-industries/secbox/releases/download";

	// Files the loader downloads in order. Hash pin for each.
	public static readonly (string FileName, string Sha256)[] CoreFiles =
	{
		("Secbox.Core.dll",                        "41294305819a86b309023715a30e1072fc1443e32752fb0a709616515486828d"),
		("Secbox.Contracts.dll",                   "4af77101ba0322838df48d28dcff65e346a4eb796c836651441266564430ed6b"),
		("Secbox.Rules.dll",                       "1392d416d447d012655133cf38d0e05211f0029d526c9b72f95d4ad0ce0cf96b"),
		("Secbox.Scanner.dll",                     "3ef7a21e71bc16fa18eef4f7b6adf970f90e38ffa2889d9d702bc3925cedaac9"),
		("Mono.Cecil.dll",                         "831dca77470d85cb6ffbea3072daa7a3df5b7c9fcfd9c3f43674a9be99d4bfcf"),
		// BridgeProtocol v2 additions — runtime monitoring stack.
		("Secbox.Sentinel.Contracts.dll",          "c4ad737eaee1064227b4b1ab892afc2048c29864009270cf350077d69d53994e"),
		("Secbox.Sentinel.Client.dll",             "46812403394f6af2b4089064c1cbff57cb26394803c27364a6e34bb7470d469a"),
		("Microsoft.Diagnostics.NETCore.Client.dll", "863a7b01a6ea6db9bd8df140bf0bfeed91909a5d26140e5265a8ee2344847adb"),
		// Tier E (Harmony runtime patches) — ManagedCallSensor patches
		// System.Diagnostics.Process.Start for library-attributed spawn tracking.
		("0Harmony.dll",                           "817b0127f0f512122a0e0f8cab1d89c6431f6533898e473bbecdc61846cf945c"),
		("secbox-profiler-win-x64.dll",            "a0eafac135c4d9c638352db8022cf8f130faecc4a8bbc31224cbb7543c8ca0d8"),
	};

	public static string LocalCachePath =>
		Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"secbox", "core", CoreVersion);

	// Default folder where local dev builds of Secbox.Core get dropped.
	// The AfterBuild MSBuild target in Secbox.Core.csproj copies here.
	public static string DevDefaultPath =>
		Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"secbox", "core", "dev");

	public static bool AutoUpdate => SecboxConfig.Load().AutoUpdate;

	public static bool DevModeActive
	{
		get
		{
			var env = Environment.GetEnvironmentVariable("SECBOX_DEV_PATH");
			if (!string.IsNullOrEmpty(env)) return true;
			return SecboxConfig.Load().DevMode;
		}
	}

	// Resolution order:
	//   1. %SECBOX_DEV_PATH% env var (highest priority — for one-off testing)
	//   2. config.json DevPath if DevMode = true and DevPath non-empty
	//   3. DevDefaultPath if DevMode = true but DevPath blank
	//   4. null (production mode — use CDN download path)
	public static string DevOverridePath
	{
		get
		{
			var env = Environment.GetEnvironmentVariable("SECBOX_DEV_PATH");
			if (!string.IsNullOrEmpty(env)) return env;

			var cfg = SecboxConfig.Load();
			if (!cfg.DevMode) return null;
			if (!string.IsNullOrEmpty(cfg.DevPath)) return cfg.DevPath;
			return DevDefaultPath;
		}
	}

	// Menu-toggle helpers. Mutate the config file + return new state.
	public static bool EnableDevMode(string customPath = null)
	{
		var cfg = SecboxConfig.Load();
		cfg.DevMode = true;
		if (customPath != null) cfg.DevPath = customPath;
		cfg.Save();
		return true;
	}

	public static bool DisableDevMode()
	{
		var cfg = SecboxConfig.Load();
		cfg.DevMode = false;
		cfg.Save();
		return false;
	}
}
