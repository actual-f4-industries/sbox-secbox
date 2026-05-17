namespace Sandbox.SecBox;

public enum Decision
{
	// User hasn't decided yet. Default for never-seen packages.
	Unreviewed,

	// Allow for this session only. Will re-prompt on next install / boot if
	// the content hash matches the original review.
	AllowOnce,

	// Trust this exact content hash forever. Any byte change in any .dll or
	// .cs file under the package invalidates this and forces re-review.
	TrustAlways,

	// Block this content hash. secbox refuses to allow load; on install,
	// triggers uninstall flow.
	Block,
}
