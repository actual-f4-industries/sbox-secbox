using System.Runtime.CompilerServices;

namespace Sandbox.SecBox.Lifecycle;

// ModuleInitializer fires once when the runtime loads our assembly — the
// earliest possible C# entry point, before any of our static constructors run
// on first use. Use it to arm subscriptions that must be in place before any
// third-party library install can happen.
internal static class SecboxBoot
{
	[ModuleInitializer]
#pragma warning disable CA2255 // ModuleInitializer is exactly what we want here — earliest C# entry point
	public static void Init()
#pragma warning restore CA2255
	{
		try
		{
			InstallHook.Subscribe();
			RuntimeMonitor.Subscribe();
			global::Sandbox.Internal.GlobalGameNamespace.Log.Info(
				"[secbox] initialised" );
		}
		catch ( System.Exception ex )
		{
			// Never let a secbox failure prevent the editor from booting.
			global::Sandbox.Internal.GlobalGameNamespace.Log.Error(
				$"[secbox] boot failed: {ex.Message}\n{ex.StackTrace}" );
		}
	}
}
