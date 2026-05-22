using System;
using System.Reflection;
using Editor;
using Sandbox.SecBox.Lifecycle;
using DiagnosticsLog = Sandbox.SecBox.Bridge.DiagnosticsLog;

namespace Sandbox.SecBox.UI;

// Re-injects a SecboxStatusPanel into the Library Manager's internal
// LibraryDetail widget. LibraryDetail is transient:
//   - LibraryManagerDock.Rebuild() replaces the dock's content widget each
//     time the engine bumps the dock's content hash.
//   - LibraryList.OnLibrarySelected fires `content.Layout.Clear( true );
//     content.Layout.Add( new LibraryDetail( library ) )` on every row click.
//   - LibraryDetail.FetchAndBuild() calls Layout.Clear( true ) on itself
//     after construction to populate header/body/buttons.
//
// One-shot injection is therefore guaranteed to be wiped. EnsureInstalled
// is called every frame from LibraryManagerInjector; it locates the current
// LibraryDetail descendant(s) and adds a SecboxStatusPanel to any that don't
// already have one as a descendant. The check is cheap and idempotent.
//
// The "currently displayed library" accessor on LibraryDetail is discovered
// once via reflection (instance field of type Package or LibraryProject) and
// cached as a MemberInfo. Per-instance resolvers close over the discovered
// member + the specific LibraryDetail instance.
internal static class LibraryDetailPanel
{
	static Type _detailType;
	static MemberInfo _selectedMember; // FieldInfo or PropertyInfo
	static Type _selectedMemberType;

	public static bool Installed { get; private set; }

	public static bool EnsureInstalled( Widget libraryManagerDock )
	{
		if ( libraryManagerDock == null || !libraryManagerDock.IsValid )
		{
			ResetState();
			return false;
		}

		if ( _detailType == null )
		{
			_detailType = ReflectionHelpers.ResolveEditorType(
				"Editor.LibraryManager.LibraryDetail", anchor: libraryManagerDock.GetType() );
			if ( _detailType == null )
			{
				DiagnosticsLog.Warn( "[secbox] LibraryDetailPanel: Editor.LibraryManager.LibraryDetail type not found - detail panel disabled" );
				return false;
			}
			DiscoverSelectedMember( _detailType );
		}

		bool any = false;
		try
		{
			foreach ( var w in libraryManagerDock.GetDescendants<Widget>() )
			{
				if ( w == null || w.GetType() != _detailType ) continue;
				if ( w.Layout == null ) continue;

				if ( HasPanel( w ) ) { any = true; continue; }

				// Don't inject into our own detail view. Same content-based
				// self-detection as BootAudit: sbproj presence is ground truth,
				// since "{org}.{ident}#local" forms (and forks) defeat prefix filters.
				if ( IsSecBoxLibrary( ResolveSelected( w ) ) ) continue;

				var detailInstance = w;
				var panel = new SecboxStatusPanel( detailInstance, () => ResolveSelected( detailInstance ) );
				w.Layout.Add( panel );
				panel.Refresh();
				any = true;

				if ( !Installed )
				{
					Installed = true;
					DiagnosticsLog.Info( "[secbox] LibraryDetailPanel installed in LibraryDetail (will re-inject as instances are recreated)" );
				}
			}
		}
		catch ( Exception ex )
		{
			DiagnosticsLog.Warn( $"[secbox] LibraryDetailPanel.EnsureInstalled: walk threw: {ex.Message}" );
		}

		// Refresh existing panels each tick so the status updates when the
		// trust store changes or when the user switches packages within the
		// same LibraryDetail instance.
		try
		{
			foreach ( var p in libraryManagerDock.GetDescendants<SecboxStatusPanel>() )
				p.Refresh();
		}
		catch { /* paint-adjacent - never propagate */ }

		return any;
	}

	static bool HasPanel( Widget detail )
	{
		try
		{
			foreach ( var d in detail.GetDescendants<SecboxStatusPanel>() )
				if ( d != null && d.IsValid ) return true;
		}
		catch { }
		return false;
	}

	static void DiscoverSelectedMember( Type detailType )
	{
		try
		{
			var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

			// Prefer LibraryProject-typed members. LibraryDetail has both a
			// `Package` field (always set, even for not-installed browse rows)
			// and an `Installed` LibraryProject field (null when the package
			// isn't locally installed). We want the latter so the SecBox panel
			// can hide itself when the user is browsing uninstalled packages.
			foreach ( var fi in detailType.GetFields( flags ) )
			{
				if ( IsLibraryProjectType( fi.FieldType ) )
				{
					_selectedMember = fi;
					_selectedMemberType = fi.FieldType;
					DiagnosticsLog.Trace( $"[secbox] LibraryDetail resolver: field '{fi.Name}' ({fi.FieldType.Name})" );
					return;
				}
			}
			foreach ( var pi in detailType.GetProperties( flags ) )
			{
				if ( IsLibraryProjectType( pi.PropertyType ) )
				{
					_selectedMember = pi;
					_selectedMemberType = pi.PropertyType;
					DiagnosticsLog.Trace( $"[secbox] LibraryDetail resolver: property '{pi.Name}' ({pi.PropertyType.Name})" );
					return;
				}
			}

			// Fallback: Package-typed member (legacy / future engine refactors).
			foreach ( var fi in detailType.GetFields( flags ) )
			{
				if ( IsLibraryType( fi.FieldType ) )
				{
					_selectedMember = fi;
					_selectedMemberType = fi.FieldType;
					DiagnosticsLog.Trace( $"[secbox] LibraryDetail resolver (fallback): field '{fi.Name}' ({fi.FieldType.Name})" );
					return;
				}
			}
			foreach ( var pi in detailType.GetProperties( flags ) )
			{
				if ( IsLibraryType( pi.PropertyType ) )
				{
					_selectedMember = pi;
					_selectedMemberType = pi.PropertyType;
					DiagnosticsLog.Trace( $"[secbox] LibraryDetail resolver (fallback): property '{pi.Name}' ({pi.PropertyType.Name})" );
					return;
				}
			}

			DiagnosticsLog.Warn( "[secbox] LibraryDetailPanel: no LibraryProject/Package-typed member found on LibraryDetail - panel will show 'no library selected'" );
		}
		catch ( Exception ex )
		{
			DiagnosticsLog.Warn( $"[secbox] LibraryDetailPanel.DiscoverSelectedMember threw: {ex.Message}" );
		}
	}

	static bool IsLibraryType( Type t )
	{
		if ( t == null ) return false;
		if ( t == typeof( LibraryProject ) ) return true;
		if ( t == typeof( Package ) ) return true;
		if ( typeof( LibraryProject ).IsAssignableFrom( t ) ) return true;
		if ( typeof( Package ).IsAssignableFrom( t ) ) return true;
		return false;
	}

	static bool IsLibraryProjectType( Type t )
	{
		if ( t == null ) return false;
		if ( t == typeof( LibraryProject ) ) return true;
		if ( typeof( LibraryProject ).IsAssignableFrom( t ) ) return true;
		return false;
	}

	static object ResolveSelected( Widget detailInstance )
	{
		if ( detailInstance == null || !detailInstance.IsValid || _selectedMember == null ) return null;
		try
		{
			return _selectedMember switch
			{
				FieldInfo fi => fi.GetValue( detailInstance ),
				PropertyInfo pi => pi.GetValue( detailInstance ),
				_ => null,
			};
		}
		catch { return null; }
	}

	static bool IsSecBoxLibrary( object lib )
	{
		if ( lib is not LibraryProject lp ) return false;
		try
		{
			var folder = lp.Project?.RootDirectory?.FullName;
			if ( string.IsNullOrEmpty( folder ) ) return false;
			return System.IO.File.Exists( System.IO.Path.Combine( folder, "secbox.sbproj" ) );
		}
		catch { return false; }
	}

	static void ResetState()
	{
		Installed = false;
		// _detailType + _selectedMember are stable across dock lifetimes - keep them cached.
	}
}
