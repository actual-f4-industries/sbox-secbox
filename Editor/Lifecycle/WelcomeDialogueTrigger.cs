using System;
using System.IO;
using Editor;
using Sandbox.SecBox.Bridge;
using Sandbox.SecBox.UI;

namespace Sandbox.SecBox.Lifecycle;

// Decides whether to show the post-install welcome and fires it exactly once
// per editor session. Frame-driven so we can self-heal — we wait until
// Project.Current is loaded before showing (editor.created isn't a reliable
// "project is ready" signal). Same throttle / latch pattern as
// LibraryManagerInjector.
//
// Show rules:
//   - Suppressed entirely if SecboxConfig.WelcomeDialogueDismissedGlobally.
//   - Suppressed for projects where <root>/.secbox/welcome-shown exists.
//   - Otherwise show once. Marker is written on every close path.
public static class WelcomeDialogueTrigger
{
	const string MarkerFileName = "welcome-shown";

	static bool _decided;
	static int _frameCounter;

	[EditorEvent.Frame]
	public static void OnFrame()
	{
		if ( _decided ) return;

		// 8x throttle, same as LibraryManagerInjector. Frame fires at editor
		// render rate so the unthrottled cost would be silly for a one-shot
		// decision.
		if ( (_frameCounter++ & 0b111) != 0 ) return;

		DiagnosticsLog.Wrap( "WelcomeDialogueTrigger.OnFrame", DecideAndMaybeShow );
	}

	[EditorEvent.Hotload]
	public static void OnHotload()
	{
		// Do NOT reset _decided — hot-reloading secbox code must not re-fire
		// the welcome. Only the throttle counter is safe to reset.
		_frameCounter = 0;
	}

	// Public entry used by MenuItems > "Show Welcome..." to force-show
	// regardless of marker / global flag. Does NOT write the marker (it's
	// already there if needed; manual re-opens shouldn't change persistence).
	public static void ShowNow( bool isManualInvocation )
	{
		MainThread.Queue( () =>
		{
			try
			{
				var root = PackageLocator.CurrentProjectRoot();
				var markerPath = string.IsNullOrEmpty( root ) ? null : MarkerPathFor( root );
				ShowDialog( markerPath, isManualInvocation );
			}
			catch ( Exception ex )
			{
				DiagnosticsLog.Error( "[secbox] welcome: manual show failed", ex );
			}
		} );
	}

	static void DecideAndMaybeShow()
	{
		// Bail cheap if no project is loaded yet — avoids re-reading config
		// every tick while the user sits on the project picker.
		var projectRoot = PackageLocator.CurrentProjectRoot();
		if ( string.IsNullOrEmpty( projectRoot ) )
			return;

		var cfg = SecboxConfig.Load();
		if ( cfg.WelcomeDialogueDismissedGlobally )
		{
			_decided = true;
			DiagnosticsLog.Info( "[secbox] welcome: skipped — globally dismissed" );
			return;
		}

		var markerPath = MarkerPathFor( projectRoot );
		if ( MarkerExists( markerPath ) )
		{
			_decided = true;
			DiagnosticsLog.Trace( $"[secbox] welcome: already shown for {projectRoot}" );
			return;
		}

		// Latch BEFORE marshalling so a second frame tick can't race a
		// second dialogue into existence while MainThread.Queue is in flight.
		_decided = true;

		MainThread.Queue( () =>
		{
			try { ShowDialog( markerPath, isManualInvocation: false ); }
			catch ( Exception ex ) { DiagnosticsLog.Error( "[secbox] welcome: dialog create failed", ex ); }
		} );
	}

	static void ShowDialog( string markerPath, bool isManualInvocation )
	{
		var dlg = new WelcomeDialogue();
		dlg.Closed = result => OnDialogueClosed( markerPath, isManualInvocation, result );
		dlg.Show();
		DiagnosticsLog.Info( $"[secbox] welcome: dialogue shown (manual={isManualInvocation})" );
	}

	static void OnDialogueClosed( string markerPath, bool isManualInvocation, WelcomeDialogueResult result )
	{
		DiagnosticsLog.Wrap( "WelcomeDialogueTrigger.OnClose", () =>
		{
			// Write marker on every auto-shown close — even "Got it" without
			// the don't-show checkbox should stop the welcome reappearing in
			// THIS project. Manual re-opens via menu skip the write (the
			// marker is already there, and re-opening shouldn't change
			// persistence for projects that may not have one yet).
			if ( !isManualInvocation && !string.IsNullOrEmpty( markerPath ) )
				WriteMarker( markerPath );

			if ( result != null && result.DontShowAgainGlobally )
			{
				var cfg = SecboxConfig.Load();
				cfg.WelcomeDialogueDismissedGlobally = true;
				cfg.Save();
				DiagnosticsLog.Info( "[secbox] welcome: globally dismissed by user" );
			}
		} );
	}

	static string MarkerPathFor( string projectRoot )
		=> Path.Combine( projectRoot, ".secbox", MarkerFileName );

	static bool MarkerExists( string markerPath )
	{
		try { return File.Exists( markerPath ); }
		catch { return false; }
	}

	static void WriteMarker( string markerPath )
	{
		try
		{
			var dir = Path.GetDirectoryName( markerPath );
			if ( !string.IsNullOrEmpty( dir ) )
				Directory.CreateDirectory( dir );
			File.WriteAllText( markerPath, string.Empty );
		}
		catch ( Exception ex )
		{
			DiagnosticsLog.Warn( $"[secbox] welcome: failed to write marker at {markerPath}: {ex.Message}" );
			// _decided is already true; we won't loop. Next session this
			// project may show the welcome again — acceptable for
			// unwritable roots.
		}
	}
}
