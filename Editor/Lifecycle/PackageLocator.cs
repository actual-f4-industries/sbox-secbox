using System;
using System.IO;
using Editor;

namespace Sandbox.SecBox.Lifecycle;

// Locates a Package's on-disk folder so we can scan it. The package metadata
// from the engine tells us version/ident but not directly where the files
// landed. We probe a few canonical locations:
//   1. Project.Current.RootDirectory / "Libraries" / <ident> — for libraries
//      added to the open project via Library Manager.
//   2. The engine's package download cache (typically under steamapps/sbox/data).
//
// Returns null if we can't locate it — caller logs and skips scanning rather
// than crashing.
internal static class PackageLocator
{
	public static string FolderFor( Package pkg )
	{
		if ( pkg == null ) return null;

		var ident = pkg.FullIdent ?? pkg.Ident;
		if ( string.IsNullOrEmpty( ident ) ) return null;

		// Library Manager installs land here for the open project.
		var proj = Project.Current;
		if ( proj?.RootDirectory != null )
		{
			var libRoot = Path.Combine( proj.RootDirectory.FullName, "Libraries" );
			if ( Directory.Exists( libRoot ) )
			{
				// Try ident verbatim, also try last segment (sometimes folders
				// are <org>.<name> sometimes just <name>).
				var direct = Path.Combine( libRoot, ident );
				if ( Directory.Exists( direct ) ) return direct;

				var lastSegment = ident.Contains( '.' )
					? ident.Substring( ident.LastIndexOf( '.' ) + 1 )
					: ident;
				var bySegment = Path.Combine( libRoot, lastSegment );
				if ( Directory.Exists( bySegment ) ) return bySegment;

				// LocalPackage exposes CodePath/ContentPath directly via
				// reflection (the type is internal to the engine).
				var codePath = ReflectionHelpers.GetProp( pkg, "CodePath" ) as string;
				if ( !string.IsNullOrEmpty( codePath ) && Directory.Exists( codePath ) )
				{
					// CodePath is usually <root>/Code — return the parent so
					// we scan the whole library.
					var parent = Path.GetDirectoryName( codePath );
					if ( !string.IsNullOrEmpty( parent ) && Directory.Exists( parent ) )
						return parent;
				}
			}
		}

		return null;
	}

	public static string CurrentProjectRoot()
	{
		try { return Project.Current?.RootDirectory?.FullName; }
		catch { return null; }
	}
}
