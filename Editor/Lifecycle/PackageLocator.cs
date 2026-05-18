using System;
using System.IO;
using Editor;

namespace Sandbox.SecBox.Lifecycle;

// Locates a Package's on-disk folder so we can scan it. Returns ONLY paths
// under <projectRoot>/Libraries/<something>/ — never the project root itself,
// never anything outside the project tree. Engine packages and the project's
// own package both correctly return null, so InstallHook skips them.
internal static class PackageLocator
{
	public static string FolderFor(Package pkg)
	{
		if (pkg == null) return null;

		var ident = pkg.FullIdent ?? pkg.Ident;
		if (string.IsNullOrEmpty(ident)) return null;

		var proj = Project.Current;
		var projectRoot = proj?.RootDirectory?.FullName;
		if (string.IsNullOrEmpty(projectRoot)) return null;

		var libRoot = Path.Combine(projectRoot, "Libraries");
		if (!Directory.Exists(libRoot)) return null;

		// Try ident verbatim.
		var direct = Path.Combine(libRoot, ident);
		if (Directory.Exists(direct) && IsUnderLibRoot(direct, libRoot)) return direct;

		// Try last segment (sometimes folders are <org>.<name> sometimes just <name>).
		var lastSegment = ident.Contains('.')
			? ident.Substring(ident.LastIndexOf('.') + 1)
			: ident;
		var bySegment = Path.Combine(libRoot, lastSegment);
		if (Directory.Exists(bySegment) && IsUnderLibRoot(bySegment, libRoot)) return bySegment;

		// LocalPackage's CodePath. CodePath is usually <something>/Code — we want
		// the parent. But ONLY accept it if that parent lands under Libraries/.
		// Without that check, the deadlock_district project's own LocalPackage
		// (CodePath = <projectRoot>/Code, parent = <projectRoot>) would resolve
		// to the entire project — catastrophic over-scan.
		var codePath = ReflectionHelpers.GetProp(pkg, "CodePath") as string;
		if (!string.IsNullOrEmpty(codePath) && Directory.Exists(codePath))
		{
			var parent = Path.GetDirectoryName(codePath);
			if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent) && IsUnderLibRoot(parent, libRoot))
				return parent;
		}

		return null;
	}

	// Defence-in-depth — ensures returned paths are strict descendants of
	// <projectRoot>/Libraries/. Refuses Libraries/ itself.
	static bool IsUnderLibRoot(string candidate, string libRoot)
	{
		try
		{
			var full = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);
			var root = Path.GetFullPath(libRoot).TrimEnd(Path.DirectorySeparatorChar);
			if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return false;
			return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
		}
		catch { return false; }
	}

	public static string CurrentProjectRoot()
	{
		try { return Project.Current?.RootDirectory?.FullName; }
		catch { return null; }
	}
}
