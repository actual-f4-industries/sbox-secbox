using System;
using System.Threading.Tasks;
using Editor;
using Sandbox.SecBox.Bridge;
using Sandbox.SecBox.Lifecycle;
using DiagnosticsLog = Sandbox.SecBox.Bridge.DiagnosticsLog;

namespace Sandbox.SecBox.UI;

// Confirmation dialog shown before installing Secbox Sentinel.
//
// Explains the privilege, scope, and reversibility of the install so the
// user makes an informed choice before the UAC prompt appears. Used by the
// `secbox > Runtime Monitoring > Install Sentinel...` menu item and by the
// "Install / Repair" button in the settings dialog.
//
// Custom Qt window because EditorUtility.DisplayDialog is info-only in
// s&box (returns void, no yes/no result).
public sealed class SentinelInstallDialog : BaseWindow
{
	const string CssH1     = "font-size: 18px; font-weight: 700; color: #ffffff;";
	const string CssH2     = "font-size: 13px; font-weight: 600; color: #ffffff;";
	const string CssBody   = "color: #e8eaee; font-size: 12px;";
	const string CssSubtle = "color: #9aa0a6; font-size: 11px;";
	const string CssMono   = "font-family: 'Consolas','Menlo',monospace; color: #c5cad1; font-size: 11px;";
	const string CssWarn   = "color: #fdd835; font-size: 12px; font-weight: 600;";

	Label _status;
	Button _btnInstall;
	Button _btnCancel;
	bool _running;

	public SentinelInstallDialog() : base()
	{
		DeleteOnClose = true;
		Size = new Vector2(620, 520);
		WindowTitle = "secbox — install Sentinel";
		SetWindowIcon("shield_lock");

		Layout = Layout.Column();
		Layout.Margin = 18;
		Layout.Spacing = 10;

		BuildHeader();
		BuildWhatItIs();
		BuildWhatItDoes();
		BuildWhatItDoesNot();
		BuildTrust();
		BuildStatusLine();
		BuildFooter();
	}

	void BuildHeader()
	{
		var col = Layout.AddColumn();
		col.Spacing = 4;
		var title = new Label("Install Secbox Sentinel?");
		title.SetStyles(CssH1);
		col.Add(title);
		var sub = new Label("One-time admin install. Adds kernel-level monitoring on top of "
			+ "the always-on in-process profiler. Reversible via Uninstall in the settings panel.");
		sub.SetStyles(CssSubtle);
		sub.WordWrap = true;
		col.Add(sub);
	}

	void BuildWhatItIs()
	{
		AddSection("What it is",
			"A signed Windows Service named `SecboxSentinel`, running as LocalSystem. " +
			"Started automatically with Windows. Single-binary install under " +
			$"`{System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Secbox", "Sentinel")}`. " +
			"Communicates only over a Named Pipe scoped to the current interactive user (no network).");
	}

	void BuildWhatItDoes()
	{
		AddSection("What it does",
			"Subscribes to Windows ETW kernel providers for the s&box editor process and "
			+ "forwards normalized events (file create/write/delete, process spawn, network "
			+ "connect, registry change, image load) to the in-editor secbox client. The "
			+ "client correlates them with the in-process profiler's managed-call attribution "
			+ "to identify which library originated each operation.");
	}

	void BuildWhatItDoesNot()
	{
		AddSection("What it does NOT do",
			"• Block, kill, or modify any process or file. Detection-only.\n"
			+ "• Open network ports. Pipe is localhost-only and DACL-scoped to the current user.\n"
			+ "• Collect data outside this machine. All events stay in-process or in the local log.\n"
			+ "• Run with kernel-mode drivers. User-mode service, same privilege as any signed admin tool.");
	}

	void BuildTrust()
	{
		AddSection("Trust", null);
		var src = new Label($"Source: {SentinelInstaller.MsiDownloadUrl}");
		src.SetStyles(CssMono);
		src.WordWrap = true;
		src.TextSelectable = true;
		Layout.Add(src);
		var hash = new Label($"Pinned SHA-256: {SentinelInstaller.ExpectedMsiSha256}");
		hash.SetStyles(CssMono);
		hash.WordWrap = true;
		hash.TextSelectable = true;
		Layout.Add(hash);
		var refused = new Label("Adapter refuses to install any MSI whose hash doesn't match the pin.");
		refused.SetStyles(CssSubtle);
		refused.WordWrap = true;
		Layout.Add(refused);
	}

	void BuildStatusLine()
	{
		_status = new Label("Ready. Click Install to download, verify, and launch the MSI.");
		_status.SetStyles(CssBody);
		_status.WordWrap = true;
		Layout.Add(_status);
	}

	void BuildFooter()
	{
		var row = Layout.AddRow();
		row.Spacing = 8;

		var warn = new Label("Requires admin (UAC prompt).");
		warn.SetStyles(CssWarn);
		row.Add(warn);

		row.AddStretchCell();

		_btnCancel = new Button("Cancel");
		_btnCancel.Clicked = () => Close();
		row.Add(_btnCancel);

		_btnInstall = new Button.Primary("Install");
		_btnInstall.Icon = "shield_lock";
		_btnInstall.Clicked = () => _ = InstallAsync();
		row.Add(_btnInstall);
	}

	async Task InstallAsync()
	{
		if (_running) return;
		_running = true;
		_btnInstall.Enabled = false;
		_btnCancel.Enabled = false;

		try
		{
			UpdateStatus("Downloading MSI from GitHub release…");
			var dl = await SentinelInstaller.EnsureMsiCachedAsync().ConfigureAwait(true);
			if (!dl.Success)
			{
				UpdateStatus($"Download failed: {dl.Message}");
				_btnCancel.Enabled = true;
				_running = false;
				return;
			}
			UpdateStatus("MSI verified. Launching installer (UAC will prompt)…");

			var run = SentinelInstaller.RunInstaller(dl.Message);
			UpdateStatus(run.Message);

			if (run.Success)
			{
				// Re-arm runtime monitoring so the new EtwSensor picks up
				// the just-installed service.
				try { RuntimeMonitorCoordinator.ReapplySettings(); }
				catch (Exception ex) { DiagnosticsLog.Warn($"reapply after install threw: {ex.Message}"); }
			}
		}
		catch (Exception ex)
		{
			DiagnosticsLog.Error("Sentinel install flow threw", ex);
			UpdateStatus($"Install threw: {ex.Message}");
		}
		finally
		{
			_btnCancel.Enabled = true;
			_btnInstall.Enabled = true;
			_running = false;
		}
	}

	void UpdateStatus(string msg)
	{
		try { MainThread.Queue(() => { try { _status.Text = msg; } catch { } }); } catch { }
	}

	void AddSection(string title, string body)
	{
		var h = new Label(title);
		h.SetStyles(CssH2);
		Layout.Add(h);

		if (body != null)
		{
			var p = new Label(body);
			p.SetStyles(CssBody);
			p.WordWrap = true;
			Layout.Add(p);
		}
	}
}
