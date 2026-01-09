# MySimpleApp - Command Runner

A lightweight .NET 8 Windows Forms utility for batch-processing files through custom command-line tools.

## Features

- **Dynamic Command Deck**: Create custom buttons for any executable on your system.
- **File Staging Area**: Select multiple files to be processed using a standard dialog.
- **Batch Processing**: Run a command sequentially for every file in the staging area.
- **Placeholder Support**: Use `{file}` in command arguments to automatically inject the current file path.
- **Standalone Execution**: Run utilities without files by omitting the `{file}` placeholder.
- **Real-time Logging**: Integrated console to monitor standard output and errors.
- **Command Management**: Add, edit, or delete commands with ease.
- **Persistence**: Your configurations are saved automatically to `commands.json`.

## How to Use

1. **Add Files**: Use the high-visibility **Add Files** button in the left panel to stage files.
2. **Configure Commands**: 
   - Go to `Configuration > Add New Command`.
   - Provide a Name, browse for the Executable, and define Arguments.
   - Example: `Name: Ping, Exe: ping.exe, Args: {file} -n 1`.
3. **Execute**: Click a button in the **Command Deck** (right panel). The button will disable and turn grey during execution to prevent overlap.
4. **Manage**:
   - **Edit/Delete**: Toggle `Configuration > Edit Mode Toggle`, then click a command button to modify or remove it.
   - **Clear**: Use `Configuration > Clear File List` to reset the staging area.
5. **Save**: Click `Configuration > Save Settings` to persist your setup.

## Technical Details

- **Framework**: .NET 8.0 Windows Forms.
- **Serialization**: `System.Text.Json`.
- **Concurrency**: Asynchronous process execution via `Task.Run` to keep the UI responsive.
- **Thread Safety**: UI updates (logging) are handled via `Control.Invoke`.

## Prerequisites

- .NET 8.0 SDK or Runtime installed.