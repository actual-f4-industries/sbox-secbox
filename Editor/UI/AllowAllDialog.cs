using System;
using System.Collections.Generic;
using System.Linq;
using Editor;
using Sandbox.Audio;
using Sandbox.SecBox.Bridge.Dto;

namespace Sandbox.SecBox.UI;

/// <summary>
/// Confirmation dialog shown when the user clicks "Allow All".
/// Lists all concern categories with checkboxes (all pre-checked by default).
/// </summary>
public sealed class AllowAllDialog : BaseWindow
{
	private readonly Concern[] _concerns;
	private readonly List<bool> _checkedStates;
	private string[] _selectedConcerns = Array.Empty<string>();

	private const string CssCard = "background-color: #2b2b2f; border-radius: 6px; padding: 8px 10px;";
	private const string CssLabel = "color: #e8eaee; font-size: 13px;";

	public AllowAllDialog(Concern[] concerns) : base()
	{
		_concerns = concerns ?? Array.Empty<Concern>();
		_checkedStates = new List<bool>(_concerns.Length);
		for (int i = 0; i < _concerns.Length; i++)
			_checkedStates.Add(true); // All pre-checked

		DeleteOnClose = true;
		Size = new Vector2(420, 320);
		WindowTitle = "Confirm Allow All";
		SetWindowIcon("warning");

		Layout = Layout.Column();
		Layout.Margin = 16;
		Layout.Spacing = 12;

		BuildBody();
		BuildFooter();
	}

	/// <summary>
	/// Which categories the user confirmed (checked). Empty if canceled.
	/// </summary>
	public string[] SelectedConcerns => _selectedConcerns;

	/// <summary>
	/// Creates and shows the dialog modally.
	/// </summary>
	public static AllowAllDialog Show(Concern[] concerns)
	{
		var dialog = new AllowAllDialog(concerns);
		dialog.Show();
		return dialog;
	}

	private void BuildBody()
	{
		// Warning text
		var warning = new Label("This library wants to:");
		warning.SetStyles("font-size: 13px; color: #c5cad1;");
		Layout.Add(warning);

		// Scrollable concerns list
		var scroll = Layout.Add(new ScrollArea(this), 1);
		scroll.Canvas = new Widget(scroll);
		scroll.Canvas.Layout = Layout.Column();
		scroll.Canvas.Layout.Margin = 4;
		scroll.Canvas.Layout.Spacing = 6;

		for (int i = 0; i < _concerns.Length; i++)
		{
			var concern = _concerns[i];
			var row = scroll.Canvas.Layout.AddRow();
			row.Spacing = 8;

			var checkbox = new Checkbox();
			checkbox.State = _checkedStates[i] ? CheckState.On : CheckState.Off ;
			checkbox.StateChanged = state =>
			{
				_checkedStates[i] = state == CheckState.On;
			};
			row.Add(checkbox);

			var label = new Label(concern.Statement);
			label.SetStyles(CssLabel);
			label.WordWrap = true;
			row.Add(label);
		}

		scroll.Canvas.Layout.AddStretchCell();

		// Irreversible warning
		var irreversible = new Label("This cannot be easily undone.");
		irreversible.SetStyles("color: #e53935; font-size: 11px; font-style: italic;");
		Layout.Add(irreversible);
	}

	private void BuildFooter()
	{
		var row = Layout.AddRow();
		row.Spacing = 8;
		row.AddStretchCell();

		var cancel = new Button("Cancel");
		cancel.Clicked = () =>
		{
			_selectedConcerns = Array.Empty<string>();
			Close();
		};
		row.Add(cancel);

		var confirm = new Button.Primary("Yes, Allow All");
		confirm.Clicked = () =>
		{
			var selected = new List<string>();
			for (int i = 0; i < _concerns.Length; i++)
			{
				if (_checkedStates[i])
					selected.Add(_concerns[i].Category);
			}
			_selectedConcerns = selected.ToArray();
			Close();
		};
		row.Add(confirm);
	}
}
