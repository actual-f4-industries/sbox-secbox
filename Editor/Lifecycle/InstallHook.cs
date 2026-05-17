using System;
using System.Linq;
using System.Reflection;

namespace Sandbox.SecBox.Lifecycle;

// Subscribes to PackageManager.OnPackageInstalledToContext via reflection
// (PackageManager is internal, no public alternative for code packages).
// Tries to insert at the front of the delegate chain so we run before
// ToolsDll/GameInstanceDll — which lets a synchronous scan + dialog block
// before assembly load. Falls back to appending if the field-level trick
// fails on a future engine version.
public static class InstallHook
{
	const string EventName = "OnPackageInstalledToContext";
	static bool subscribed;

	public static void Subscribe()
	{
		if ( subscribed ) return;

		var pmType = ReflectionHelpers.PackageManagerType();
		if ( pmType == null )
		{
			global::Sandbox.Internal.GlobalGameNamespace.Log.Warning(
				"[secbox] PackageManager type not found — install hook disabled" );
			return;
		}

		// Build the delegate matching Action<ActivePackage, string>. We can't
		// name ActivePackage statically (internal), so use Action<object,string>
		// and rely on runtime variance via DynamicInvoke — actually no, the
		// CLR rejects that. Use Delegate.CreateDelegate against the event's
		// real type.
		var ev = pmType.GetEvent( EventName, BindingFlags.Public | BindingFlags.Static );
		if ( ev == null )
		{
			global::Sandbox.Internal.GlobalGameNamespace.Log.Warning(
				$"[secbox] {EventName} event not found on PackageManager — install hook disabled" );
			return;
		}

		var ourMethod = typeof( InstallHook ).GetMethod( nameof( OnPackageInstalled ),
			BindingFlags.NonPublic | BindingFlags.Static );

		Delegate handler;
		try
		{
			handler = Delegate.CreateDelegate( ev.EventHandlerType, ourMethod );
		}
		catch ( Exception ex )
		{
			global::Sandbox.Internal.GlobalGameNamespace.Log.Warning(
				$"[secbox] could not bind install handler: {ex.Message}" );
			return;
		}

		if ( ReflectionHelpers.InsertFirstInChain( pmType, EventName, handler ) )
		{
			global::Sandbox.Internal.GlobalGameNamespace.Log.Info(
				"[secbox] install hook armed (first-in-chain)" );
		}
		else if ( ReflectionHelpers.AppendToChain( pmType, EventName, handler ) )
		{
			global::Sandbox.Internal.GlobalGameNamespace.Log.Warning(
				"[secbox] install hook armed (appended — pre-load gating may not work)" );
		}
		else
		{
			global::Sandbox.Internal.GlobalGameNamespace.Log.Warning(
				"[secbox] install hook could not subscribe; new packages will not be scanned" );
			return;
		}

		subscribed = true;
	}

	// Engine calls this with (ActivePackage, tag). ActivePackage is internal
	// to Sandbox.Engine, so we receive it as object and reflect for properties.
	static void OnPackageInstalled( object activePackage, string tag )
	{
		try
		{
			HandleInstall( activePackage, tag );
		}
		catch ( Exception ex )
		{
			global::Sandbox.Internal.GlobalGameNamespace.Log.Error(
				$"[secbox] install handler threw: {ex.Message}\n{ex.StackTrace}" );
		}
	}

	static void HandleInstall( object activePackage, string tag )
	{
		var pkg = ReflectionHelpers.GetProp( activePackage, "Package" ) as Package;
		if ( pkg == null )
		{
			global::Sandbox.Internal.GlobalGameNamespace.Log.Trace(
				$"[secbox] install handler: no Package on ActivePackage for tag={tag}" );
			return;
		}

		// Skip our own package and engine-internal "local" infrastructure tags
		// that fire dozens of times during boot. Real user installs are
		// either "game" or "tool" context.
		var ident = pkg.FullIdent ?? pkg.Ident ?? "<unknown>";
		if ( ident.StartsWith( "local.secbox" ) ) return;

		global::Sandbox.Internal.GlobalGameNamespace.Log.Info(
			$"[secbox] package install: ident={ident} tag={tag}" );

		var projectRoot = PackageLocator.CurrentProjectRoot();
		if ( string.IsNullOrEmpty( projectRoot ) )
		{
			global::Sandbox.Internal.GlobalGameNamespace.Log.Trace(
				"[secbox] no current project — skipping scan" );
			return;
		}

		var store = TrustStore.Load( projectRoot );
		if ( !store.Policy.ScanOnInstall )
		{
			global::Sandbox.Internal.GlobalGameNamespace.Log.Trace(
				"[secbox] ScanOnInstall disabled — skipping" );
			return;
		}

		var folder = PackageLocator.FolderFor( pkg );
		if ( string.IsNullOrEmpty( folder ) )
		{
			global::Sandbox.Internal.GlobalGameNamespace.Log.Trace(
				$"[secbox] couldn't locate package folder for {ident} — skipping scan" );
			return;
		}

		var hash = PackageHasher.HashFolder( folder );
		var existing = store.Find( hash );
		if ( existing != null )
		{
			switch ( existing.Decision )
			{
				case Decision.TrustAlways:
					global::Sandbox.Internal.GlobalGameNamespace.Log.Info(
						$"[secbox] {ident} matches TrustAlways entry; allowing" );
					return;
				case Decision.Block:
					global::Sandbox.Internal.GlobalGameNamespace.Log.Error(
						$"[secbox] {ident} matches Block entry; should refuse install (uninstall path TBD in task #5b)" );
					return;
			}
		}

		var scanner = new PackageScanner();
		var findings = scanner.ScanFolder( folder ).ToList();

		var critical = findings.Count( f => f.Severity == Severity.Critical );
		var high = findings.Count( f => f.Severity == Severity.High );
		var medium = findings.Count( f => f.Severity == Severity.Medium );
		var low = findings.Count( f => f.Severity == Severity.Low );

		global::Sandbox.Internal.GlobalGameNamespace.Log.Info(
			$"[secbox] scan {ident}: Critical={critical} High={high} Medium={medium} Low={low}" );

		var maxSeverity = findings.Count == 0
			? Severity.Info
			: findings.Max( f => f.Severity );

		if ( maxSeverity >= store.Policy.PromptThreshold )
		{
			// Persist as Unreviewed first so a crash mid-dialog still leaves
			// a trail in the trust store. The dialog flips to TrustAlways on
			// the user's positive confirmation.
			store.Upsert( new TrustEntry
			{
				PackageIdent = ident,
				Version = pkg.Revision?.VersionId.ToString(),
				ContentHash = hash,
				Decision = Decision.Unreviewed,
				ReviewedAt = DateTime.UtcNow,
				CriticalCount = critical,
				HighCount = high,
				MediumCount = medium,
				LowCount = low,
				Notes = $"Auto-recorded by install hook. First 5 findings:\n"
					+ string.Join( "\n", findings.Take( 5 ).Select( f => "  " + f.ToString() ) ),
			} );
			store.Save();

			global::Sandbox.Internal.GlobalGameNamespace.Log.Warning(
				$"[secbox] {ident}: {critical} critical / {high} high findings — opening review dialog" );

			Sandbox.SecBox.UI.ReviewDialog.Show( ident, hash, findings, store );
		}
	}
}
