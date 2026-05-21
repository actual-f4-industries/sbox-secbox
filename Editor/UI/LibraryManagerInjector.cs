using System;
using Editor;
using DiagnosticsLog = Sandbox.SecBox.Bridge.DiagnosticsLog;

namespace Sandbox.SecBox.UI;

// Orchestrates injection of SecBox UI into the Library Manager dock. The dock
// is created lazily by the editor (only when the user opens View > Library
// Manager). Even after the dock exists, its children are transient:
//
//   - LibraryManagerDock has an [EditorEvent.Frame] -> Rebuild loop that
//     can recreate ListLocal / ListGlobal.
//   - LibraryDetail is replaced wholesale on every row click.
//   - LibraryDetail.FetchAndBuild() Layout.Clear()s itself after construction.
//
// Therefore we drive both decorators from per-frame EnsureInstalled calls
// rather than from one-shot installers. The work is cheap (descendant walks
// of a single dock; per-instance idempotence checks). When the dock is closed
// the cached state is reset, and the next time it reopens we wire fresh.
public static class LibraryManagerInjector
{
	static Widget _cachedDock;
	static int _frameCounter;

	// Called from SecboxBoot.Init purely for the log line confirming wiring.
	// Actual work happens on the editor's main loop via [EditorEvent.Frame].
	public static void Arm()
	{
		DiagnosticsLog.Info( "[secbox] LibraryManagerInjector armed - awaiting first frame" );
	}

	[EditorEvent.Frame]
	public static void OnFrame()
	{
		// Throttle. Frame fires at editor render rate (60+ Hz). The walk is
		// cheap but still pointless at that rate when nothing changes.
		// ~8x throttle while idle; user-perceived UI lag is negligible.
		if ( (_frameCounter++ & 0b111) != 0 ) return;

		Widget dock;
		try
		{
			var main = EditorWindow;
			dock = main?.DockManager?.GetDockWidget( "Library Manager" );
		}
		catch ( Exception ex )
		{
			DiagnosticsLog.Warn( $"[secbox] LibraryManagerInjector.OnFrame: lookup threw: {ex.Message}" );
			return;
		}

		if ( dock == null || !dock.IsValid )
		{
			// User closed the Library Manager tab (or never opened it).
			if ( _cachedDock != null )
			{
				_cachedDock = null;
				DiagnosticsLog.Trace( "[secbox] LibraryManagerInjector: dock gone - state reset" );
			}
			return;
		}

		if ( !ReferenceEquals( dock, _cachedDock ) )
		{
			_cachedDock = dock;
			DiagnosticsLog.Trace( "[secbox] LibraryManagerInjector: new dock instance detected" );
		}

		try
		{
			LibraryRowBadge.EnsureInstalled( dock );
			LibraryDetailPanel.EnsureInstalled( dock );
		}
		catch ( Exception ex )
		{
			DiagnosticsLog.Error( "[secbox] LibraryManagerInjector.OnFrame: ensure threw", ex );
		}
	}

	[EditorEvent.Hotload]
	public static void OnHotload()
	{
		_cachedDock = null;
		DiagnosticsLog.Trace( "[secbox] LibraryManagerInjector: hotload - state reset" );
	}
}
