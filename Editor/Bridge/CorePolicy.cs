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
		("Secbox.Core.dll",                        "7678f835cf2e4ce6f7b6085b8b86032ccf2fcf2009a027afb7bd3eaef4b7b48b"),
		("Secbox.Contracts.dll",                   "cd112cc1675d506317dd09ddfee23be3b7db1ec38627943b0c5f9779b6026287"),
		("Secbox.Rules.dll",                       "c321ffdfcabf8de0a4ece360b766c986810e971a92faf3ff15d68d9c67323c92"),
		("Secbox.Scanner.dll",                     "7de5ef9a513d5446d848a240e60d13e2e6b362e11e30add350d9c02ed2ec31d7"),
		("Mono.Cecil.dll",                         "831dca77470d85cb6ffbea3072daa7a3df5b7c9fcfd9c3f43674a9be99d4bfcf"),
		// BridgeProtocol v2 additions — runtime monitoring stack.
		("Secbox.Sentinel.Contracts.dll",          "440917e0359c409492b59a2fda6e925b88d229d5d17f60c95f6e6b409e5227d0"),
		("Secbox.Sentinel.Client.dll",             "87f5f988482f095ce55d7a1d5963b011f6be26755b11f13f5b9f62865dc5656b"),
		("Microsoft.Diagnostics.NETCore.Client.dll", "863a7b01a6ea6db9bd8df140bf0bfeed91909a5d26140e5265a8ee2344847adb"),
		// Tier E (Harmony runtime patches) — ManagedCallSensor patches
		// System.Diagnostics.Process.Start for library-attributed spawn tracking.
		("0Harmony.dll",                           "817b0127f0f512122a0e0f8cab1d89c6431f6533898e473bbecdc61846cf945c"),
		// WPF runtime-detection dialog. The editor adapter Process.Starts
		// this exe directly when a Critical finding arrives and Sentinel
		// service isn't running. Out-of-process by construction — survives
		// any subsequent editor freeze. Framework-dependent (.NET 10
		// Desktop Runtime required on the user's machine).
		("SecboxAlertUI.exe",                      "209ca00918c76c4514102b696b23561796cb1cd7ba7bd4c3ec1b7510b02242f2"),
		("SecboxAlertUI.dll",                      "92edb8f6c3cd9ca5a7dc6837d4a105d6c9bac933cc70c70690f9c888132144eb"),
		("SecboxAlertUI.deps.json",                "027de1eceb8174e61c3de67af0ba5d6f34e936997c2ff55144f1b916bbcfe7c3"),
		("SecboxAlertUI.runtimeconfig.json",       "96bc2946dff8790400d652ec7f8de7bb043e954980d59470c112788b68a1fbd3"),
		("secbox-profiler-win-x64.dll",            "afc4f431b2f277b29d03a633eb4d74dce2fc53bfc342655beb97b9168d33d726"),
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
