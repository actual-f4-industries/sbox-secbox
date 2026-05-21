using System;
using System.IO;

namespace Sandbox.SecBox.Bridge;

// Pinned configuration for downloading + verifying Secbox.Core.dll.
//
// SHA-256 hashes are baked in at adapter compile time - refusing any
// downloaded blob whose hash doesn't match. To bump to a new core version:
//   1. Push a vX.Y.Z tag - `.github/workflows/release.yml` builds every
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
	// MUST match the GitHub Release tag exactly - the URL construction in
	// SecboxCoreLoader builds {BaseUrl}/{CoreVersion}/{filename}, which
	// resolves to GitHub's canonical release-asset URL:
	//   https://github.com/<org>/<repo>/releases/download/<tag>/<filename>
	public const string CoreVersion = "v0.1.0";

	// GitHub Releases serve assets at
	//   https://github.com/<org>/<repo>/releases/download/<tag>/<filename>
	// SecboxCoreLoader appends "/{CoreVersion}/{filename}" so BaseUrl is the
	// download prefix without the tag segment.
	public const string BaseUrl =
		"https://github.com/actual-f4-industries/secbox/releases/download";

	// Files the loader downloads in order. Hash pin for each.
	public static readonly (string FileName, string Sha256)[] CoreFiles =
	{
		("Secbox.Core.dll",                        "94dc0573aa1e076d5436c5fb040acb560ccd378aadba25853b834a2eab734abd"),
		("Secbox.Contracts.dll",                   "f9d6fdf4226a1fcc514461b65d7e2c372f8612fec5ba92a955800321447cf699"),
		("Secbox.Rules.dll",                       "1182edbf45d2b1129e9f28bf87558e1b41cd7e3a28dda3c230c516493a118df4"),
		("Secbox.Scanner.dll",                     "56db37557921c33da3108c1261ac8c39948ca94f12f5173b1edacda8d2bbc953"),
		("Mono.Cecil.dll",                         "831dca77470d85cb6ffbea3072daa7a3df5b7c9fcfd9c3f43674a9be99d4bfcf"),
		// Tier E (Harmony runtime patches) - ManagedCallSensor patches
		// System.Diagnostics.Process.Start for library-attributed spawns.
		("0Harmony.dll",                           "fd77b88724f4104440df0cf979a851d35eec75ea3a7e86297d04abe47c71aff6"),
		// WPF decision dialog. The Tier E hook Process.Starts this exe
		// in-process and blocks the calling thread on its exit code.
		// Self-contained single-file: .NET 10 + WPF embedded (~62MB). Hash
		// changes every release (runtime DLLs inside the bundle are stamped).
		("SecboxAlertUI.exe",                      "0429a035745e86268fe22e407630100bf9e2d63d4a9b49065e371232f5346ac3"),
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
	//   1. %SECBOX_DEV_PATH% env var (highest priority - for one-off testing)
	//   2. config.json DevPath if DevMode = true and DevPath non-empty
	//   3. DevDefaultPath if DevMode = true but DevPath blank
	//   4. null (production mode - use CDN download path)
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
