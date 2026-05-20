using System;
using System.Linq;
using Editor;
using Sandbox.SecBox.Bridge;
using Sandbox.SecBox.Bridge.Dto;
using Sandbox.SecBox.Lifecycle;

namespace Sandbox.SecBox.UI;

// Top-level menu entries under "secbox/" in the editor menu bar. Lets users
// trigger scans, open the trust store, toggle dev mode, etc.
public static class MenuItems
{
	[Menu( "Editor", "secbox/Dev Mode/Toggle Dev Mode" )]
	public static void ToggleDevMode()
	{
		bool wasActive = CorePolicy.DevModeActive;
		if (wasActive)
		{
			CorePolicy.DisableDevMode();
			SecboxCoreLoader.TryUnload();
			EditorUtility.DisplayDialog(
				"secbox: dev mode OFF",
				$"Production mode restored. Next scan loads Secbox.Core from the verified CDN cache.\n\nConfig: {SecboxConfig.FilePath}",
				icon: "🔒");
		}
		else
		{
			CorePolicy.EnableDevMode();
			SecboxCoreLoader.TryUnload();
			System.IO.Directory.CreateDirectory(CorePolicy.DevDefaultPath);
			EditorUtility.DisplayDialog(
				"secbox: dev mode ON",
				$"Hash verification SKIPPED. Loading Secbox.Core from:\n{CorePolicy.DevDefaultPath}\n\n"
				+ $"Build the Secbox solution (its AfterBuild target auto-copies here).\n\n"
				+ $"Config: {SecboxConfig.FilePath}\n"
				+ $"Edit 'devPath' in the JSON to point elsewhere.",
				icon: "🛠️");
		}
	}

	[Menu( "Editor", "secbox/Dev Mode/Show Status" )]
	public static void ShowDevModeStatus()
	{
		var active = CorePolicy.DevModeActive;
		var resolved = CorePolicy.DevOverridePath ?? "(production mode — verified CDN cache)";
		var cfg = SecboxConfig.Load();
		var envOverride = System.Environment.GetEnvironmentVariable("SECBOX_DEV_PATH");

		var lines = new[]
		{
			$"Dev mode: {(active ? "ON" : "OFF")}",
			$"",
			$"Config file: {SecboxConfig.FilePath}",
			$"  exists: {System.IO.File.Exists(SecboxConfig.FilePath)}",
			$"  devMode: {cfg.DevMode}",
			$"  devPath: {(string.IsNullOrEmpty(cfg.DevPath) ? "(unset → DevDefaultPath)" : cfg.DevPath)}",
			$"  autoUpdate: {cfg.AutoUpdate}",
			$"",
			$"DevDefaultPath: {CorePolicy.DevDefaultPath}",
			$"  exists: {System.IO.Directory.Exists(CorePolicy.DevDefaultPath)}",
			$"",
			$"Resolved load path: {resolved}",
			$"",
			$"%SECBOX_DEV_PATH% override: {envOverride ?? "(not set)"}",
		};

		EditorUtility.DisplayDialog("secbox dev-mode status", string.Join("\n", lines), icon: active ? "🛠️" : "🔒");
	}

	[Menu( "Editor", "secbox/Dev Mode/Open Config File" )]
	public static void OpenConfigFile()
	{
		var path = SecboxConfig.FilePath;
		if (!System.IO.File.Exists(path))
		{
			// Create with defaults so the user has something to edit.
			new SecboxConfig().Save();
		}
		try
		{
			System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
			{
				FileName = path,
				UseShellExecute = true,
			});
		}
		catch (System.Exception ex)
		{
			EditorUtility.DisplayDialog("secbox", $"Could not open: {ex.Message}\n\nPath: {path}");
		}
	}

	[Menu( "Editor", "secbox/Open Diagnostics Log" )]
	public static void OpenDiagnosticsLog()
	{
		var path = DiagnosticsLog.FilePath;
		if (!System.IO.File.Exists(path))
		{
			EditorUtility.DisplayDialog("secbox", $"Log file does not exist yet:\n{path}");
			return;
		}
		try
		{
			System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
			{
				FileName = path,
				UseShellExecute = true,
			});
		}
		catch (System.Exception ex)
		{
			EditorUtility.DisplayDialog("secbox", $"Could not open: {ex.Message}\n\nPath: {path}");
		}
	}

	[Menu( "Editor", "secbox/Open Diagnostics Log Folder" )]
	public static void OpenDiagnosticsLogFolder()
	{
		var folder = System.IO.Path.GetDirectoryName(DiagnosticsLog.FilePath);
		try
		{
			System.IO.Directory.CreateDirectory(folder);
			System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
			{
				FileName = folder,
				UseShellExecute = true,
			});
		}
		catch (System.Exception ex)
		{
			EditorUtility.DisplayDialog("secbox", $"Could not open: {ex.Message}\n\nFolder: {folder}");
		}
	}

	[Menu( "Editor", "secbox/Dev Mode/Reload Core Now" )]
	public static void ReloadCore()
	{
		var unloaded = SecboxCoreLoader.TryUnload();
		try
		{
			SecboxCoreClient.EnsureReadyAsync().GetAwaiter().GetResult();
			var info = SecboxCoreClient.GetInfo();
			EditorUtility.DisplayDialog(
				"secbox: core reloaded",
				$"{(unloaded ? "Unloaded previous instance.\n\n" : "")}Loaded Secbox.Core v{info.ScannerVersion}\n"
				+ $"Protocol: {info.ProtocolVersion}\n"
				+ $"Finders: {string.Join(", ", info.AvailableFinders)}\n"
				+ $"Packs: {info.AvailableRulePacks.Count}",
				icon: "♻️");
		}
		catch (System.Exception ex)
		{
			EditorUtility.DisplayDialog(
				"secbox: core reload failed",
				$"{ex.Message}\n\nCheck the configured dev path or production hash pinning.",
				icon: "😬");
		}
	}

	[Menu( "Editor", "secbox/Scan All Libraries Now" )]
	public static void ScanAll()
	{
		global::Sandbox.Internal.GlobalGameNamespace.Log.Info(
			"[secbox] manual scan triggered" );
		BootAudit.Run();
		ShowUnreviewedSummary();
	}

	[Menu( "Editor", "secbox/Show Unreviewed Findings..." )]
	public static void ShowUnreviewedSummary()
	{
		var root = PackageLocator.CurrentProjectRoot();
		if ( string.IsNullOrEmpty( root ) )
		{
			EditorUtility.DisplayDialog( "secbox", "No current project — open a project first." );
			return;
		}

		var store = TrustStore.Load( root );
		var unreviewed = store.Entries
			.Where( e => e.Decision == Decision.Unreviewed )
			.OrderByDescending( e => e.CriticalCount + e.HighCount )
			.ToList();

		if ( unreviewed.Count == 0 )
		{
			EditorUtility.DisplayDialog( "secbox",
				$"No unreviewed packages.\n\nTrust store: {store.Entries.Count} entries.\nLocation: {store.FilePath}" );
			return;
		}

		var lines = unreviewed
			.Take( 20 )
			.Select( e => $"  {e.PackageIdent}: Crit={e.CriticalCount} High={e.HighCount} Med={e.MediumCount} Low={e.LowCount}" );

		var body = $"{unreviewed.Count} unreviewed package(s):\n\n"
			+ string.Join( "\n", lines )
			+ "\n\nOpen .secbox/trust.json for details, or run a fresh scan with secbox > Scan All Libraries Now.";

		EditorUtility.DisplayDialog( "secbox: pending reviews", body );
	}

	// ============================================================
	// Runtime monitoring (Tier B profiler + opt-in Tier A Sentinel)
	// ============================================================

	[Menu( "Editor", "secbox/Runtime Monitoring/Settings..." )]
	public static void OpenSentinelSettings()
	{
		try
		{
			new SentinelSettingsDialog().Show();
		}
		catch ( System.Exception ex )
		{
			EditorUtility.DisplayDialog( "secbox", $"Could not open settings: {ex.Message}" );
		}
	}

	[Menu( "Editor", "secbox/Runtime Monitoring/Install Sentinel..." )]
	public static void InstallSentinel()
	{
		try
		{
			new SentinelInstallDialog().Show();
		}
		catch ( System.Exception ex )
		{
			EditorUtility.DisplayDialog( "secbox", $"Could not open installer dialog: {ex.Message}" );
		}
	}

	[Menu( "Editor", "secbox/Runtime Monitoring/Show Status" )]
	public static void ShowRuntimeMonitorStatus()
	{
		var cfg = SecboxConfig.Load();
		var attached = Lifecycle.RuntimeMonitorCoordinator.IsAttached;
		var sentinelInstalled = Lifecycle.SentinelInstaller.IsServiceInstalled();
		var sentinelRunning = sentinelInstalled && Lifecycle.SentinelInstaller.IsServiceRunning();

		string sensors = "(not attached)";
		if ( attached )
		{
			try
			{
				var s = Bridge.RuntimeMonitorBridge.GetStatus();
				sensors = string.Join( "\n  ",
					s.Select( x => $"{x.Id}: {x.Status}{(string.IsNullOrEmpty(x.LastError) ? "" : " — " + x.LastError)}" ) );
			}
			catch ( System.Exception ex ) { sensors = $"(status query failed: {ex.Message})"; }
		}

		var lines = new[]
		{
			$"Tier B (profiler): {(cfg.RuntimeMonitoringEnabled ? "enabled" : "disabled")}",
			$"Tier A (Sentinel): {(cfg.SentinelEnabled ? "enabled in config" : "disabled in config")}",
			$"Sentinel service:  {(sentinelInstalled ? (sentinelRunning ? "installed & running" : "installed, stopped") : "NOT installed")}",
			$"Attached:          {attached}",
			$"Recent findings:   {Lifecycle.RuntimeMonitorCoordinator.RecentCount}",
			"",
			"Sensors:",
			"  " + sensors,
		};

		EditorUtility.DisplayDialog( "secbox: runtime monitoring", string.Join( "\n", lines ), icon: attached ? "monitor_heart" : "monitor" );
	}

	[Menu( "Editor", "secbox/Runtime Monitoring/Attach Now" )]
	public static void AttachRuntimeNow()
	{
		Lifecycle.RuntimeMonitorCoordinator.EnsureAttached();
		ShowRuntimeMonitorStatus();
	}

	[Menu( "Editor", "secbox/Runtime Monitoring/Detach Now" )]
	public static void DetachRuntimeNow()
	{
		Lifecycle.RuntimeMonitorCoordinator.Detach();
		EditorUtility.DisplayDialog( "secbox", "Runtime sensors detached." );
	}

	[Menu( "Editor", "secbox/Runtime Monitoring/Open Sentinel Event Log" )]
	public static void OpenSentinelEventLog()
	{
		var path = Bridge.SentinelEventLog.FilePath;
		if ( !System.IO.File.Exists( path ) )
		{
			EditorUtility.DisplayDialog( "secbox",
				$"Sentinel event log does not exist yet:\n{path}\n\n"
				+ "Enable Sentinel via Runtime Monitoring > Settings… and let it capture some events first." );
			return;
		}
		try
		{
			System.Diagnostics.Process.Start( new System.Diagnostics.ProcessStartInfo
			{
				FileName = path,
				UseShellExecute = true,
			} );
		}
		catch ( System.Exception ex )
		{
			EditorUtility.DisplayDialog( "secbox", $"Could not open: {ex.Message}\n\nPath: {path}" );
		}
	}

	[Menu( "Editor", "secbox/Open Trust Store File" )]
	public static void OpenTrustStore()
	{
		var root = PackageLocator.CurrentProjectRoot();
		if ( string.IsNullOrEmpty( root ) )
		{
			EditorUtility.DisplayDialog( "secbox", "No current project." );
			return;
		}

		var path = System.IO.Path.Combine( root, ".secbox", "trust.json" );
		if ( !System.IO.File.Exists( path ) )
		{
			EditorUtility.DisplayDialog( "secbox", $"Trust store does not exist yet at:\n{path}\n\nInstall a library or run a scan to create it." );
			return;
		}

		try
		{
			System.Diagnostics.Process.Start( new System.Diagnostics.ProcessStartInfo
			{
				FileName = path,
				UseShellExecute = true,
			} );
		}
		catch ( System.Exception ex )
		{
			EditorUtility.DisplayDialog( "secbox", $"Could not open: {ex.Message}\n\nPath: {path}" );
		}
	}
	
	[Menu( "Editor", "secbox/Open Source Code" )]
	public static void OpenSourceCode()
	{
		const string url = "https://github.com/actual-f4-industries/sbox-secbox";
		try
		{
			System.Diagnostics.Process.Start( new System.Diagnostics.ProcessStartInfo
			{
				FileName = url,
				UseShellExecute = true,
			} );
			EditorUtility.DisplayDialog( "secbox", $"Opened! Please find https://github.com/actual-f4-industries/sbox-secbox in your default browser." );
		}
		catch ( System.Exception ex )
		{
			EditorUtility.DisplayDialog( "secbox", $"Could not open source code:\n{ex.Message}\n\nURL: {url}" );
		}
	}

}
