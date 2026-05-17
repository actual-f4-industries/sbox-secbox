using System.Reflection.Metadata;

namespace Sandbox.SecBox;

// Formats System.Reflection.Metadata handles into the engine's AccessControl
// key format so the rule strings ported from Sandbox.Access match unchanged:
//   type   →  "{AssemblyName}/{TypeFullName}"
//   method →  "{AssemblyName}/{TypeFullName}.{MethodName}"
//
// Note: we used to add parameter signatures (engine does) but resolving
// signatures via S.R.M is significantly more work than via Cecil. v0.1 omits
// parameter list — rules with explicit signatures will still match the prefix
// pattern thanks to regex `*` wildcards already used throughout EngineRules.cs.
public static class MemberKey
{
	public static string ForType( MetadataReader md, TypeReferenceHandle handle )
	{
		var typeRef = md.GetTypeReference( handle );
		var ns = md.GetString( typeRef.Namespace );
		var name = md.GetString( typeRef.Name );
		var asmName = AssemblyOf( md, typeRef.ResolutionScope );
		return string.IsNullOrEmpty( ns )
			? $"{asmName}/{name}"
			: $"{asmName}/{ns}.{name}";
	}

	public static string ForMember( MetadataReader md, MemberReferenceHandle handle )
	{
		var memberRef = md.GetMemberReference( handle );
		var memberName = md.GetString( memberRef.Name );

		string parentKey;
		switch ( memberRef.Parent.Kind )
		{
			case HandleKind.TypeReference:
				parentKey = ForType( md, (TypeReferenceHandle)memberRef.Parent );
				break;
			case HandleKind.TypeSpecification:
				// Generic instantiations — resolve to underlying type ref via
				// signature decoding (lossy for v0.1; fall back to "<spec>").
				parentKey = "<unknown>/<TypeSpec>";
				break;
			case HandleKind.MethodDefinition:
				// vararg sentinel — rare; emit unknown.
				parentKey = "<unknown>/<MethodDef>";
				break;
			default:
				parentKey = "<unknown>/<unknown>";
				break;
		}

		return $"{parentKey}.{memberName}";
	}

	public static string AssemblyOf( MetadataReader md, EntityHandle scope )
	{
		switch ( scope.Kind )
		{
			case HandleKind.AssemblyReference:
				var asmRef = md.GetAssemblyReference( (AssemblyReferenceHandle)scope );
				return md.GetString( asmRef.Name );
			case HandleKind.ModuleReference:
				var modRef = md.GetModuleReference( (ModuleReferenceHandle)scope );
				return md.GetString( modRef.Name );
			case HandleKind.TypeReference:
				// Nested type — recurse upward to find the outermost scope.
				var nested = md.GetTypeReference( (TypeReferenceHandle)scope );
				return AssemblyOf( md, nested.ResolutionScope );
			case HandleKind.AssemblyDefinition:
				var def = md.GetAssemblyDefinition();
				return md.GetString( def.Name );
			default:
				return "<unknown>";
		}
	}
}
