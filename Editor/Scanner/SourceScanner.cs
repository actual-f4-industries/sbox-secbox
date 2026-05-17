using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Sandbox.SecBox.Rules;

namespace Sandbox.SecBox;

// Source-level scanner for .cs files. Uses Roslyn syntax tree only (no semantic
// model) — this means it works without a buildable compilation but is fuzzier
// than the Cecil scanner. The binary scanner is authoritative; source findings
// are early-warning signals for packages that ship source-only.
//
// What it flags textually:
//   - `using` directives importing suspect namespaces  (Low)
//   - Attribute usage matching critical attribute names (Critical)
//   - Identifier/type references containing critical names (High)
//   - String literals matching SuspiciousStringLiterals  (High)
//
// False positives are expected; this is triage, not enforcement. The user gets
// to see both source and binary findings in the review dialog.
public sealed class SourceScanner
{
	static readonly string[] SuspectNamespaces =
	{
		"System.IO",
		"System.Diagnostics",                  // Process lives here
		"System.Runtime.InteropServices",      // P/Invoke, Marshal
		"System.Reflection.Emit",
		"System.Runtime.Loader",               // AssemblyLoadContext
		"System.Net.Sockets",
		"Microsoft.Win32",                     // Registry
		"Microsoft.CodeAnalysis.CSharp.Scripting",
		"Microsoft.CodeAnalysis.Scripting",
	};

	// Critical: these type/method/attribute names trigger Critical findings on
	// any source-text occurrence, regardless of surrounding namespace.
	static readonly string[] CriticalIdentifiers =
	{
		"DllImport", "LibraryImport", "UnsafeAccessor",
		"Process", "ProcessStartInfo",
		"AssemblyLoadContext",
		"LoadFile", "LoadFrom", "UnsafeLoadFrom",
		"GetDelegateForFunctionPointer", "GetFunctionPointerForDelegate",
		"NativeLibrary",
		"CSharpScript", "ScriptOptions",
	};

	// High: dangerous but sometimes legitimate; binary scanner gives stricter
	// verdict via signature matching.
	static readonly string[] HighIdentifiers =
	{
		"File", "Directory", "DirectoryInfo", "FileInfo", "FileStream",
		"FileSystemWatcher", "DriveInfo", "IsolatedStorage",
		"WebClient", "WebRequest", "HttpListener", "TcpClient", "UdpClient",
		"Socket", "Dns",
		"Registry", "RegistryKey",
		"Activator",
		"MethodInfo", "ConstructorInfo", "FieldInfo", "PropertyInfo",
	};

	public IEnumerable<Finding> Scan( string sourcePath )
	{
		if ( !File.Exists( sourcePath ) )
			yield break;

		string text = null;
		string readError = null;
		try { text = File.ReadAllText( sourcePath ); }
		catch ( Exception ex ) { readError = ex.Message; }
		if ( text == null )
		{
			yield return new Finding( Severity.Low, "scan.source-read-failed",
				$"Could not read source: {readError}", sourcePath );
			yield break;
		}

		SyntaxTree tree = null;
		string parseError = null;
		try { tree = CSharpSyntaxTree.ParseText( text, path: sourcePath ); }
		catch ( Exception ex ) { parseError = ex.Message; }
		if ( tree == null )
		{
			yield return new Finding( Severity.Low, "scan.source-parse-failed",
				$"Could not parse source: {parseError}", sourcePath );
			yield break;
		}

		var walker = new Walker( sourcePath );
		walker.Visit( tree.GetRoot() );
		foreach ( var f in walker.Findings )
			yield return f;
	}

	sealed class Walker : CSharpSyntaxWalker
	{
		readonly string path;
		public List<Finding> Findings { get; } = new();

		public Walker( string p ) : base( SyntaxWalkerDepth.Node ) { path = p; }

		string LocAt( SyntaxNode n )
		{
			var span = n.GetLocation().GetLineSpan();
			return $"{path}:{span.StartLinePosition.Line + 1}";
		}

		public override void VisitUsingDirective( UsingDirectiveSyntax node )
		{
			var name = node.Name?.ToString();
			if ( name == null ) { base.VisitUsingDirective( node ); return; }

			foreach ( var ns in SuspectNamespaces )
			{
				if ( name == ns || name.StartsWith( ns + "." ) )
				{
					Findings.Add( new Finding( Severity.Low, "source.suspect-using",
						$"Imports suspect namespace: {name}", LocAt( node ) ) );
					break;
				}
			}

			base.VisitUsingDirective( node );
		}

		public override void VisitAttribute( AttributeSyntax node )
		{
			var name = node.Name?.ToString() ?? "";
			foreach ( var crit in CriticalIdentifiers )
			{
				if ( name == crit || name.EndsWith( "." + crit ) )
				{
					Findings.Add( new Finding( Severity.Critical, "source.critical-attr",
						$"Attribute usage: [{name}] — strong attack signal",
						LocAt( node ) ) );
					break;
				}
			}
			base.VisitAttribute( node );
		}

		public override void VisitIdentifierName( IdentifierNameSyntax node )
		{
			var ident = node.Identifier.ValueText;

			foreach ( var crit in CriticalIdentifiers )
			{
				if ( ident == crit )
				{
					Findings.Add( new Finding( Severity.Critical, "source.critical-ident",
						$"Identifier reference: {ident}", LocAt( node ) ) );
					break;
				}
			}

			foreach ( var high in HighIdentifiers )
			{
				if ( ident == high )
				{
					Findings.Add( new Finding( Severity.High, "source.high-ident",
						$"Identifier reference: {ident} — verify in binary scan",
						LocAt( node ) ) );
					break;
				}
			}

			base.VisitIdentifierName( node );
		}

		public override void VisitLiteralExpression( LiteralExpressionSyntax node )
		{
			if ( node.IsKind( SyntaxKind.StringLiteralExpression ) )
			{
				var s = node.Token.ValueText ?? "";
				foreach ( var needle in CriticalRules.SuspiciousStringLiterals )
				{
					if ( s.IndexOf( needle, StringComparison.OrdinalIgnoreCase ) >= 0 )
					{
						Findings.Add( new Finding( Severity.High, "source.suspicious-literal",
							$"String literal contains \"{needle}\"", LocAt( node ) ) );
						break;
					}
				}
			}
			base.VisitLiteralExpression( node );
		}
	}
}
