using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Sandbox.SecBox.Rules;

namespace Sandbox.SecBox;

// Scans a .NET assembly's metadata tables using System.Reflection.Metadata
// (BCL — always available, unlike Mono.Cecil which the editor strips from the
// regenerated csproj).
//
// v0.1 scope: enumerate every type reference, member reference, and method
// definition's attributes / P/Invoke flag. Match against EngineRules
// (engine-mirror whitelist — anything not allowed → Medium) and CriticalRules
// (always-flag patterns → Critical). Also flag P/Invoke methods by the
// MethodAttributes.PInvokeImpl flag (does not require attribute resolution).
//
// v0.1 explicitly does NOT walk individual IL instructions — that needs
// signature/IL decoding work we deferred. Loses: in-binary suspicious-string
// detection, per-instruction location info. Source scanner still catches
// suspicious strings in .cs files.
public sealed class AssemblyScanner
{
	readonly RuleSet engineRules = EngineRules.Build();
	readonly RuleSet criticalRules = CriticalRules.BuildMatcher();
	readonly HashSet<string> seenKeys = new();

	public IEnumerable<Finding> Scan( string dllPath )
	{
		if ( !File.Exists( dllPath ) )
			yield break;

		FileStream stream = null;
		PEReader pe = null;
		MetadataReader md = null;
		string readError = null;
		string localAsmName = null;

		try
		{
			stream = File.OpenRead( dllPath );
			pe = new PEReader( stream );
			if ( !pe.HasMetadata )
				readError = "no managed metadata";
			else
			{
				md = pe.GetMetadataReader();
				localAsmName = md.GetString( md.GetAssemblyDefinition().Name );
			}
		}
		catch ( Exception ex )
		{
			readError = ex.Message;
		}

		if ( md == null )
		{
			yield return new Finding( Severity.Medium, "scan.read-failed",
				$"Could not read assembly: {readError}", dllPath );
			pe?.Dispose();
			stream?.Dispose();
			yield break;
		}

		seenKeys.Clear();

		try
		{
			foreach ( var f in ScanTypeReferences( md, localAsmName ) ) yield return f;
			foreach ( var f in ScanMemberReferences( md, localAsmName ) ) yield return f;
			foreach ( var f in ScanMethodDefinitions( md ) ) yield return f;
			foreach ( var f in ScanCustomAttributes( md, localAsmName ) ) yield return f;
			foreach ( var f in ScanTypeDefinitions( md ) ) yield return f;
		}
		finally
		{
			pe.Dispose();
			stream.Dispose();
		}
	}

	IEnumerable<Finding> ScanTypeReferences( MetadataReader md, string localAsmName )
	{
		foreach ( var handle in md.TypeReferences )
		{
			var key = MemberKey.ForType( md, handle );
			if ( !seenKeys.Add( key ) ) continue;

			var asmName = MemberKey.AssemblyOf( md, md.GetTypeReference( handle ).ResolutionScope );
			if ( asmName == localAsmName ) continue;

			foreach ( var f in Classify( key, asmName, location: "(type ref)" ) )
				yield return f;
		}
	}

	IEnumerable<Finding> ScanMemberReferences( MetadataReader md, string localAsmName )
	{
		foreach ( var handle in md.MemberReferences )
		{
			var memberRef = md.GetMemberReference( handle );
			var key = MemberKey.ForMember( md, handle );
			if ( !seenKeys.Add( key ) ) continue;

			string asmName = "<unknown>";
			if ( memberRef.Parent.Kind == HandleKind.TypeReference )
				asmName = MemberKey.AssemblyOf( md, md.GetTypeReference( (TypeReferenceHandle)memberRef.Parent ).ResolutionScope );

			if ( asmName == localAsmName ) continue;

			foreach ( var f in Classify( key, asmName, location: "(member ref)" ) )
				yield return f;
		}
	}

	IEnumerable<Finding> ScanMethodDefinitions( MetadataReader md )
	{
		// Detect P/Invoke directly via the PInvokeImpl flag — no attribute
		// resolution needed, can't be evaded with a custom DllImport copy.
		foreach ( var handle in md.MethodDefinitions )
		{
			var methodDef = md.GetMethodDefinition( handle );
			if ( ( methodDef.Attributes & MethodAttributes.PinvokeImpl ) == 0 ) continue;

			var name = md.GetString( methodDef.Name );
			var typeHandle = methodDef.GetDeclaringType();
			var typeDef = md.GetTypeDefinition( typeHandle );
			var typeFqn = $"{md.GetString( typeDef.Namespace )}.{md.GetString( typeDef.Name )}";

			string target = "(unresolved)";
			try
			{
				var importInfo = methodDef.GetImport();
				if ( !importInfo.Module.IsNil )
				{
					var mod = md.GetModuleReference( importInfo.Module );
					var entryPoint = importInfo.Name.IsNil ? name : md.GetString( importInfo.Name );
					target = $"{md.GetString( mod.Name )}!{entryPoint}";
				}
			}
			catch { }

			yield return new Finding( Severity.Critical, "method.pinvoke",
				$"P/Invoke to native code: {target}", $"{typeFqn}::{name}" );
		}
	}

	IEnumerable<Finding> ScanCustomAttributes( MetadataReader md, string localAsmName )
	{
		foreach ( var handle in md.CustomAttributes )
		{
			var ca = md.GetCustomAttribute( handle );

			// Resolve the attribute type via the constructor handle's parent.
			string attrTypeKey = ResolveAttributeTypeKey( md, ca.Constructor );
			if ( attrTypeKey == null ) continue;

			if ( criticalRules.IsAllowed( attrTypeKey ) )
			{
				yield return new Finding( Severity.Critical, "attr.critical",
					$"Critical attribute: {attrTypeKey}", "(custom attribute)" );
			}
		}
	}

	IEnumerable<Finding> ScanTypeDefinitions( MetadataReader md )
	{
		foreach ( var handle in md.TypeDefinitions )
		{
			var typeDef = md.GetTypeDefinition( handle );
			var name = md.GetString( typeDef.Name );
			var ns = md.GetString( typeDef.Namespace );
			var fqn = string.IsNullOrEmpty( ns ) ? name : $"{ns}.{name}";

			// ExplicitLayout types — used for memory aliasing.
			if ( ( typeDef.Attributes & TypeAttributes.ExplicitLayout ) != 0
			     && !( name.StartsWith( "__StaticArrayInitTypeSize=" ) ) )
			{
				yield return new Finding( Severity.High, "type.explicit-layout",
					"ExplicitLayout type — can alias memory and bypass type safety",
					fqn );
			}
		}
	}

	static string ResolveAttributeTypeKey( MetadataReader md, EntityHandle ctorHandle )
	{
		switch ( ctorHandle.Kind )
		{
			case HandleKind.MemberReference:
				var mref = md.GetMemberReference( (MemberReferenceHandle)ctorHandle );
				if ( mref.Parent.Kind == HandleKind.TypeReference )
					return MemberKey.ForType( md, (TypeReferenceHandle)mref.Parent );
				return null;
			case HandleKind.MethodDefinition:
				var mdef = md.GetMethodDefinition( (MethodDefinitionHandle)ctorHandle );
				var typeHandle = mdef.GetDeclaringType();
				var typeDef = md.GetTypeDefinition( typeHandle );
				var ns = md.GetString( typeDef.Namespace );
				var n = md.GetString( typeDef.Name );
				var asmName = md.GetString( md.GetAssemblyDefinition().Name );
				return string.IsNullOrEmpty( ns ) ? $"{asmName}/{n}" : $"{asmName}/{ns}.{n}";
			default:
				return null;
		}
	}

	IEnumerable<Finding> Classify( string key, string asmName, string location )
	{
		if ( criticalRules.IsAllowed( key ) )
		{
			yield return new Finding( Severity.Critical, "rule.critical",
				$"Hits critical rule: {key}", location );
			yield break;
		}

		if ( engineRules.IsAssemblyAllowed( asmName ) )
		{
			if ( !engineRules.IsAllowed( key ) )
			{
				yield return new Finding( Severity.Medium, "rule.engine-not-whitelisted",
					$"Member not on engine game-side whitelist: {key}", location );
			}
		}
		else
		{
			yield return new Finding( Severity.Medium, "rule.foreign-assembly",
				$"Reference into non-whitelisted assembly: {asmName} ({key})", location );
		}
	}
}
