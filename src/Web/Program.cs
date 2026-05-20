using System.Diagnostics;
using System.Runtime.InteropServices;

// Walk up from bin/Debug/net10.0 until we find the directory containing package.json
var dir = AppContext.BaseDirectory;
while (dir is not null && !File.Exists(Path.Combine(dir, "package.json")))
    dir = Path.GetDirectoryName(dir);

if (dir is null)
{
    Console.Error.WriteLine("ERROR: Could not locate package.json. Run 'npm run dev' manually from src/Web.");
    return 1;
}

Console.WriteLine($"Starting Vite dev server in: {dir}");
Console.WriteLine("Open http://localhost:5173 in your browser.");
Console.WriteLine("Press Ctrl+C to stop.");

var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

using var proc = new Process
{
    StartInfo = new ProcessStartInfo
    {
        FileName         = isWindows ? "cmd.exe" : "/bin/sh",
        Arguments        = isWindows ? "/k npm run dev" : "-c \"npm run dev\"",
        WorkingDirectory = dir,
        UseShellExecute  = true,   // opens a visible terminal window
    }
};

proc.Start();
proc.WaitForExit();
return proc.ExitCode;
