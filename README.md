# MySimpleApp - Advanced Command Runner

A high-performance .NET 8 Windows Forms utility designed for engineers and power users to batch-process files through custom command-line tools with surgical precision.

## 🚀 Key Features

- **Dynamic Command Deck**: Instantly create and manage bespoke buttons for any executable or script on your system.
- **Smart Placeholder System**:
  - `$file`: Injects the full, quoted path of the current file.
  - `$dt`: Injects a Unix millisecond timestamp for versioning.
- **Advanced Conjunction Logic**: Use the `+` operator to join paths and variables into a **unified quoted string**.
  - Example: `$file+_+dt` produces `"C:\path\file.png_17679..."` (unified quotes, no broken paths).
- **High-Performance Log Console**:
  - **Asynchronous Batching**: Updates every 100ms to prevent UI freezing during massive output bursts.
  - **Color-Coded Feedback**: Success (**Bold Green**), Errors (**Bold Red**), and Process Starts (**Bold Blue**).
  - **Log Controls**: Enable/Disable or Pause logging. Buffers logs while paused and flushes them on resume.
  - **Scale**: Supports up to 100,000 lines of history with optimized memory trimming.
- **Process Lifecycle Management**: Automatically tracks and terminates all child processes when the application is closed, preventing orphaned "zombie" tasks.
- **Persistence**: Configuration is stored in a simple, portable `commands.json`.

## 🛠 How to Use

### 1. Stage Your Files
Use the **Add Files** menu item to load files into the staging area. You can manage the list via the right-click context menu (Remove Selected / Clear All).

### 2. Configure a Command
Navigate to `Configuration > Add New Command`.
- **Name**: The label for your button.
- **Executable**: Path to your tool (e.g., `c2patool.exe`, `exiftool.exe`, `ffmpeg.exe`).
- **Arguments**: Define how the tool should run.
  - *Standard*: `$file -v`
  - *Output with Timestamp*: `-o $file+_+dt`
  - *Prefix*: `--prefix $dt+_+$file`

### 3. Execution & Management
- Click any button in the **Command Deck** to start batch processing.
- **Iteration Tracking**: The log will display `========== X of Y ==========` for clear progress monitoring.
- **Edit Mode**: Toggle `Configuration > Edit Mode` and click any button to modify its path or arguments.

## ⚙️ Technical Specifications

- **Target Framework**: .NET 8.0 (Windows Forms).
- **Concurrency**: Fully asynchronous process orchestration with thread-safe logging.
- **UI Architecture**: TableLayout/FlowLayout hybrid for a responsive, clean aesthetic.
- **Process I/O**: Redirected `StandardOutput` and `StandardError` with real-time stream processing.

## 📜 Prerequisites

- .NET 8.0 Desktop Runtime installed.