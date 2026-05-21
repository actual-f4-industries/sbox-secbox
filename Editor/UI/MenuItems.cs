using System;
using System.Linq;
using Editor;
using Sandbox.SecBox.Bridge;
using Sandbox.SecBox.Bridge.Dto;
using Sandbox.SecBox.Lifecycle;

namespace Sandbox.SecBox.UI;

// Top-level menu entries under "secbox/" in the editor menu bar. Lets users
// trigger scans, manage runtime monitoring, and access dev tooling.
public static class MenuItems
{
	[Menu( "Editor", "secbox/Trusted Libraries..." )]
	public static void OpenTrustManager()
	{
		TrustManagerWindow.Open();
	}

	[Menu( "Editor", "secbox/Dev/Toggle Dev Mode" )]
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

	[Menu( "Editor", "secbox/Dev/Show Status" )]
	public static void ShowDevModeStatus()
	{
		var active = CorePolicy.DevModeActive;
		var resolved = CorePolicy.DevOverridePath ?? "(production mode - verified CDN cache)";
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

	[Menu( "Editor", "secbox/Dev/Open Config File" )]
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

	[Menu( "Editor", "secbox/Dev/Open Diagnostics Log" )]
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

	[Menu( "Editor", "secbox/Dev/Open Diagnostics Log Folder" )]
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

	[Menu( "Editor", "secbox/Dev/Reload Core Now" )]
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

	[Menu( "Editor", "secbox/Scan now" )]
	public static void ScanNow()
	{
		global::Sandbox.Internal.GlobalGameNamespace.Log.Info(
			"[secbox] manual scan triggered" );

		var root = PackageLocator.CurrentProjectRoot();
		if ( string.IsNullOrEmpty( root ) )
		{
			EditorUtility.DisplayDialog( "secbox", "No current project. Open a project first." );
			return;
		}

		// Open the results window in its scanning state, then run the scan off the
		// UI thread (ScanFolder / EnsureReadyAsync block internally and would
		// deadlock here) and feed results back on the main thread.
		var window = ScanResultsWindow.OpenScanning();
		System.Threading.Tasks.Task.Run( () =>
		{
			try
			{
				var results = BootAudit.ScanAllLibraries();
				MainThread.Queue( () => { try { window.SetResults( results ); } catch { } } );
			}
			catch ( System.Exception ex )
			{
				DiagnosticsLog.Error( "[secbox] manual scan failed", ex );
				MainThread.Queue( () => { try { window.SetResults( null ); } catch { } } );
			}
		} );
	}

	// ============================================================
	// Runtime monitoring (Tier E managed-call enforcement)
	// ============================================================

	[Menu( "Editor", "secbox/Runtime Monitoring/Show Status" )]
	public static void ShowRuntimeMonitorStatus()
	{
		var cfg = SecboxConfig.Load();
		var attached = Lifecycle.RuntimeMonitorCoordinator.IsAttached;

		string sensors = "(not attached)";
		if ( attached )
		{
			try
			{
				var s = Bridge.RuntimeMonitorBridge.GetStatus();
				sensors = string.Join( "\n  ",
					s.Select( x => $"{x.Id}: {x.Status}{(string.IsNullOrEmpty(x.LastError) ? "" : " - " + x.LastError)}" ) );
			}
			catch ( System.Exception ex ) { sensors = $"(status query failed: {ex.Message})"; }
		}

		var lines = new[]
		{
			$"Runtime enforcement (Tier E): {(cfg.RuntimeMonitoringEnabled ? "enabled" : "disabled")}",
			$"Block library Process.Start:  {cfg.BlockLibraryProcessStart}",
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
}
