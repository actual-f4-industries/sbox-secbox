using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.SecBox.Bridge.Dto;

namespace Sandbox.SecBox.UI;

/// <summary>
/// Static utility that maps raw findings into human-readable concern categories.
/// Mapping is based on the RuleId prefix pattern.
/// </summary>
public static class ConcernMapper
{
	// Category definitions: key → (statement, default icon)
	private static readonly Dictionary<string, (string Statement, string DefaultIcon)> _categories = new()
	{
		{ "filesystem", ("This library wants to **read and write files**", "\u26a0\u00ef") },
		{ "process",    ("This library wants to **run programs**", "\u26a0\u00ef") },
		{ "interop",    ("This library wants to call **native system code**", "\u26a0\u00ef") },
		{ "dynamicCode",("This library wants to **download and run code**", "\u26a0\u00ef") },
		{ "rawNetwork", ("This library wants to **access the internet**", "\u2139\u00ef") },
		{ "environment",("This library wants to **modify system settings**", "\u2139\u00ef") },
		{ "unsafeCode", ("This library uses **unsafe memory operations**", "\u2139\u00ef") },
		{ "other",      ("This library contains patterns we cannot classify", "\u2139\u00ef") },
	};

	// RuleId prefix → category key mapping
	private static readonly Dictionary<string, string> _prefixMap = new()
	{
		{ "critical.filesystem.", "filesystem" },
		{ "critical.process.", "process" },
		{ "critical.interop.", "interop" },
		{ "critical.dynamicCode.", "dynamicCode" },
		{ "critical.rawNetwork.", "rawNetwork" },
		{ "critical.environment.", "environment" },
		{ "critical.reflection.", "unsafeCode" },
	};

	// Severity ranking for comparison
	private static readonly Dictionary<Severity, int> _severityRank = new()
	{
		{ Severity.Info, 0 },
		{ Severity.Low, 1 },
		{ Severity.Medium, 2 },
		{ Severity.High, 3 },
		{ Severity.Critical, 4 },
	};

	/// <summary>
	/// Maps raw findings into concern categories.
	/// Only categories with at least one finding are returned.
	/// Sorted by severity (Critical first), then alphabetically by category key.
	/// </summary>
	public static Concern[] Map(Finding[] findings)
	{
		if (findings == null || findings.Length == 0)
			return Array.Empty<Concern>();

		// Group findings by category
		var groups = new Dictionary<string, List<Finding>>();

		foreach (var f in findings)
		{
			var category = ResolveCategory(f.RuleId);
			if (!groups.TryGetValue(category, out var list))
			{
				list = new List<Finding>();
				groups[category] = list;
			}
			list.Add(f);
		}

		// Build concerns
		var concerns = new List<Concern>();
		foreach (var kvp in groups)
		{
			var category = kvp.Key;
			var list = kvp.Value;

			if (!_categories.TryGetValue(category, out var def))
				continue; // Should not happen

			var highest = list.Max(f => _severityRank.GetValueOrDefault(f.Severity, 0));
			var highestSeverity = _severityRank.FirstOrDefault(x => x.Value == highest).Key;

			var ruleIds = list.Select(f => f.RuleId).Distinct().OrderBy(r => r).ToArray();

			concerns.Add(new Concern
			{
				Category = category,
				Statement = def.Statement,
				FindingCount = list.Count,
				HighestSeverity = highestSeverity,
				RuleIds = ruleIds,
				Selected = highestSeverity == Severity.Critical || highestSeverity == Severity.High,
			});
		}

		// Sort: Critical first, then High, Medium, Low; within same severity, alphabetical by category
		concerns.Sort((a, b) =>
		{
			var rankA = _severityRank.GetValueOrDefault(a.HighestSeverity, 0);
			var rankB = _severityRank.GetValueOrDefault(b.HighestSeverity, 0);
			if (rankA != rankB)
				return rankB.CompareTo(rankA); // Descending severity
			return string.Compare(a.Category, b.Category, StringComparison.Ordinal);
		});

		return concerns.ToArray();
	}

	/// <summary>
	/// Resolves a RuleId to a category key based on prefix matching.
	/// Falls back to "other" if no prefix matches.
	/// </summary>
	private static string ResolveCategory(string ruleId)
	{
		if (string.IsNullOrEmpty(ruleId))
			return "other";

		foreach (var kvp in _prefixMap)
		{
			if (ruleId.StartsWith(kvp.Key, StringComparison.OrdinalIgnoreCase))
				return kvp.Value;
		}

		return "other";
	}

	/// <summary>
	/// Returns the human-readable statement for a category key.
	/// </summary>
	public static string GetStatement(string categoryKey)
	{
		if (string.IsNullOrEmpty(categoryKey))
			return "Unknown concern";

		if (_categories.TryGetValue(categoryKey, out var def))
			return def.Statement;

		return "Unknown concern";
	}

	/// <summary>
	/// Returns the icon for a category key based on whether it has Critical/High severity.
	/// </summary>
	public static string GetIcon(string categoryKey, Severity severity)
	{
		if (severity == Severity.Critical || severity == Severity.High)
			return "\u26a0\u00ef"; // Warning
		return "\u2139\u00ef"; // Info
	}

	/// <summary>
	/// Helper for icon selection - returns true if the concern has Critical or High severity.
	/// </summary>
	public static bool HasCriticalOrHigh(Concern concern)
	{
		return concern?.HighestSeverity == Severity.Critical || concern?.HighestSeverity == Severity.High;
	}
}
