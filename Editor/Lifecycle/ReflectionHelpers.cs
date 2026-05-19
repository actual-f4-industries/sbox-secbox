using System;
using System.Linq;
using System.Reflection;

namespace Sandbox.SecBox.Lifecycle;

// Reflection-only helpers for poking at engine internals secbox needs to wire
// into. Kept in one place so policy reviewers can audit the unsafe surface
// area quickly. Every method here documents *why* the engine doesn't expose a
// public alternative.
internal static class ReflectionHelpers
{
	// Locate Sandbox.PackageManager (internal static class). Returns null if
	// the engine renames or removes it — caller must handle gracefully.
	public static Type PackageManagerType()
	{
		// First try direct: Sandbox.Engine assembly is loaded by the time secbox
		// runs (we reference it). Scan loaded assemblies for the type.
		foreach ( var asm in AppDomain.CurrentDomain.GetAssemblies() )
		{
			if ( asm.GetName().Name != "Sandbox.Engine" ) continue;
			var t = asm.GetType( "Sandbox.PackageManager", throwOnError: false );
			if ( t != null ) return t;
		}
		return Type.GetType( "Sandbox.PackageManager, Sandbox.Engine", throwOnError: false );
	}

	// Insert a handler at the *front* of a field-like event's invocation list,
	// even though the C# `+=` operator can only append. The backing field of a
	// field-like event has the same name as the event and is typically private
	// static. We swap the field directly so our handler runs first.
	//
	// Why this matters: GameInstanceDll/ToolsDll subscribe at engine boot. If
	// secbox subscribes normally it runs LAST in the chain — by which point
	// the engine has already called LoadPackage and the package's static
	// constructors have run. Running first lets a synchronous scan + modal
	// dialog block before the engine ever sees the install event.
	public static bool InsertFirstInChain( Type containingType, string eventName, Delegate ourHandler )
	{
		if ( containingType == null || ourHandler == null ) return false;

		var field = containingType.GetField( eventName,
			BindingFlags.Static | BindingFlags.Instance |
			BindingFlags.NonPublic | BindingFlags.Public );

		if ( field == null ) return false;

		var existing = field.GetValue( null ) as Delegate;
		Delegate newChain = ourHandler;
		if ( existing != null )
		{
			foreach ( var d in existing.GetInvocationList() )
				newChain = Delegate.Combine( newChain, d );
		}
		field.SetValue( null, newChain );
		return true;
	}

	// Fallback subscription via the public event accessor — appends to the end
	// of the chain. Returns true on success.
	public static bool AppendToChain( Type containingType, string eventName, Delegate ourHandler )
	{
		if ( containingType == null || ourHandler == null ) return false;

		var ev = containingType.GetEvent( eventName,
			BindingFlags.Static | BindingFlags.Instance |
			BindingFlags.NonPublic | BindingFlags.Public );

		if ( ev == null ) return false;

		try { ev.AddEventHandler( null, ourHandler ); return true; }
		catch { return false; }
	}

	// Pull a property by name off an object via reflection, returning null on
	// any failure. For grabbing PackageManager.ActivePackage members from
	// outside Sandbox.Engine.
	public static object GetProp( object instance, string propertyName )
	{
		if ( instance == null ) return null;
		try
		{
			return instance.GetType().GetProperty( propertyName,
				BindingFlags.Public | BindingFlags.NonPublic |
				BindingFlags.Instance | BindingFlags.Static )?.GetValue( instance );
		}
		catch { return null; }
	}

	// Resolve a type by full name when it's declared `internal` in another
	// assembly. Editor.LibraryManager.LibraryList / LibraryDetail are the
	// callers — we can't reference them at compile time, but their hosting
	// assembly is loaded by the time editor code runs.
	//
	// Anchor type lets us short-circuit to the right assembly when known.
	// Falls back to scanning every loaded assembly. Returns null if the type
	// has been renamed or removed — caller must log and degrade gracefully.
	public static Type ResolveEditorType( string fullName, Type anchor = null )
	{
		if ( string.IsNullOrEmpty( fullName ) ) return null;
		try
		{
			if ( anchor != null )
			{
				var t = anchor.Assembly.GetType( fullName, throwOnError: false );
				if ( t != null ) return t;
			}
			foreach ( var asm in AppDomain.CurrentDomain.GetAssemblies() )
			{
				try
				{
					var t = asm.GetType( fullName, throwOnError: false );
					if ( t != null ) return t;
				}
				catch { /* dynamic / refl-only assemblies throw — skip */ }
			}
		}
		catch { }
		return null;
	}

	// Invoke a non-public instance method via reflection. Used to fall through
	// to BaseItemWidget.PaintItem(VirtualWidget) from inside our ItemPaint
	// wrapper so default row rendering still happens when we decorate.
	public static object InvokeNonPublic( object instance, string methodName, params object[] args )
	{
		if ( instance == null ) return null;
		try
		{
			var mi = instance.GetType().GetMethod( methodName,
				BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public );
			return mi?.Invoke( instance, args );
		}
		catch { return null; }
	}
}
