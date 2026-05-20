using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sandbox.SecBox;

// JSON-backed trust store. Persists to <projectRoot>/.secbox/trust.json.
// Layout:
//   { "Policy": {...}, "Entries": [TrustEntry, ...] }
// Hand-editable (intentionally) so users can revoke or audit without UI.
public sealed class TrustStore
{
	const string Subfolder = ".secbox";
	const string FileName = "trust.json";

	static readonly JsonSerializerOptions JsonOpts = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		Converters = { new JsonStringEnumConverter() },
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	public Policy Policy { get; set; } = new();
	public List<TrustEntry> Entries { get; set; } = new();

	// Bumped on every Save() so listeners (status panel) can detect changes
	// without rereading the file or comparing entry contents.
	static int _version;
	public static int Version => System.Threading.Volatile.Read( ref _version );

	[JsonIgnore]
	public string FilePath { get; private set; }

	public static TrustStore Load( string projectRoot )
	{
		var path = Path.Combine( projectRoot, Subfolder, FileName );
		if ( !File.Exists( path ) )
			return new TrustStore { FilePath = path };

		try
		{
			var json = File.ReadAllText( path );
			var store = JsonSerializer.Deserialize<TrustStore>( json, JsonOpts ) ?? new TrustStore();
			store.FilePath = path;
			store.Policy ??= new Policy();
			store.Entries ??= new List<TrustEntry>();
			return store;
		}
		catch ( Exception ex )
		{
			// Corrupt store: preserve the file under a sidecar name so user
			// can recover, return a fresh store rather than crashing.
			try { File.Move( path, path + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true ); }
			catch { }
			global::Sandbox.Internal.GlobalGameNamespace.Log.Warning( $"[secbox] trust store unreadable ({ex.Message}); starting fresh" );
			return new TrustStore { FilePath = path };
		}
	}

	public void Save()
	{
		if ( string.IsNullOrEmpty( FilePath ) )
			throw new InvalidOperationException( "TrustStore.FilePath not set; call Load first or construct via Create()." );

		var dir = Path.GetDirectoryName( FilePath );
		if ( !string.IsNullOrEmpty( dir ) && !Directory.Exists( dir ) )
			Directory.CreateDirectory( dir );

		var json = JsonSerializer.Serialize( this, JsonOpts );
		File.WriteAllText( FilePath, json );
		System.Threading.Interlocked.Increment( ref _version );
	}

	// Look up by hash. The hash is the authoritative key.
	public TrustEntry Find( string contentHash )
	{
		for ( int i = 0; i < Entries.Count; i++ )
		{
			if ( Entries[i].ContentHash == contentHash )
				return Entries[i];
		}
		return null;
	}

	public TrustEntry Upsert( TrustEntry entry )
	{
		for ( int i = 0; i < Entries.Count; i++ )
		{
			if ( Entries[i].ContentHash == entry.ContentHash )
			{
				Entries[i] = entry;
				return entry;
			}
		}
		Entries.Add( entry );
		return entry;
	}

	public bool Remove( string contentHash )
	{
		for ( int i = 0; i < Entries.Count; i++ )
		{
			if ( Entries[i].ContentHash == contentHash )
			{
				Entries.RemoveAt( i );
				return true;
			}
		}
		return false;
	}
}
