using Editor;
using Sandbox;

/// <summary>
/// This is a component - in your library!
/// </summary>
[Title( "secbox - My Component" )]
public class MyLibraryComponent : Component
{
	[Event("package.changed.installed")]
	public static void TestInstalledCallback()
	{
		Log.Info("FOOBAR");
	}
	
	// [EditorEvent.Frame]
	// public static void OnEnterPlayMode()
	// {
	//
	// 	if (Game.IsPlaying)
	// 	{
	// 		// Log.Info("CAN U FEEL IT NOW MR CRABS");
	// 	}
	// }
}
