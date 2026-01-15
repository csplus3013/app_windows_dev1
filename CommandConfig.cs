namespace MySimpleApp;

public class CommandConfig
{
    public string Name { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string BackgroundColor { get; set; } = "AliceBlue";

    public CommandConfig() { }

    public CommandConfig(string name, string executablePath, string arguments)
    {
        Name = name;
        ExecutablePath = executablePath;
        Arguments = arguments;
    }
}