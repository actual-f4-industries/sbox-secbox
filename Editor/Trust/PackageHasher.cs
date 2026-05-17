using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Sandbox.SecBox;

// SHA-256 over a deterministic stream of (relative path, file bytes) for every
// .dll, .cs, and .razor file inside the package folder. Ordering by
// case-insensitive relative path makes the hash reproducible across machines.
// Any byte change in any covered file changes the hash; trust decisions are
// keyed by it, so an attacker re-uploading a tampered version with the same
// PackageIdent/Version cannot inherit a prior TrustAlways.
public static class PackageHasher
{
	static readonly string[] HashedExtensions = { ".dll", ".cs", ".razor", ".cshtml" };

	public static string HashFolder( string folder )
	{
		if ( !Directory.Exists( folder ) )
			return "missing:" + folder;

		using var sha = SHA256.Create();

		var files = Directory.EnumerateFiles( folder, "*", SearchOption.AllDirectories )
			.Where( p => HashedExtensions.Contains( Path.GetExtension( p ).ToLowerInvariant() ) )
			.Select( p => (rel: Path.GetRelativePath( folder, p ).Replace( '\\', '/' ).ToLowerInvariant(), full: p) )
			.OrderBy( t => t.rel, StringComparer.Ordinal )
			.ToList();

		using var stream = new MemoryStream();
		foreach ( var (rel, full) in files )
		{
			var pathBytes = Encoding.UTF8.GetBytes( rel + "\n" );
			stream.Write( pathBytes, 0, pathBytes.Length );
			try
			{
				var contentBytes = File.ReadAllBytes( full );
				stream.Write( contentBytes, 0, contentBytes.Length );
			}
			catch
			{
				var marker = Encoding.UTF8.GetBytes( "[unreadable]\n" );
				stream.Write( marker, 0, marker.Length );
			}
		}

		stream.Position = 0;
		var hash = sha.ComputeHash( stream );

		var sb = new StringBuilder( hash.Length * 2 );
		for ( int i = 0; i < hash.Length; i++ )
			sb.Append( hash[i].ToString( "x2" ) );
		return sb.ToString();
	}
}
