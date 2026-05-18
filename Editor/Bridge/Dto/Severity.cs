namespace Sandbox.SecBox.Bridge.Dto;

// Local mirror of Secbox.Contracts.Severity. The editor adapter cannot
// reference Secbox.Contracts at compile time (editor regenerates the csproj
// and would strip the reference) — these DTOs exist purely for JSON
// deserialization. Values must match Secbox.Contracts.Severity.
public enum Severity
{
	Info,
	Low,
	Medium,
	High,
	Critical,
}
