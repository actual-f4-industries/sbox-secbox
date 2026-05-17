using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Sandbox.SecBox;

// Orchestrates scanning of an installed package folder. Walks the tree, finds
// .NET assemblies and source files, dispatches to specialist scanners, and
// flags native unmanaged DLLs that ship inside the package (a Critical
// finding all on its own — there's no legitimate reason for a managed library
// package to ship unmanaged binaries).
public sealed class PackageScanner
{
	static readonly string[] NativeExtensions = { ".so", ".dylib" };
	static readonly string[] AssemblyExtensions = { ".dll" };
	static readonly string[] SourceExtensions = { ".cs" };

	public IEnumerable<Finding> ScanFolder( string packageFolder )
	{
		if ( !Directory.Exists( packageFolder ) )
		{
			yield return new Finding( Severity.Medium, "package.missing",
				"Package folder does not exist", packageFolder );
			yield break;
		}

		var asmScanner = new AssemblyScanner();
		var sourceScanner = new SourceScanner();

		foreach ( var path in Directory.EnumerateFiles( packageFolder, "*", SearchOption.AllDirectories ) )
		{
			// Skip our own build output / intermediate folders inside packages.
			var rel = RelPath( packageFolder, path );
			if ( rel.Contains( "\\obj\\" ) || rel.Contains( "/obj/" )
			     || rel.Contains( "\\bin\\" ) || rel.Contains( "/bin/" ) )
				continue;

			var ext = Path.GetExtension( path ).ToLowerInvariant();

			if ( AssemblyExtensions.Contains( ext ) )
			{
				if ( !IsManagedAssembly( path ) )
				{
					yield return new Finding( Severity.Critical, "package.native-dll",
						"Unmanaged native DLL shipped inside package — opaque to scanner; recommend uninstall",
						rel );
					continue;
				}

				foreach ( var f in asmScanner.Scan( path ) )
					yield return f with { Location = $"{rel} :: {f.Location}" };
				continue;
			}

			if ( SourceExtensions.Contains( ext ) )
			{
				foreach ( var f in sourceScanner.Scan( path ) )
					yield return f;
				continue;
			}

			if ( NativeExtensions.Contains( ext ) )
			{
				yield return new Finding( Severity.Critical, "package.native-binary",
					$"Unmanaged native binary ({ext}) shipped inside package",
					rel );
			}
		}
	}

	static string RelPath( string root, string full )
	{
		try { return Path.GetRelativePath( root, full ); }
		catch { return full; }
	}

	// Cheap PE-header sniff: PE files have an MZ stub followed by a PE header
	// whose 'NumberOfRvaAndSizes' fixed offset points at a CLI directory entry.
	// We just check for the CLI header presence — a managed .dll has a non-zero
	// CLI directory; a native .dll has a zero CLI directory.
	static bool IsManagedAssembly( string path )
	{
		try
		{
			using var fs = File.OpenRead( path );
			using var br = new BinaryReader( fs );

			fs.Position = 0x3C;
			var peHeaderOffset = br.ReadInt32();
			if ( peHeaderOffset <= 0 || peHeaderOffset > fs.Length - 4 ) return false;

			fs.Position = peHeaderOffset;
			if ( br.ReadUInt32() != 0x00004550 ) return false; // "PE\0\0"

			fs.Position = peHeaderOffset + 4 + 20; // skip COFF header
			var magic = br.ReadUInt16();
			int cliDirOffset = peHeaderOffset + 4 + 20 + ( magic == 0x20B ? 112 + 14 * 8 : 96 + 14 * 8 );

			fs.Position = cliDirOffset;
			var rva = br.ReadUInt32();
			var size = br.ReadUInt32();
			return rva != 0 && size != 0;
		}
		catch
		{
			return false;
		}
	}
}
