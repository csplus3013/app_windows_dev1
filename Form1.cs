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

        // MenuStrip
        _menuStrip = new MenuStrip();
        var configMenu = new ToolStripMenuItem("Configuration");
        configMenu.DropDownItems.Add("Add New Command", null, (s, e) => ShowAddCommandDialog());
        configMenu.DropDownItems.Add("Save Settings", null, (s, e) => SaveCommands());
        _menuStrip.Items.Add(configMenu);
        this.MainMenuStrip = _menuStrip;
        this.Controls.Add(_menuStrip);

        // Log Console (Bottom)
        _logConsole = new RichTextBox
        {
            Dock = DockStyle.Bottom,
            Height = 150,
            ReadOnly = true,
            BackColor = Color.Black,
            ForeColor = Color.LightGray,
            Font = new Font("Consolas", 9)
        };
        this.Controls.Add(_logConsole);

        // SplitContainer
        _splitContainer = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 300
        };
        this.Controls.Add(_splitContainer);

        // Left Panel (File Staging)
        var leftPanel = new Panel { Dock = DockStyle.Fill };
        _btnAddFiles = new Button
        {
            Text = "Add Files",
            Dock = DockStyle.Top,
            Height = 40,
            FlatStyle = FlatStyle.Flat
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
            Margin = new Padding(5)
        };
        btn.Click += async (s, e) => await ExecuteCommandAsync(config);
        _commandDeck.Controls.Add(btn);
    }

    private async Task ExecuteCommandAsync(CommandConfig config)
    {
        if (_fileListBox.Items.Count == 0)
        {
            Log("Error: No files selected in the staging area.");
            return;
        }

        if (!File.Exists(config.ExecutablePath))
        {
            Log($"Error: Executable not found at '{config.ExecutablePath}'");
            return;
        }

        var files = _fileListBox.Items.Cast<string>().ToList();
        Log($"--- Starting execution: {config.Name} ---");

        foreach (var file in files)
        {
            await Task.Run(() => 
            {
                try
                {
                    var args = config.Arguments.Replace("{file}", $"\"{file}\"");
                    Log($"Executing: {config.ExecutablePath} {args}");

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = config.ExecutablePath,
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
                    Log($"Exception processing {file}: {ex.Message}");
                }
            });
        }

        Log($"--- Finished: {config.Name} ---");
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
        public CommandConfig CommandConfig { get; private set; } = new();
        private TextBox _txtName = null!;
        private TextBox _txtExe = null!;
        private TextBox _txtArgs = null!;

        public CommandInputDialog()
        {
            SetupUI();
        }

        private void SetupUI()
        {
            this.Text = "Add New Command";
            this.Size = new Size(400, 250);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.CenterParent;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), RowCount = 4, ColumnCount = 2 };
            
            layout.Controls.Add(new Label { Text = "Name:", Anchor = AnchorStyles.Left }, 0, 0);
            _txtName = new TextBox { Dock = DockStyle.Fill };
            layout.Controls.Add(_txtName, 1, 0);

            layout.Controls.Add(new Label { Text = "Executable:", Anchor = AnchorStyles.Left }, 0, 1);
            _txtExe = new TextBox { Dock = DockStyle.Fill };
            layout.Controls.Add(_txtExe, 1, 1);

            layout.Controls.Add(new Label { Text = "Arguments:", Anchor = AnchorStyles.Left }, 0, 2);
            _txtArgs = new TextBox { Dock = DockStyle.Fill };
            layout.Controls.Add(_txtArgs, 1, 2);

            var btnOk = new Button { Text = "OK", DialogResult = DialogResult.OK };
            btnOk.Click += (s, e) => 
            {
                CommandConfig = new CommandConfig(_txtName.Text, _txtExe.Text, _txtArgs.Text);
            };
            layout.Controls.Add(btnOk, 1, 3);

            this.Controls.Add(layout);
        }
    }
}
