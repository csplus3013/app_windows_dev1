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
    private readonly ConcurrentDictionary<CommandConfig, CancellationTokenSource> _runningCommands = new();
    
    // Logging State
    private bool _isLogEnabled = true;
    private bool _isLogPaused = false;
    private struct LogEntry { public string Message; public Color? Color; public bool IsBold; }
    private readonly List<LogEntry> _logBuffer = new();
    private readonly ConcurrentQueue<LogEntry> _pendingLogs = new();
    private readonly System.Windows.Forms.Timer _logFlushTimer;
    private const int MaxLogLines = 100000;
    private const int MaxLinesPerBatch = 500;
    private readonly List<LogEntry> _allLogs = new(); // Full history for filtering
    private TextBox _filterTextBox = null!;
    private CheckBox _chkApplyFilter = null!;

    // UI Controls
    private MenuStrip _menuStrip = null!;
    private SplitContainer _splitContainer = null!;
    private ListBox _fileListBox = null!;
    private FlowLayoutPanel _commandDeck = null!;
    private RichTextBox _logConsole = null!;
    private Label _lblFiles = null!;
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
        var clearListItem = new ToolStripMenuItem("Clear List", null, (s, e) => { _fileListBox.Items.Clear(); UpdateStagedFilesInfo(); });
        clearListItem.ForeColor = Color.Maroon;

        // Quick Action: Clear Log
        var clearLogItem = new ToolStripMenuItem("Clear Log", null, (s, e) => ClearLogHistory());
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
            RowCount = 5, // 0:lbl, 1:list, 2:actions, 3:filter, 4:log
            ColumnCount = 1,
            Padding = new Padding(10, 20, 10, 10)
        };
        leftTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // 0: Header label
        leftTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // 1: File List
        leftTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // 2: Log Controls
        leftTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // 3: Filter Controls
        leftTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 320)); // 4: Log Console area

        _lblFiles = new Label 
        { 
            Text = "Staged Files: (0)", 
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
            AllowDrop = true // Enable Drag and Drop
        };
        _fileListBox.SelectedIndexChanged += (s, e) => UpdateStagedFilesInfo();

        // Drag and Drop Handlers
        _fileListBox.DragEnter += (s, e) =>
        {
            if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effect = DragDropEffects.Copy;
            else
                e.Effect = DragDropEffects.None;
        };

        _fileListBox.DragDrop += (s, e) =>
        {
            if (e.Data != null && e.Data.GetData(DataFormats.FileDrop) is string[] droppedFiles)
            {
                _fileListBox.BeginUpdate();
                foreach (var file in droppedFiles)
                {
                    // Basic check to avoid folders if you only want files, 
                    // though usually utilities handle both.
                    if (File.Exists(file) || Directory.Exists(file))
                    {
                        _fileListBox.Items.Add(file);
                    }
                }
                _fileListBox.EndUpdate();
                UpdateStagedFilesInfo();
            }
        };

        // Context Menu for ListBox
        var listContextMenu = new ContextMenuStrip();
        listContextMenu.Items.Add("Remove Selected", null, (s, e) => RemoveSelectedFiles());
        listContextMenu.Items.Add(new ToolStripSeparator());
        listContextMenu.Items.Add("Clear All", null, (s, e) => { _fileListBox.Items.Clear(); UpdateStagedFilesInfo(); });
        _fileListBox.ContextMenuStrip = listContextMenu;

        // 5. Log Controls
        // 5. Log Controls Panel
        var logTable = new TableLayoutPanel 
        { 
            Dock = DockStyle.Fill, 
            ColumnCount = 3,
            RowCount = 1,
            Height = 30,
            Margin = new Padding(0, 5, 0, 0)
        };
        logTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        logTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        logTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        
        var chkEnableLog = new CheckBox { Text = "Enable Log", Checked = true, AutoSize = true, Anchor = AnchorStyles.Left };
        chkEnableLog.CheckedChanged += (s, e) => _isLogEnabled = chkEnableLog.Checked;
        
        var chkPauseLog = new CheckBox { Text = "Pause Log", Checked = false, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(10, 0, 0, 0) };
        chkPauseLog.CheckedChanged += (s, e) => 
        {
            _isLogPaused = chkPauseLog.Checked;
            if (!_isLogPaused) FlushLogBuffer();
        };

        var chkSelectAll = new CheckBox { Text = "Select All", Checked = false, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(10, 0, 0, 0) };
        chkSelectAll.CheckedChanged += (s, e) => 
        {
            _fileListBox.BeginUpdate();
            for (int i = 0; i < _fileListBox.Items.Count; i++)
            {
                _fileListBox.SetSelected(i, chkSelectAll.Checked);
            }
            _fileListBox.EndUpdate();
            UpdateStagedFilesInfo();
        };
        
        logTable.Controls.Add(chkEnableLog, 0, 0);
        logTable.Controls.Add(chkPauseLog, 1, 0);
        logTable.Controls.Add(chkSelectAll, 2, 0);

        // 5.5. Filter Controls Panel
        var filterTable = new TableLayoutPanel 
        { 
            Dock = DockStyle.Fill, 
            ColumnCount = 4,
            RowCount = 1,
            Height = 30,
            Margin = new Padding(0, 5, 0, 0)
        };
        filterTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));    // 0: Label
        filterTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200)); // 1: TextBox (Shortened)
        filterTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));    // 2: Checkbox
        filterTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); // 3: Filler

        filterTable.Controls.Add(new Label { Text = "Filter:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        
        _filterTextBox = new TextBox { Width = 190, Font = new Font("Segoe UI", 9), Anchor = AnchorStyles.Left };
        _filterTextBox.TextChanged += (s, e) => { if (_chkApplyFilter.Checked) RefreshLogConsole(); };
        filterTable.Controls.Add(_filterTextBox, 1, 0);

        _chkApplyFilter = new CheckBox { Text = "Apply", Checked = false, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(5, 0, 0, 0) };
        _chkApplyFilter.CheckedChanged += (s, e) => RefreshLogConsole();
        filterTable.Controls.Add(_chkApplyFilter, 2, 0);

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
        logContextMenu.Items.Add("Clear Console", null, (s, e) => ClearLogHistory());
        _logConsole.ContextMenuStrip = logContextMenu;
        
        leftTable.Controls.Add(_lblFiles, 0, 0);
        leftTable.Controls.Add(_fileListBox, 0, 1);
        leftTable.Controls.Add(logTable, 0, 2);
        leftTable.Controls.Add(filterTable, 0, 3);
        leftTable.Controls.Add(_logConsole, 0, 4);
        
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
            UpdateStagedFilesInfo();
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
                return;
            }

            // Toggle Logic: If running, STOP it. If idle, START it.
            if (_runningCommands.TryGetValue(config, out var cts))
            {
                Log($"Requesting stop for: {config.Name}...", Color.Orange, true);
                cts.Cancel();
                return;
            }

            // Start Execution
            var newCts = new CancellationTokenSource();
            if (!_runningCommands.TryAdd(config, newCts)) return;

            string originalText = btn.Text;
            Color originalBack = btn.BackColor;
            Color originalFore = btn.ForeColor;

            btn.BackColor = Color.Crimson;
            btn.ForeColor = Color.White;
            btn.Text = "STOP: " + originalText;

            try
            {
                await ExecuteCommandAsync(config, newCts.Token);
            }
            catch (OperationCanceledException)
            {
                Log($"Task Stopped: {config.Name}", Color.Orange, true);
            }
            catch (Exception ex)
            {
                Log($"Error: {ex.Message}", Color.Red, true);
            }
            finally
            {
                _runningCommands.TryRemove(config, out _);
                newCts.Dispose();

                // UI Restore (Check if disposed in case refresh happened during run)
                if (!btn.IsDisposed)
                {
                    btn.BackColor = originalBack;
                    btn.ForeColor = originalFore;
                    btn.Text = originalText;
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
        UpdateStagedFilesInfo();
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

    private async Task ExecuteCommandAsync(CommandConfig config, CancellationToken token)
    {
        if (!File.Exists(config.ExecutablePath))
        {
            Log($"Error: Executable not found at '{config.ExecutablePath}'", Color.Red, true);
            return;
        }

        // Determine target files: Only process explicitly selected files
        var files = _fileListBox.SelectedItems.Cast<string>().ToList();

        Log($"--- Starting execution: {config.Name} ---", Color.LightSkyBlue, true);
        
        if (files.Count == 0)
        {
            // Check if arguments contain placeholder
            if (config.Arguments.Contains("{file}") || config.Arguments.Contains("$file"))
            {
                Log("Error: No files selected. Please select files from the list to process.", Color.Red, true);
            }
            else
            {
                // Execute once without file replacement if no files selected and no placeholder
                await RunProcessAsync(config.ExecutablePath, config.Arguments, token);
            }
        }
        else
        {
            string stamp = DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString();
            int current = 0;
            int total = files.Count;

            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();

                current++;
                Log($"========== {current} of {total} ==========", Color.Gold, true);
                
                // Rate-limit UI updates to prevent flooding the message loop (especially when log is disabled)
                if (total < 100 || current % 5 == 0 || current == total)
                {
                    UpdateStagedFilesInfo(current, total);
                }
                
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

                await RunProcessAsync(config.ExecutablePath, args, token);
            }
        }

        Log($"--- Finished: {config.Name} ---", Color.LimeGreen, true);
        UpdateStagedFilesInfo();
    }

    private async Task RunProcessAsync(string exe, string args, CancellationToken token)
    {
        await Task.Run(() =>
        {
            try
            {
                if (_isLogEnabled) Log($"Executing: {exe} {args}");

                var startInfo = new ProcessStartInfo
                    {
                        FileName = exe,
                        Arguments = args,
                        RedirectStandardOutput = _isLogEnabled,
                        RedirectStandardError = _isLogEnabled,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                using var process = new Process { StartInfo = startInfo };
                
                lock (_processLock) { _activeProcesses.Add(process); }

                // Force kill native process immediately on cancellation
                using var registration = token.Register(() => 
                {
                    try { if (!process.HasExited) process.Kill(true); } 
                    catch { /* Ignore */ }
                });

                process.OutputDataReceived += (s, e) => { if (e.Data != null && _isLogEnabled) Log($"[OUT] {e.Data}"); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null && _isLogEnabled) Log($"[ERR] {e.Data}", Color.Red, true); };

                process.Start();

                if (_isLogEnabled)
                {
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                }
                
                // Use a wait loop that checks for cancellation to ensure we don't hang on WaitForExit
                while (!process.WaitForExit(100))
                {
                    if (token.IsCancellationRequested)
                    {
                        try { if (!process.HasExited) process.Kill(true); } catch { }
                        token.ThrowIfCancellationRequested();
                    }
                }

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

    private void ClearLogHistory()
    {
        _logConsole.Clear();
        lock (_allLogs)
        {
            _allLogs.Clear();
        }
        // Also wipe any logs currently waiting to be displayed
        while (_pendingLogs.TryDequeue(out _)) { }
        lock (_logBuffer)
        {
            _logBuffer.Clear();
        }
    }

    /// <summary>
    /// Appends text to the log with support for ANSI escape sequences.
    /// Handles basic SGR codes: 0 (Reset), 1 (Bold), 30-37 (Foreground colors).
    /// </summary>
    private void AppendStyledText(string text, Color baseColor, bool baseBold)
    {
        // Performance: Skip regex/splitting if no ANSI code is present
        if (text.IndexOf('\x1b') < 0)
        {
            _logConsole.SelectionStart = _logConsole.TextLength;
            _logConsole.SelectionColor = baseColor;
            _logConsole.SelectionFont = new Font(_logConsole.Font, baseBold ? FontStyle.Bold : FontStyle.Regular);
            _logConsole.AppendText(text + Environment.NewLine);
            return;
        }

        // Simple regex to find ANSI escape sequences: ESC [ <codes> m
        var segments = System.Text.RegularExpressions.Regex.Split(text, @"(\x1b\[[0-9;]*m)");
        
        Color currentColor = baseColor;
        bool currentBold = baseBold;

        foreach (var segment in segments)
        {
            if (segment.StartsWith("\x1b["))
            {
                // Parse the code (e.g., "\x1b[31;1m")
                string codePart = segment.Substring(2, segment.Length - 3);
                var parts = codePart.Split(';');
                foreach (var p in parts)
                {
                    if (int.TryParse(p, out int n))
                    {
                        if (n == 0) { currentColor = baseColor; currentBold = baseBold; }
                        else if (n == 1) currentBold = true;
                        else if (n >= 30 && n <= 37) currentColor = GetAnsiColor(n);
                        else if (n == 39) currentColor = baseColor;
                    }
                }
            }
            else if (!string.IsNullOrEmpty(segment))
            {
                _logConsole.SelectionStart = _logConsole.TextLength;
                _logConsole.SelectionLength = 0;
                _logConsole.SelectionColor = currentColor;
                _logConsole.SelectionFont = new Font(_logConsole.Font, currentBold ? FontStyle.Bold : FontStyle.Regular);
                _logConsole.AppendText(segment);
            }
        }
        _logConsole.AppendText(Environment.NewLine);
    }

    private void RefreshLogConsole()
    {
        if (_logConsole.IsDisposed) return;

        // Capture state BEFORE clear
        bool wasAtBottom = _logConsole.SelectionStart >= _logConsole.TextLength - 10;
        int oldSelection = _logConsole.SelectionStart;

        _logConsole.SuspendLayout();
        _logConsole.Clear();

        // Transfer pending logs to history so they are not lost during refresh
        lock (_allLogs)
        {
            while (_pendingLogs.TryDequeue(out var entry))
            {
                _allLogs.Add(entry);
                if (_allLogs.Count > MaxLogLines) _allLogs.RemoveAt(0);
            }
        }

        List<LogEntry> logsToDisplay;
        lock (_allLogs)
        {
            logsToDisplay = _allLogs.ToList();
        }

        string filter = _filterTextBox.Text;
        bool apply = _chkApplyFilter.Checked;

        foreach (var entry in logsToDisplay)
        {
            if (apply && !string.IsNullOrEmpty(filter) && entry.Message.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            Color textColor = entry.Color ?? Color.LightGray;
            bool bold = entry.IsBold;

            if (entry.Color == null && (entry.Message.Contains("Error:") || entry.Message.Contains("[ERR]")))
            {
                textColor = Color.Red;
                bold = true;
            }

            if (entry.Message.Contains("\x1b["))
            {
                // Note: This helper appends a newline
                AppendStyledText(entry.Message, textColor, bold);
            }
            else
            {
                _logConsole.SelectionStart = _logConsole.TextLength;
                _logConsole.SelectionColor = textColor;
                _logConsole.SelectionFont = new Font(_logConsole.Font, bold ? FontStyle.Bold : FontStyle.Regular);
                _logConsole.AppendText(entry.Message + Environment.NewLine);
            }
        }

        // Only scroll to bottom in Refresh if it's explicitly desired
        if (wasAtBottom && _logConsole.TextLength > 0)
        {
            _logConsole.SelectionStart = _logConsole.TextLength;
            _logConsole.ScrollToCaret();
        }
        else
        {
            // If not at bottom, don't jump. Selection is already at 0 due to Clear().
            // Simply scrolling to 0 ensures we stay at the top/current view of the reset text.
        }
        
        _logConsole.ResumeLayout();
    }

    private Color GetAnsiColor(int code) => code switch
    {
        30 => Color.Black,
        31 => Color.Crimson,
        32 => Color.LimeGreen,
        33 => Color.Gold,
        34 => Color.CornflowerBlue,
        35 => Color.MediumOrchid,
        36 => Color.DeepSkyBlue,
        37 => Color.White,
        _ => Color.LightGray
    };

    private void UpdateStagedFilesInfo(int? currentOverride = null, int? totalOverride = null)
    {
        if (_lblFiles == null) return;

        int listTotal = _fileListBox.Items.Count;
        
        if (currentOverride.HasValue)
        {
            // PROCESSING MODE: Show current progress through the active set
            int activeTotal = totalOverride ?? listTotal;
            _lblFiles.Text = $"Processing: {activeTotal} (file {currentOverride.Value} of {activeTotal})";
            _lblFiles.ForeColor = Color.RoyalBlue;
        }
        else
        {
            // IDLE MODE: Show list totals and selection context
            _lblFiles.ForeColor = Color.DimGray;
            int selectedCount = _fileListBox.SelectedItems.Count;
            
            if (listTotal == 0)
            {
                _lblFiles.Text = "Staged Files: (0)";
            }
            else if (selectedCount > 1)
            {
                _lblFiles.Text = $"Staged Files: {listTotal} ({selectedCount} selected)";
            }
            else
            {
                int current = _fileListBox.SelectedIndex + 1;
                string status = current > 0 ? $"(item {current} of {listTotal})" : $"({listTotal} files)";
                _lblFiles.Text = $"Staged Files: {listTotal} {status}";
            }
        }
    }

    private void BatchFlushLog()
    {
        if (_pendingLogs.IsEmpty || _logConsole.IsDisposed) return;

        // Temporarily stop redrawing to boost performance
        _logConsole.SuspendLayout();
        try
        {
            // Smart scroll: Only scroll to bottom if we are already at the bottom
            bool wasAtBottom = _logConsole.SelectionStart >= _logConsole.TextLength - 1;

            int linesProcessed = 0;
            var batchToStore = new List<LogEntry>();

            while (linesProcessed < MaxLinesPerBatch && _pendingLogs.TryDequeue(out var entry))
            {
                linesProcessed++;
                batchToStore.Add(entry);

                Color textColor = entry.Color ?? Color.LightGray;
                bool bold = entry.IsBold;

                // Auto-detect errors if not explicitly colored
                if (entry.Color == null && (entry.Message.Contains("Error:") || entry.Message.Contains("[ERR]")))
                {
                    textColor = Color.Red;
                    bold = true;
                }

                // Apply dynamic filtering
                if (_chkApplyFilter.Checked && !string.IsNullOrEmpty(_filterTextBox.Text))
                {
                    if (entry.Message.IndexOf(_filterTextBox.Text, StringComparison.OrdinalIgnoreCase) < 0)
                        continue; // Skip this line
                }

                if (entry.Message.Contains("\x1b["))
                {
                    AppendStyledText(entry.Message, textColor, bold);
                }
                else
                {
                    _logConsole.SelectionStart = _logConsole.TextLength;
                    _logConsole.SelectionLength = 0;
                    _logConsole.SelectionColor = textColor;
                    _logConsole.SelectionFont = new Font(_logConsole.Font, bold ? FontStyle.Bold : FontStyle.Regular);
                    _logConsole.AppendText(entry.Message + Environment.NewLine);
                }
            }

            // Keep the log length manageable (trim from top)
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

            // Batch update history at once to reduce lock contention
            if (batchToStore.Count > 0)
            {
                lock (_allLogs)
                {
                    _allLogs.AddRange(batchToStore);
                    if (_allLogs.Count > MaxLogLines)
                    {
                        _allLogs.RemoveRange(0, _allLogs.Count - MaxLogLines);
                    }
                }
            }

            if (wasAtBottom)
            {
                _logConsole.SelectionStart = _logConsole.TextLength;
                _logConsole.ScrollToCaret();
            }
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
