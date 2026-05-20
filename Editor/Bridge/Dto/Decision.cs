namespace Sandbox.SecBox.Bridge.Dto;

// Mirror of Secbox.Contracts.Decision. Used both for incoming scan-report
// overall verdicts AND for local trust-store entries (the Trust subsystem
// shares this enum).
public enum Decision
{
	NotReviewed,
	AllowOnce,
	TrustAlways,
	Block,
	Quarantine,
}
