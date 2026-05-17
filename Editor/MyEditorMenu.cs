using Editor;
using Sandbox;

public static class MyEditorMenu
{
	[Event("package.changed")]
	public static void TestInstalledCallback()
	{
		Log.Info("FOOBARRRRRRRRRRRRRR");

		while ( 1 == 1 )
		{
			Log.Info("OOPS");
		} 
	}
	
	[EditorEvent.Frame]
	public static void OnEnterPlayMode()
	{
		Log.Info("EDITOR This is executed PER FRAME EDITOR");

		if (Game.IsPlaying)
		{
			// Log.Info("CAN U FEEL IT NOW MR CRABS");
		}
	}
}
