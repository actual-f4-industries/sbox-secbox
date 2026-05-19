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
		("Secbox.Core.dll",                        "5265cd1fbd3d154ab343fb7bd2d05391682070ee84b5f11926c38243463a5d0b"),
		("Secbox.Contracts.dll",                   "95d36b0f6695b088518fa1eedf21952b6bce8bd039a505ebdeb143d2edd25727"),
		("Secbox.Rules.dll",                       "21e3066a6f97985943a92d7c052319cfe17ef19e425f2f47c583f6cd3a7f0dd8"),
		("Secbox.Scanner.dll",                     "7e0486fee9ec5cb05b42d0e756ae72ebd5580a13db3a061a7754ba2dc5a838db"),
		("Mono.Cecil.dll",                         "831dca77470d85cb6ffbea3072daa7a3df5b7c9fcfd9c3f43674a9be99d4bfcf"),
		// BridgeProtocol v2 additions — runtime monitoring stack.
		("Secbox.Sentinel.Contracts.dll",          "08bffafe11b92730eb5ffe1159a8c2d263aadd4ba95e06f6ae43805fb078fcad"),
		("Secbox.Sentinel.Client.dll",             "71e9175c218d8bafc6001e87054e2bb06a1cd5af8337e3c21d47bbc915746bc7"),
		("Microsoft.Diagnostics.NETCore.Client.dll", "863a7b01a6ea6db9bd8df140bf0bfeed91909a5d26140e5265a8ee2344847adb"),
		// Tier E (Harmony runtime patches) — ManagedCallSensor patches
		// System.Diagnostics.Process.Start for library-attributed spawn tracking.
		("0Harmony.dll",                           "817b0127f0f512122a0e0f8cab1d89c6431f6533898e473bbecdc61846cf945c"),
		// WPF runtime-detection dialog. The editor adapter Process.Starts
		// this exe directly when a Critical finding arrives and Sentinel
		// service isn't running. Out-of-process by construction — survives
		// any subsequent editor freeze.
		//
		// Self-contained single-file: .NET 10 + WPF embedded inside the
		// exe (~62MB compressed). Works on machines without .NET Desktop
		// Runtime installed. Hash will change every release because R2R /
		// runtime DLL contents inside the bundle are version-stamped.
		("SecboxAlertUI.exe",                      "863fde2befd9be43c3a9c00f8bdcf42f0149cc98822d4f0c3dc4e5277876f22f"),
		("secbox-profiler-win-x64.dll",            "b9f055c9053df5bd3e5d0ec559cab83117eb5422e1523a3b2b67a377d91c8d2f"),
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
