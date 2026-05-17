using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Sandbox.SecBox.Rules;

public sealed class RuleSet
{
	public List<Regex> Whitelist { get; } = new();
	public List<Regex> Blacklist { get; } = new();
	public HashSet<string> AssemblyWhitelist { get; } = new();

	public void AddAssembly( string asmName ) => AssemblyWhitelist.Add( asmName );

	public void AddRules( IEnumerable<string> rules )
	{
		foreach ( var line in rules )
			AddRule( line );
	}

	public void AddRule( string line )
	{
		var pattern = line.Trim();
		if ( string.IsNullOrEmpty( pattern ) )
			return;

		bool deny = pattern.StartsWith( '!' );
		if ( deny ) pattern = pattern[1..];

		pattern = Regex.Escape( pattern ).Replace( "\\*", ".*" );
		pattern = $"^{pattern}$";

		var rx = new Regex( pattern, RegexOptions.Compiled );
		( deny ? Blacklist : Whitelist ).Add( rx );
	}

	public bool IsAllowed( string memberPath )
	{
		if ( Blacklist.Any( r => r.IsMatch( memberPath ) ) )
			return false;
		return Whitelist.Any( r => r.IsMatch( memberPath ) );
	}

	public bool IsAssemblyAllowed( string asmName ) => AssemblyWhitelist.Contains( asmName );

	public Regex FirstMatchingPattern( string memberPath )
	{
		return Blacklist.FirstOrDefault( r => r.IsMatch( memberPath ) )
			?? Whitelist.FirstOrDefault( r => r.IsMatch( memberPath ) );
	}
}
