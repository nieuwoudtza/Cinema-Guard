using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CinemaGuard.Installer;

/// <summary>The one setup window: folder, desktop shortcut, start when done.</summary>
internal sealed class SetupForm : Form
{
    private const int BcmSetShield = 0x160C;

    private readonly TextBox _folder = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0) };
    private readonly Button _browse = new() { Text = "Browse…", AutoSize = true, Margin = new Padding(0) };
    private readonly Label _folderNote = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 6, 0, 10) };
    private readonly CheckBox _desktop = new() { Text = "Create a desktop shortcut", Checked = true, AutoSize = true, Margin = new Padding(0, 0, 0, 6) };
    private readonly CheckBox _launch = new() { Text = "Start Cinema Guard when setup finishes", Checked = true, AutoSize = true, Margin = new Padding(0) };
    private readonly ProgressBar _progress = new() { Style = ProgressBarStyle.Marquee, Dock = DockStyle.Fill, Visible = false, Height = 6, Margin = new Padding(0, 14, 0, 6) };
    private readonly Label _status = new() { AutoSize = true, Margin = new Padding(0) };
    private readonly Button _install = new() { AutoSize = true, FlatStyle = FlatStyle.System, MinimumSize = new Size(96, 0) };
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, MinimumSize = new Size(96, 0) };
    private readonly Timer _recheck = new() { Interval = 400 };

    private readonly string? _existing = Installer.ExistingFolder();
    private bool _busy;

    public SetupForm()
    {
        var updating = _existing is not null;

        Text = "Cinema Guard Setup";
        Font = new Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(20, 16, 20, 16);
        AcceptButton = _install;
        CancelButton = _cancel;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch (ArgumentException) { }

        _install.Text = updating ? "Update" : "Install";
        _folder.Text = _existing ?? Installer.DefaultFolder;

        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill, Width = 480 };
        layout.Controls.Add(new Label
        {
            Text = updating ? $"Update Cinema Guard to {Installer.Version}" : $"Install Cinema Guard {Installer.Version}",
            Font = new Font("Segoe UI Semibold", 13f),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
        });
        layout.Controls.Add(new Label
        {
            Text = "Choose where to install it. Your own profile needs no special permission; folders such as Program Files ask for administrator permission.",
            AutoSize = true,
            MaximumSize = new Size(480, 0),
            Margin = new Padding(0, 0, 0, 14),
        });
        layout.Controls.Add(new Label { Text = "Install folder", AutoSize = true, Margin = new Padding(0, 0, 0, 4) });

        var folderRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        folderRow.Controls.Add(_folder, 0, 0);
        folderRow.Controls.Add(_browse, 1, 0);
        layout.Controls.Add(folderRow);

        layout.Controls.Add(_folderNote);
        layout.Controls.Add(_desktop);
        layout.Controls.Add(_launch);
        layout.Controls.Add(_progress);
        layout.Controls.Add(_status);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 14, 0, 0),
        };
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_install);
        layout.Controls.Add(buttons);

        Controls.Add(layout);

        _desktop.Checked = !updating || File.Exists(Installer.DesktopShortcutPath);

        _browse.Click += (_, _) => Browse();
        _install.Click += async (_, _) => await InstallAsync();
        _cancel.Click += (_, _) => Close();
        _folder.TextChanged += (_, _) => { _recheck.Stop(); _recheck.Start(); };
        _recheck.Tick += (_, _) => { _recheck.Stop(); RefreshFolderNote(); };
        FormClosing += (_, e) => e.Cancel = _busy;
        Shown += (_, _) => CheckThisPc();

        RefreshFolderNote();
    }

    private void CheckThisPc()
    {
        var problem = Installer.Unsupported();
        if (problem is null) return;

        MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        Close();
    }

    private void Browse()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose the folder to install Cinema Guard in. A \"Cinema Guard\" folder is created inside it.",
            ShowNewFolderButton = true,
            SelectedPath = ParentOf(_folder.Text),
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var chosen = dialog.SelectedPath;
        _folder.Text = Path.GetFileName(chosen.TrimEnd('\\')).Equals(Installer.AppTitle, StringComparison.OrdinalIgnoreCase)
            ? chosen
            : Path.Combine(chosen, Installer.AppTitle);
    }

    private static string ParentOf(string folder)
    {
        try
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(folder));
            return parent is not null && Directory.Exists(parent) ? parent : "";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "";
        }
    }

    /// <summary>A full local path, or null with the reason shown.</summary>
    private string? ChosenFolder(out string? problem)
    {
        problem = null;
        var text = _folder.Text.Trim().Trim('"');

        try
        {
            if (text.Length == 0 || !Path.IsPathRooted(text) || text.StartsWith(@"\\", StringComparison.Ordinal))
            {
                problem = @"Enter a full folder path on this PC, such as C:\Apps\Cinema Guard.";
                return null;
            }

            var folder = Path.GetFullPath(text);
            if (Path.GetPathRoot(folder)!.TrimEnd('\\').Length == folder.TrimEnd('\\').Length)
            {
                problem = @"Choose a folder, not a whole drive, such as D:\Cinema Guard.";
                return null;
            }

            if (!Installer.SameFolder(folder, _existing) && Installer.IsOccupied(folder))
            {
                problem = "That folder already has other files in it. Choose an empty or new folder.";
                return null;
            }

            return folder;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            problem = "That isn't a valid folder path.";
            return null;
        }
    }

    private void RefreshFolderNote()
    {
        if (_busy) return;

        var folder = ChosenFolder(out var problem);
        var needsAdmin = folder is not null && Installer.NeedsElevation(folder);

        _folderNote.ForeColor = problem is null ? SystemColors.GrayText : Color.Firebrick;
        _folderNote.Text = problem
            ?? (needsAdmin
                ? "This folder needs administrator permission. Windows will ask when you click " + _install.Text + "."
                : "No administrator permission needed.");

        _install.Enabled = problem is null;
        SetShield(needsAdmin);
    }

    private void SetShield(bool show)
    {
        if (_install.IsHandleCreated || _install.Handle != IntPtr.Zero)
            SendMessage(_install.Handle, BcmSetShield, IntPtr.Zero, show ? new IntPtr(1) : IntPtr.Zero);
    }

    private async Task InstallAsync()
    {
        var folder = ChosenFolder(out var problem);
        if (folder is null)
        {
            ShowError(problem!);
            return;
        }

        if (Installer.IsAppRunning())
        {
            ShowError("Cinema Guard is running. Close it, then click " + _install.Text + " again.");
            return;
        }

        var moving = _existing is not null && !Installer.SameFolder(_existing, folder);
        if (moving && MessageBox.Show(this,
                $"Cinema Guard is already installed in:{Environment.NewLine}{_existing}{Environment.NewLine}{Environment.NewLine}" +
                "Move it to the new folder? The old copy is removed; your settings and filter words are kept.",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        SetBusy(true, moving ? "Removing the old copy…" : "Installing…");

        var error = await Task.Run(() =>
        {
            try
            {
                if (moving) Installer.UninstallOld(_existing!);

                if (Installer.NeedsElevation(folder)) return Installer.InstallElevated(folder);

                Installer.Install(folder);
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        });

        if (error is not null)
        {
            SetBusy(false, null);
            ShowError(error);
            return;
        }

        var notes = Finish(folder);
        SetBusy(false, null);

        if (_launch.Checked && notes is null)
        {
            Close();
            return;
        }

        MessageBox.Show(this, notes ?? $"Cinema Guard {Installer.Version} is installed.", Text, MessageBoxButtons.OK,
            notes is null ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        Close();
    }

    /// <summary>Shortcuts and the first launch, as the person who ran setup. Returns a note when something failed.</summary>
    private string? Finish(string folder)
    {
        try
        {
            if (!File.Exists(Installer.StartMenuShortcutPath))
                Installer.CreateShortcut(Installer.StartMenuShortcutPath, folder);

            if (_desktop.Checked)
                Installer.CreateShortcut(Installer.DesktopShortcutPath, folder);
            else if (File.Exists(Installer.DesktopShortcutPath))
                File.Delete(Installer.DesktopShortcutPath);
        }
        catch (Exception ex)
        {
            return $"Cinema Guard is installed, but a shortcut could not be created: {ex.Message}";
        }

        if (!_launch.Checked) return null;

        try
        {
            Installer.Launch(folder);
            return null;
        }
        catch (Exception ex)
        {
            return $"Cinema Guard is installed, but it could not be started: {ex.Message}";
        }
    }

    private void SetBusy(bool busy, string? status)
    {
        _busy = busy;
        _progress.Visible = busy;
        _status.ForeColor = SystemColors.ControlText;
        _status.Text = status ?? "";
        _folder.Enabled = _browse.Enabled = _desktop.Enabled = _launch.Enabled = _install.Enabled = _cancel.Enabled = !busy;
        UseWaitCursor = busy;
    }

    private void ShowError(string message)
    {
        _status.ForeColor = Color.Firebrick;
        _status.MaximumSize = new Size(480, 0);
        _status.Text = message;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
