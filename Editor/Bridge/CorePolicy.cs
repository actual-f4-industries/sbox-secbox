using System;
using System.IO;

namespace Sandbox.SecBox.Bridge;

// Pinned configuration for downloading + verifying Secbox.Core.dll.
//
// SHA-256 hashes are baked in at adapter compile time — refusing any
// downloaded blob whose hash doesn't match. To bump to a new core version:
//   1. Build Secbox.sln Release.
//   2. sha256sum src/Secbox.Core/bin/Release/net10.0/Secbox.Core.dll
//   3. Update ExpectedCoreSha256 and CoreVersion here.
//   4. Re-publish the adapter.
//
// Users who want to opt out of network can hand-place Secbox.Core.dll under
// LocalCachePath and disable AutoUpdate.
public static class CorePolicy
{
	public const int RequiredProtocolVersion = 1;

	// The version string is informational; identity is the SHA-256 hash.
	public const string CoreVersion = "0.1.0-dev";

	// CDN base URL. Multiple files (Secbox.Core.dll + dependencies) live at
	// {BaseUrl}/{version}/{filename}.
	public const string BaseUrl = "https://f4pl0.com/secbox/";

	// Files the loader downloads in order. Hash pin for each.
	// PLACEHOLDER hashes — replace with real SHA-256 values after publishing.
	public static readonly (string FileName, string Sha256)[] CoreFiles =
	{
		("Secbox.Core.dll",     "0000000000000000000000000000000000000000000000000000000000000000"),
		("Secbox.Contracts.dll","0000000000000000000000000000000000000000000000000000000000000000"),
		("Secbox.Rules.dll",    "0000000000000000000000000000000000000000000000000000000000000000"),
		("Secbox.Scanner.dll",  "0000000000000000000000000000000000000000000000000000000000000000"),
		("Mono.Cecil.dll",      "0000000000000000000000000000000000000000000000000000000000000000"),
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
