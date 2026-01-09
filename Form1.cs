using System.Diagnostics;
using System.Text.Json;

namespace MySimpleApp;

public partial class Form1 : Form
{
    private readonly List<CommandConfig> _commands = new();
    private const string ConfigFileName = "commands.json";

    // UI Controls
    private MenuStrip _menuStrip = null!;
    private SplitContainer _splitContainer = null!;
    private ListBox _fileListBox = null!;
    private FlowLayoutPanel _commandDeck = null!;
    private RichTextBox _logConsole = null!;
    private Button _btnAddFiles = null!;
    private bool _isEditMode = false;

    public Form1()
    {
        InitializeComponent();
        SetupCustomUI();
        LoadCommands();
    }

    private void SetupCustomUI()
    {
        this.Text = "MySimpleApp - Command Runner";
        this.Size = new Size(1000, 700);

        // Main Container Panel to host everything
        var mainContainer = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(0)
        };
        this.Controls.Add(mainContainer);

        // MenuStrip (Added to mainContainer first to dock at top)
        _menuStrip = new MenuStrip();
        _menuStrip.Dock = DockStyle.Top;
        var configMenu = new ToolStripMenuItem("Configuration");
        configMenu.DropDownItems.Add("Add New Command", null, (s, e) => ShowAddCommandDialog());
        configMenu.DropDownItems.Add("Clear File List", null, (s, e) => _fileListBox.Items.Clear());
        
        var editModeItem = new ToolStripMenuItem("Edit Mode Toggle") { CheckOnClick = true };
        editModeItem.CheckedChanged += (s, e) =>
        {
            _isEditMode = editModeItem.Checked;
            Log($"Edit Mode: {(_isEditMode ? "ON" : "OFF")}");
            RefreshCommandDeck();
        };
        configMenu.DropDownItems.Add(editModeItem);

        configMenu.DropDownItems.Add("Save Settings", null, (s, e) => SaveCommands());
        _menuStrip.Items.Add(configMenu);
        mainContainer.Controls.Add(_menuStrip);
        this.MainMenuStrip = _menuStrip;

        // Log Console (Bottom of Main Container)
        _logConsole = new RichTextBox
        {
            Dock = DockStyle.Bottom,
            Height = 150,
            ReadOnly = true,
            BackColor = Color.Black,
            ForeColor = Color.LightGray,
            Font = new Font("Consolas", 9)
        };
        mainContainer.Controls.Add(_logConsole);

        // SplitContainer (Fill remaining Main Container)
        _splitContainer = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 300
        };
        mainContainer.Controls.Add(_splitContainer);

        // Left Panel (File Staging)
        var leftPanel = new Panel { Dock = DockStyle.Fill };
        _btnAddFiles = new Button
        {
            Text = "Add Files",
            Dock = DockStyle.Top,
            Height = 45,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.LightSteelBlue,
            Font = new Font(this.Font, FontStyle.Bold)
        };
        _btnAddFiles.Click += (s, e) => AddFiles();
        
        _fileListBox = new ListBox
        {
            Dock = DockStyle.Fill,
            SelectionMode = SelectionMode.MultiExtended
        };
        
        leftPanel.Controls.Add(_fileListBox);
        leftPanel.Controls.Add(_btnAddFiles);
        _splitContainer.Panel1.Controls.Add(leftPanel);

        // Right Panel (Command Deck)
        _commandDeck = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(10)
        };
        _splitContainer.Panel2.Controls.Add(_commandDeck);
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
            Width = 150,
            Height = 60,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(5),
            BackColor = _isEditMode ? Color.MistyRose : Color.AliceBlue,
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
        _commandDeck.Controls.Clear();
        foreach (var cmd in _commands)
        {
            CreateCommandButton(cmd);
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
            Log($"Error: Executable not found at '{config.ExecutablePath}'");
            return;
        }

        var files = _fileListBox.Items.Cast<string>().ToList();
        Log($"--- Starting execution: {config.Name} ---");

        if (files.Count == 0)
        {
            // Check if arguments contain {file} placeholder
            if (config.Arguments.Contains("{file}"))
            {
                Log("Error: This command requires files but the staging area is empty.");
            }
            else
            {
                // Execute once without file replacement if no files selected and no placeholder
                await RunProcessAsync(config.ExecutablePath, config.Arguments);
            }
        }
        else
        {
            foreach (var file in files)
            {
                var args = config.Arguments.Replace("{file}", $"\"{file}\"");
                await RunProcessAsync(config.ExecutablePath, args);
            }
        }

        Log($"--- Finished: {config.Name} ---");
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
                process.OutputDataReceived += (s, e) => { if (e.Data != null) Log($"[OUT] {e.Data}"); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) Log($"[ERR] {e.Data}"); };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
            }
            catch (Exception ex)
            {
                Log($"Exception: {ex.Message}");
            }
        });
    }

    private void Log(string message)
    {
        if (_logConsole.InvokeRequired)
        {
            _logConsole.Invoke(new Action(() => Log(message)));
            return;
        }
        _logConsole.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        _logConsole.SelectionStart = _logConsole.Text.Length;
        _logConsole.ScrollToCaret();
    }

    private void SaveCommands()
    {
        try
        {
            var json = JsonSerializer.Serialize(_commands, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigFileName, json);
            Log("Settings saved successfully.");
        }
        catch (Exception ex)
        {
            Log($"Error saving settings: {ex.Message}");
        }
    }

    private void LoadCommands()
    {
        if (!File.Exists(ConfigFileName)) return;

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
            }
        }
        catch (Exception ex)
        {
            Log($"Error loading settings: {ex.Message}");
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
            this.Size = new Size(500, 280);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.CenterParent;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), RowCount = 4, ColumnCount = 3 };
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
            
            layout.Controls.Add(panelButtons, 1, 3);
            layout.SetColumnSpan(panelButtons, 2);

            this.Controls.Add(layout);
        }
    }
}
