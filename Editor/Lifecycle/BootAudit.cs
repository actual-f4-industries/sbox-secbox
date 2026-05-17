using System;
using System.Linq;
using Editor;
using Sandbox;

namespace Sandbox.SecBox.Lifecycle;

// Walks LibrarySystem.All at editor startup, scans every library that doesn't
// already have a TrustAlways or Block decision recorded for its current
// content hash. Findings are persisted to the trust store as Unreviewed so
// the UI can surface them when task #8 wires the dialog.
//
// Subscribed via [Event("editor.created")] which fires after the editor main
// window is built and the library system has been initialised. Can also be
// invoked manually via the secbox menu (see UI/MenuItems.cs).
public static class BootAudit
{
	static bool ranThisSession;

	[Event( "editor.created" )]
	public static void OnEditorCreated()
	{
		if ( ranThisSession ) return;
		ranThisSession = true;
		Run();
	}

	public static void Run()
	{
		try { RunImpl(); }
		catch ( Exception ex )
		{
			global::Sandbox.Internal.GlobalGameNamespace.Log.Error(
				$"[secbox] boot audit threw: {ex.Message}\n{ex.StackTrace}" );
		}
	}

	static void RunImpl()
	{
		var projectRoot = PackageLocator.CurrentProjectRoot();
		if ( string.IsNullOrEmpty( projectRoot ) )
		{
			global::Sandbox.Internal.GlobalGameNamespace.Log.Trace(
				"[secbox] boot audit: no current project" );
			return;
		}

		var store = TrustStore.Load( projectRoot );
		if ( !store.Policy.ScanOnBoot )
		{
			global::Sandbox.Internal.GlobalGameNamespace.Log.Trace(
				"[secbox] boot audit: ScanOnBoot disabled" );
			return;
		}

		var libraries = LibrarySystem.All?.ToList();
		if ( libraries == null || libraries.Count == 0 )
		{
			global::Sandbox.Internal.GlobalGameNamespace.Log.Info(
				"[secbox] boot audit: no libraries installed" );
			return;
		}

		global::Sandbox.Internal.GlobalGameNamespace.Log.Info(
			$"[secbox] boot audit: scanning {libraries.Count} libraries" );

		var scanner = new PackageScanner();
		int newlyFlagged = 0;
		int reviewedSkipped = 0;

		foreach ( var lib in libraries )
		{
			var ident = SafeIdent( lib );
			if ( string.IsNullOrEmpty( ident ) ) continue;

			// Skip our own library.
			if ( ident.StartsWith( "local.secbox" ) || ident == "secbox" ) continue;

			var folder = ReflectionHelpers.GetProp( lib, "Project" ) is Project proj
				? proj.RootDirectory?.FullName
				: null;
			if ( string.IsNullOrEmpty( folder ) ) continue;

			var hash = PackageHasher.HashFolder( folder );
			var existing = store.Find( hash );
			if ( existing != null && existing.Decision is Decision.TrustAlways or Decision.Block )
			{
				reviewedSkipped++;
				continue;
			}

			var findings = scanner.ScanFolder( folder ).ToList();
			var critical = findings.Count( f => f.Severity == Severity.Critical );
			var high = findings.Count( f => f.Severity == Severity.High );
			var medium = findings.Count( f => f.Severity == Severity.Medium );
			var low = findings.Count( f => f.Severity == Severity.Low );

			global::Sandbox.Internal.GlobalGameNamespace.Log.Info(
				$"[secbox] {ident}: Critical={critical} High={high} Medium={medium} Low={low}" );

			var maxSeverity = findings.Count == 0 ? Severity.Info : findings.Max( f => f.Severity );
			if ( maxSeverity >= store.Policy.PromptThreshold )
			{
				store.Upsert( new TrustEntry
				{
					PackageIdent = ident,
					Version = null,
					ContentHash = hash,
					Decision = existing?.Decision ?? Decision.Unreviewed,
					ReviewedAt = DateTime.UtcNow,
					CriticalCount = critical,
					HighCount = high,
					MediumCount = medium,
					LowCount = low,
					Notes = $"Boot audit. First 5 findings:\n"
						+ string.Join( "\n", findings.Take( 5 ).Select( f => "  " + f.ToString() ) ),
				} );
				newlyFlagged++;
			}
		}

		if ( newlyFlagged > 0 ) store.Save();

		global::Sandbox.Internal.GlobalGameNamespace.Log.Info(
			$"[secbox] boot audit done: {newlyFlagged} flagged, {reviewedSkipped} pre-trusted/blocked, "
			+ $"{libraries.Count - newlyFlagged - reviewedSkipped} clean" );
	}

	static string SafeIdent( LibraryProject lib )
	{
		if ( lib == null ) return null;
		var proj = ReflectionHelpers.GetProp( lib, "Project" ) as Project;
		return proj?.Package?.FullIdent ?? proj?.Package?.Ident;
	}
}
