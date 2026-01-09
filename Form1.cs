using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.IO;
using System.Text.Json;
using System.Diagnostics;
using System.Collections.Concurrent;

namespace MySimpleApp;

public partial class Form1 : Form
{
    private readonly List<CommandConfig> _commands = new();
    private const string ConfigFileName = "commands.json";
    private readonly List<Process> _activeProcesses = new();
    private readonly object _processLock = new();
    
    // Logging State
    private bool _isLogEnabled = true;
    private bool _isLogPaused = false;
    private struct LogEntry { public string Message; public Color? Color; public bool IsBold; }
    private readonly List<LogEntry> _logBuffer = new();
    private readonly ConcurrentQueue<LogEntry> _pendingLogs = new();
    private readonly System.Windows.Forms.Timer _logFlushTimer;
    private const int MaxLogLines = 100000;
    private const int MaxLinesPerBatch = 500;

    // UI Controls
    private MenuStrip _menuStrip = null!;
    private SplitContainer _splitContainer = null!;
    private ListBox _fileListBox = null!;
    private FlowLayoutPanel _commandDeck = null!;
    private RichTextBox _logConsole = null!;
    private bool _isEditMode = false;

    public Form1()
    {
        InitializeComponent();
        
        _logFlushTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _logFlushTimer.Tick += (s, e) => BatchFlushLog();
        _logFlushTimer.Start();

        SetupCustomUI();
        LoadCommands();
    }

    private void SetupCustomUI()
    {
        this.Text = "MySimpleApp - Command Runner";
        this.Size = new Size(1100, 850); // Larger default to accommodate taller log
        this.MinimumSize = new Size(700, 600);

        // 1. MenuStrip
        _menuStrip = new MenuStrip
        {
            BackColor = Color.White,
            RenderMode = ToolStripRenderMode.Professional,
            Padding = new Padding(5, 2, 0, 2)
        };
        
        // Configuration Menu
        var configMenu = new ToolStripMenuItem("Configuration");
        configMenu.DropDownItems.Add("Add New Command", null, (s, e) => ShowAddCommandDialog());
        
        var editModeItem = new ToolStripMenuItem("Edit Mode Toggle") { CheckOnClick = true };
        editModeItem.CheckedChanged += (s, e) =>
        {
            _isEditMode = editModeItem.Checked;
            Log($"Edit Mode: {(_isEditMode ? "ON" : "OFF")}");
            RefreshCommandDeck();
        };
        configMenu.DropDownItems.Add(editModeItem);
        configMenu.DropDownItems.Add(new ToolStripSeparator());
        configMenu.DropDownItems.Add("Save Settings", null, (s, e) => SaveCommands());
        
        // Quick Action: Add Files
        var addFilesMenu = new ToolStripMenuItem("Add Files", null, (s, e) => AddFiles());
        addFilesMenu.ForeColor = Color.FromArgb(70, 130, 180);
        addFilesMenu.Font = new Font(_menuStrip.Font, FontStyle.Bold);

        // Quick Action: Clear List
        var clearListItem = new ToolStripMenuItem("Clear List", null, (s, e) => _fileListBox.Items.Clear());
        clearListItem.ForeColor = Color.Maroon;

        // Quick Action: Clear Log
        var clearLogItem = new ToolStripMenuItem("Clear Log", null, (s, e) => _logConsole.Clear());
        clearLogItem.ForeColor = Color.DarkGreen;

        _menuStrip.Items.Add(configMenu);
        _menuStrip.Items.Add(addFilesMenu);
        _menuStrip.Items.Add(clearListItem);
        _menuStrip.Items.Add(clearLogItem);
        this.Controls.Add(_menuStrip);
        this.MainMenuStrip = _menuStrip;

        // 2. SplitContainer
        _splitContainer = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 400 // Slightly wider to make the log more readable
        };
        this.Controls.Add(_splitContainer);
        _splitContainer.SendToBack();

        // 3. Left Panel Layout (Panel 1)
        var leftTable = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 4, // Increased for Log Controls
            ColumnCount = 1,
            Padding = new Padding(10, 20, 10, 10)
        };
        leftTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // 0: Header label
        leftTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // 1: File List
        leftTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // 2: Log Controls
        leftTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 320)); // 3: Log Console area

        var lblFiles = new Label 
        { 
            Text = "Staged Files:", 
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = Color.DimGray,
            AutoSize = true,
            Margin = new Padding(0, 5, 0, 5) // Added top margin to clear the menu bar shadow
        };

        // 4. File List
        _fileListBox = new ListBox
        {
            Dock = DockStyle.Fill,
            SelectionMode = SelectionMode.MultiExtended,
            IntegralHeight = false,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 11), // Larger, more readable font
            BackColor = Color.White,
        };

        // Context Menu for ListBox
        var listContextMenu = new ContextMenuStrip();
        listContextMenu.Items.Add("Remove Selected", null, (s, e) => RemoveSelectedFiles());
        listContextMenu.Items.Add(new ToolStripSeparator());
        listContextMenu.Items.Add("Clear All", null, (s, e) => _fileListBox.Items.Clear());
        _fileListBox.ContextMenuStrip = listContextMenu;

        // 5. Log Controls
        var logControls = new FlowLayoutPanel 
        { 
            Dock = DockStyle.Fill, 
            AutoSize = true, 
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 5, 0, 0)
        };
        
        var chkEnableLog = new CheckBox { Text = "Enable Log", Checked = true, AutoSize = true };
        chkEnableLog.CheckedChanged += (s, e) => _isLogEnabled = chkEnableLog.Checked;
        
        var chkPauseLog = new CheckBox { Text = "Pause Log", Checked = false, AutoSize = true };
        chkPauseLog.CheckedChanged += (s, e) => 
        {
            _isLogPaused = chkPauseLog.Checked;
            if (!_isLogPaused) FlushLogBuffer();
        };
        
        logControls.Controls.Add(chkEnableLog);
        logControls.Controls.Add(chkPauseLog);

        // 6. Log Console
        _logConsole = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = Color.Black,
            ForeColor = Color.LightGray,
            Font = new Font("Consolas", 10),
            BorderStyle = BorderStyle.None,
            Margin = new Padding(0, 10, 0, 0) // Gap between list and log
        };
        
        // Context Menu for Log
        var logContextMenu = new ContextMenuStrip();
        logContextMenu.Items.Add("Clear Console", null, (s, e) => _logConsole.Clear());
        _logConsole.ContextMenuStrip = logContextMenu;
        
        leftTable.Controls.Add(lblFiles, 0, 0);
        leftTable.Controls.Add(_fileListBox, 0, 1);
        leftTable.Controls.Add(logControls, 0, 2);
        leftTable.Controls.Add(_logConsole, 0, 3);
        
        _splitContainer.Panel1.Controls.Add(leftTable);

        // Right Panel (Command Deck)
        _commandDeck = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(10, 20, 10, 10), // Increased top padding to 20
            BackColor = Color.WhiteSmoke
        };
        _splitContainer.Panel2.Controls.Add(_commandDeck);

        this.FormClosing += (s, e) => CleanupProcesses();
    }

    private void AddFiles()
    {
        using var ofd = new OpenFileDialog
        {
            Multiselect = true,
            Title = "Select Files to Process"
        };

        if (ofd.ShowDialog() == DialogResult.OK)
        {
            _fileListBox.Items.AddRange(ofd.FileNames);
        }
    }

    private void ShowAddCommandDialog()
    {
        using var dialog = new CommandInputDialog();
        if (dialog.ShowDialog() == DialogResult.OK)
        {
            var config = dialog.CommandConfig;
            _commands.Add(config);
            CreateCommandButton(config);
        }
    }

    private void CreateCommandButton(CommandConfig config)
    {
        var btn = new Button
        {
            Text = config.Name,
            Width = 150, // Standardized width
            Height = 55, // Standardized height
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(10, 5, 10, 5), // Balanced margins
            BackColor = _isEditMode ? Color.MistyRose : Color.AliceBlue,
            Font = new Font("Segoe UI", 9, FontStyle.Regular),
            Tag = config
        };

        btn.Click += async (s, e) =>
        {
            if (_isEditMode)
            {
                ShowEditCommandDialog(config);
            }
            else
            {
                // Disable button during execution to prevent accidental loops/multiple clicks
                var originalColor = btn.BackColor;
                btn.Enabled = false;
                btn.BackColor = Color.LightGray;
                try
                {
                    await ExecuteCommandAsync(config);
                }
                finally
                {
                    btn.Enabled = true;
                    btn.BackColor = originalColor;
                }
            }
        };
        _commandDeck.Controls.Add(btn);
    }

    private void RefreshCommandDeck()
    {
        _commandDeck.SuspendLayout(); // Performance boost
        _commandDeck.Controls.Clear();
        foreach (var cmd in _commands)
        {
            CreateCommandButton(cmd);
        }
        _commandDeck.ResumeLayout();
    }

    private void RemoveSelectedFiles()
    {
        var selectedItems = _fileListBox.SelectedItems.Cast<object>().ToList();
        foreach (var item in selectedItems)
        {
            _fileListBox.Items.Remove(item);
        }
    }

    private void ShowEditCommandDialog(CommandConfig config)
    {
        using var dialog = new CommandInputDialog(config);
        var result = dialog.ShowDialog();
        
        if (result == DialogResult.OK)
        {
            var index = _commands.IndexOf(config);
            if (index != -1)
            {
                _commands[index] = dialog.CommandConfig;
                RefreshCommandDeck();
            }
        }
        else if (result == DialogResult.Abort) // Using Abort as a signal for Delete
        {
            if (MessageBox.Show($"Are you sure you want to delete '{config.Name}'?", "Confirm Delete", MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                _commands.Remove(config);
                RefreshCommandDeck();
            }
        }
    }

    private async Task ExecuteCommandAsync(CommandConfig config)
    {
        if (!File.Exists(config.ExecutablePath))
        {
            Log($"Error: Executable not found at '{config.ExecutablePath}'", Color.Red, true);
            return;
        }

        var files = _fileListBox.Items.Cast<string>().ToList();
        Log($"--- Starting execution: {config.Name} ---", Color.LightSkyBlue, true);

        if (files.Count == 0)
        {
            // Check if arguments contain placeholder
            if (config.Arguments.Contains("{file}") || config.Arguments.Contains("$file"))
            {
                Log("Error: This command requires files but the staging area is empty.", Color.Red, true);
            }
            else
            {
                // Execute once without file replacement if no files selected and no placeholder
                await RunProcessAsync(config.ExecutablePath, config.Arguments);
            }
        }
        else
        {
            string stamp = DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString();
            int current = 0;
            int total = files.Count;

            foreach (var file in files)
            {
                current++;
                Log($"========== {current} of {total} ==========", Color.Gold, true);
                
                string args = config.Arguments;

                // 1. Smart Conjunction Logic:
                // Treat '+' as a glue that merges into a single quoted block.
                args = args.Replace("$file+", "\u001f" + file)
                           .Replace("+$file", file + "\u001f")
                           .Replace("{file}+", "\u001f" + file)
                           .Replace("+{file}", file + "\u001f")
                           .Replace("$dt+", "\u001f" + stamp)
                           .Replace("+$dt", stamp + "\u001f")
                           .Replace("+", ""); // Join middle literals

                // Clean up duplicate markers and convert to quotes
                while (args.Contains("\u001f\u001f")) args = args.Replace("\u001f\u001f", "");
                args = args.Replace("\u001f", "\"");

                // 2. Standard replacements for standalone placeholders
                args = args.Replace("{file}", $"\"{file}\"")
                           .Replace("$file", $"\"{file}\"")
                           .Replace("$dt", stamp);

                await RunProcessAsync(config.ExecutablePath, args);
            }
        }

        Log($"--- Finished: {config.Name} ---", Color.LimeGreen, true);
    }

    private async Task RunProcessAsync(string exe, string args)
    {
        await Task.Run(() =>
        {
            try
            {
                Log($"Executing: {exe} {args}");

                var startInfo = new ProcessStartInfo
                    {
                        FileName = exe,
                        Arguments = args,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                using var process = new Process { StartInfo = startInfo };
                
                lock (_processLock) { _activeProcesses.Add(process); }

                process.OutputDataReceived += (s, e) => { if (e.Data != null) Log($"[OUT] {e.Data}"); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) Log($"[ERR] {e.Data}", Color.Red, true); };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();

                lock (_processLock) { _activeProcesses.Remove(process); }
            }
            catch (Exception ex)
            {
                Log($"Exception: {ex.Message}", Color.Red, true);
            }
        });
    }

    private void Log(string message, Color? color = null, bool isBold = false)
    {
        if (!_isLogEnabled) return;

        var entry = new LogEntry 
        { 
            Message = $"[{DateTime.Now:HH:mm:ss}] {message}", 
            Color = color, 
            IsBold = isBold 
        };

        // Use lock to prevent race condition during unpausing
        lock (_logBuffer)
        {
            if (_isLogPaused)
            {
                _logBuffer.Add(entry);
                return;
            }
        }

        _pendingLogs.Enqueue(entry);
    }

    private void BatchFlushLog()
    {
        if (_pendingLogs.IsEmpty || _logConsole.IsDisposed) return;

        // Temporarily stop redrawing to boost performance
        _logConsole.SuspendLayout();
        try
        {
            int linesProcessed = 0;
            while (linesProcessed < MaxLinesPerBatch && _pendingLogs.TryDequeue(out var entry))
            {
                linesProcessed++;
                Color textColor = entry.Color ?? Color.LightGray;
                bool bold = entry.IsBold;

                // Auto-detect errors if not explicitly colored
                if (entry.Color == null && (entry.Message.Contains("Error:") || entry.Message.Contains("[ERR]")))
                {
                    textColor = Color.Red;
                    bold = true;
                }

                _logConsole.SelectionStart = _logConsole.TextLength;
                _logConsole.SelectionLength = 0;
                _logConsole.SelectionColor = textColor;
                _logConsole.SelectionFont = new Font(_logConsole.Font, bold ? FontStyle.Bold : FontStyle.Regular);
                
                _logConsole.AppendText(entry.Message + Environment.NewLine);
            }

            // Keep the log length manageable (trim from top)
            // Use a faster way to count lines and only trim when significantly over
            int currentLineCount = _logConsole.GetLineFromCharIndex(_logConsole.TextLength) + 1;
            if (currentLineCount > MaxLogLines + 1000)
            {
                int linesToRemove = currentLineCount - MaxLogLines;
                int charsToRemove = _logConsole.GetFirstCharIndexFromLine(linesToRemove);
                if (charsToRemove > 0)
                {
                    _logConsole.ReadOnly = false;
                    _logConsole.Select(0, charsToRemove);
                    _logConsole.SelectedText = "";
                    _logConsole.ReadOnly = true;
                }
            }

            _logConsole.SelectionStart = _logConsole.TextLength;
            _logConsole.ScrollToCaret();
        }
        finally
        {
            _logConsole.ResumeLayout();
        }
    }

    private void FlushLogBuffer()
    {
        lock (_logBuffer)
        {
            foreach (var entry in _logBuffer)
            {
                _pendingLogs.Enqueue(entry);
            }
            _logBuffer.Clear();
        }
    }

    private void CleanupProcesses()
    {
        lock (_processLock)
        {
            foreach (var p in _activeProcesses)
            {
                try { if (!p.HasExited) p.Kill(true); } 
                catch { /* Ignore errors on cleanup */ }
            }
            _activeProcesses.Clear();
        }
    }

    private void SaveCommands()
    {
        try
        {
            var json = JsonSerializer.Serialize(_commands, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigFileName, json);
            Log("Settings saved successfully.", Color.LimeGreen, true);
        }
        catch (Exception ex)
        {
            Log($"Error saving settings: {ex.Message}", Color.Red, true);
        }
    }

    private void LoadCommands()
    {
        string fullPath = Path.GetFullPath(ConfigFileName);
        Log($"Loading config from: {fullPath}");

        if (!File.Exists(ConfigFileName))
        {
            Log("No commands.json found. You can add new commands via the Configuration menu.");
            return;
        }

        try
        {
            var json = File.ReadAllText(ConfigFileName);
            var commands = JsonSerializer.Deserialize<List<CommandConfig>>(json);
            if (commands != null)
            {
                _commands.Clear();
                _commandDeck.Controls.Clear();
                foreach (var cmd in commands)
                {
                    _commands.Add(cmd);
                    CreateCommandButton(cmd);
                }
                Log($"Loaded {commands.Count} commands.", Color.LimeGreen, true);
            }
        }
        catch (Exception ex)
        {
            Log($"Error loading settings: {ex.Message}", Color.Red, true);
        }
    }

    // Inner class for input dialog
    private class CommandInputDialog : Form
    {
        public CommandConfig CommandConfig { get; private set; }
        private TextBox _txtName = null!;
        private TextBox _txtExe = null!;
        private TextBox _txtArgs = null!;

        public CommandInputDialog(CommandConfig? existing = null)
        {
            CommandConfig = existing != null
                ? new CommandConfig(existing.Name, existing.ExecutablePath, existing.Arguments)
                : new CommandConfig();
            SetupUI(existing != null);
            
            if (existing != null)
            {
                _txtName.Text = existing.Name;
                _txtExe.Text = existing.ExecutablePath;
                _txtArgs.Text = existing.Arguments;
                this.Text = "Edit Command";
            }
        }

        private void SetupUI(bool isEditing)
        {
            this.Text = "Add New Command";
            this.Size = new Size(500, 320); // Increased height for tip label
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.CenterParent;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), RowCount = 5, ColumnCount = 3 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
            
            layout.Controls.Add(new Label { Text = "Name:", Anchor = AnchorStyles.Left }, 0, 0);
            _txtName = new TextBox { Dock = DockStyle.Fill };
            layout.Controls.Add(_txtName, 1, 0);
            layout.SetColumnSpan(_txtName, 2);

            layout.Controls.Add(new Label { Text = "Executable:", Anchor = AnchorStyles.Left }, 0, 1);
            _txtExe = new TextBox { Dock = DockStyle.Fill };
            layout.Controls.Add(_txtExe, 1, 1);
            
            var btnBrowse = new Button { Text = "...", Dock = DockStyle.Fill };
            btnBrowse.Click += (s, e) =>
            {
                using var ofd = new OpenFileDialog { Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*" };
                if (ofd.ShowDialog() == DialogResult.OK) _txtExe.Text = ofd.FileName;
            };
            layout.Controls.Add(btnBrowse, 2, 1);

            layout.Controls.Add(new Label { Text = "Arguments:", Anchor = AnchorStyles.Left }, 0, 2);
            _txtArgs = new TextBox { Dock = DockStyle.Fill };
            layout.Controls.Add(_txtArgs, 1, 2);
            layout.SetColumnSpan(_txtArgs, 2);

            var lblTip = new Label 
            { 
                Text = "Tip: Use $file for path and $dt for stamp. Join with + to fix quoting: $file+$dt", 
                ForeColor = Color.DimGray,
                Font = new Font(this.Font.FontFamily, 8),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopLeft
            };
            layout.Controls.Add(lblTip, 1, 3);
            layout.SetColumnSpan(lblTip, 2);

            var panelButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var btnOk = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 80 };
            btnOk.Click += (s, e) =>
            {
                CommandConfig.Name = _txtName.Text;
                CommandConfig.ExecutablePath = _txtExe.Text;
                CommandConfig.Arguments = _txtArgs.Text;
            };
            var btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 80 };
            
            panelButtons.Controls.Add(btnCancel);
            panelButtons.Controls.Add(btnOk);

            // Add delete button if editing
            if (isEditing)
            {
                var btnDelete = new Button { Text = "Delete", DialogResult = DialogResult.Abort, Width = 80, ForeColor = Color.Red };
                panelButtons.Controls.Add(btnDelete);
            }
            
            layout.Controls.Add(panelButtons, 1, 4);
            layout.SetColumnSpan(panelButtons, 2);

            this.Controls.Add(layout);
        }
    }
}
