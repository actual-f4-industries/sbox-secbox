using System.Linq;
using Editor;
using Sandbox.SecBox.Lifecycle;

namespace Sandbox.SecBox.UI;

// Top-level menu entries under "secbox/" in the editor menu bar. Lets users
// trigger scans, open the trust store, and re-arm hooks manually.
public static class MenuItems
{
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
}
