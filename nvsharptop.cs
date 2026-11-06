#:package Spectre.Console@*

using System.Diagnostics;
using Spectre.Console;
using Spectre.Console.Rendering;


var cliParameters = CliParameters.Create(args);
var session = new UserSession(cliParameters);
var devices = new DeviceCollection();
AnsiConsole.Clear();
var renderer = new DeviceCollectionRenderer(cliParameters.DisplayInterval);
while (!session.IsCancelled)
{
    bool modeChanged = renderer.ReadModeInput();
    if (renderer.IsQuitRequested)
        break;
    devices.Update();
    renderer.TryRender(devices, modeChanged);
    session.Sleep();
}
if (cliParameters.CleanupScreen)
    AnsiConsole.Clear();


public enum DeviceType
{
    Gpu
}

record DeviceInfo(string Id, string Name, int Temp, int Util, int MemUsed, int MemTotal, DeviceType Type);

class DeviceCollection : IEnumerable<DeviceInfo>
{
    public DateTime LastDisplay = DateTime.UtcNow;
    private readonly Dictionary<string, Queue<(int Util, int MemPct)>> history = new();
    private List<DeviceInfo> devices = new();

    public void Update()
    {
        devices = QueryDevices();
    }

    public void AddHistorySamples(int maxSamples)
    {
        foreach (var device in devices)
        {
            if (!history.TryGetValue(device.Id, out var samples))
                history[device.Id] = samples = new Queue<(int Util, int MemPct)>();

            int memPct = device.MemTotal == 0 ? 0 : Math.Clamp((int)(device.MemUsed * 100.0 / device.MemTotal), 0, 100);
            samples.Enqueue((Math.Clamp(device.Util, 0, 100), memPct));
            while (samples.Count > maxSamples)
                samples.Dequeue();
        }
    }

    public (int Util, int MemPct)[] GetHistory(DeviceInfo device)
    {
        return history.TryGetValue(device.Id, out var samples) ? samples.ToArray() : [];
    }

    private List<DeviceInfo> QueryDevices()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "nvidia-smi",
            Arguments = "--query-gpu=index,name,temperature.gpu,utilization.gpu,memory.used,memory.total --format=csv,noheader,nounits",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        var outp = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return outp
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(',', StringSplitOptions.TrimEntries))
            .Where(a => a.Length == 6)
            .Select(a => new DeviceInfo(
                a[0], a[1],
                int.Parse(a[2]), int.Parse(a[3]),
                int.Parse(a[4]), int.Parse(a[5]),
                DeviceType.Gpu
            ))
            .ToList();
    }

    public IEnumerator<DeviceInfo> GetEnumerator() => devices.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}


record CliParameters(double SampleInterval, double DisplayInterval, bool CleanupScreen)
{
    public static CliParameters Create(string[] args)
    {
        if (args.Any(a => a == "--help" || a == "-h"))
        {
            PrintHelp();
            Environment.Exit(0);
        }
        double sample = ParseArg(args, "--sample-interval", 0.1);
        double display = ParseArg(args, "--display-interval", 1);
        bool cleanup = ParseBoolArg(args, "--cleanup-screen", true);
        return new CliParameters(sample, display, cleanup);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Usage: dotnet run --file nvsharptop.cs -- [options]\n");
        Console.WriteLine("Options:");
        Console.WriteLine("  --sample-interval <sec>   Set the sample interval in seconds (default: 0.1)");
        Console.WriteLine("  --display-interval <sec>  Set the display refresh interval in seconds (default: 1)");
        Console.WriteLine("  --cleanup-screen <true|false>  Clear the screen on exit (default: true)");
        Console.WriteLine("  Press A for the table, B for meters, C for history bars, Tab to cycle, or Q to quit");
        Console.WriteLine("  --help, -h                Show this help message and exit");
    }

    private static double ParseArg(string[] args, string name, double def)
    {
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == name && double.TryParse(args[i + 1], out var t) && t > 0) return t;
        return def;
    }
    private static bool ParseBoolArg(string[] args, string name, bool def)
    {
        for (int i = 0; i < args.Length; i++)
            if (args[i] == name)
            {
                if (i + 1 < args.Length && bool.TryParse(args[i + 1], out var b)) return b;
                return true;
            }
        return def;
    }
}

class UserSession
{
    private readonly CancellationTokenSource cts = new();
    private readonly double sampleInterval;
    public CancellationToken Token => cts.Token;
    public UserSession(CliParameters cliParameters)
    {
        sampleInterval = cliParameters.SampleInterval;
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    }
    public void Sleep()
    {
        int ticks = (int)(sampleInterval * 10);
        for (int i = 0; i < ticks && !cts.Token.IsCancellationRequested; i++)
            cts.Token.WaitHandle.WaitOne(100);
    }
    public bool IsCancelled => cts.IsCancellationRequested;
}

class DeviceCollectionRenderer
{
    private readonly double displayInterval;
    private DisplayMode mode = DisplayMode.A;
    public bool IsQuitRequested { get; private set; }

    public DeviceCollectionRenderer(double displayInterval)
    {
        this.displayInterval = displayInterval;
    }

    public bool ReadModeInput()
    {
        bool changed = false;
        while (Console.KeyAvailable)
        {
            var key = Console.ReadKey(intercept: true).Key;
            if (key == ConsoleKey.Q)
            {
                IsQuitRequested = true;
                break;
            }
            var newMode = key switch
            {
                ConsoleKey.A => DisplayMode.A,
                ConsoleKey.B => DisplayMode.B,
                ConsoleKey.C => DisplayMode.C,
                ConsoleKey.Tab => mode == DisplayMode.A ? DisplayMode.B : DisplayMode.A,
                _ => mode
            };
            if (key == ConsoleKey.Tab && mode == DisplayMode.B)
                newMode = DisplayMode.C;
            else if (key == ConsoleKey.Tab && mode == DisplayMode.C)
                newMode = DisplayMode.A;
            if (newMode != mode)
            {
                mode = newMode;
                changed = true;
            }
        }
        return changed;
    }

    public void TryRender(DeviceCollection devices, bool force = false)
    {
        if (force || ShouldDisplay(devices.LastDisplay, displayInterval))
        {
            int consoleWidth = Math.Max(1, Console.WindowWidth);
            int columnCount = mode == DisplayMode.C ? 1 : consoleWidth >= 80 && devices.Count() > 1 ? 2 : 1;
            int panelWidth = Math.Max(1, (consoleWidth - (columnCount - 1) * 4) / columnCount);
            int meterWidth = Math.Clamp((panelWidth - 26) / 4, 8, 20);
            int graphWidth = Math.Max(1, panelWidth - 20);
            if (mode == DisplayMode.C)
                devices.AddHistorySamples(graphWidth);
            var display = new DeviceCollectionDisplay();
            Console.SetCursorPosition(0, 0);
            AnsiConsole.Clear();
            Console.SetCursorPosition(0, 0);
            display.Render(devices, meterWidth, columnCount, graphWidth, displayInterval, mode);
            devices.LastDisplay = DateTime.UtcNow;
            Console.CursorVisible = false;
        }
    }

    private static bool ShouldDisplay(DateTime lastDisplay, double displayInterval)
    {
        return (DateTime.UtcNow - lastDisplay).TotalSeconds >= displayInterval;
    }
}

enum DisplayMode
{
    A,
    B,
    C
}

class DeviceCollectionDisplay
{
    public void Render(DeviceCollection devices, int meterWidth, int columnCount, int graphWidth, double displayInterval, DisplayMode mode)
    {
        string modeDescription = mode switch
        {
            DisplayMode.A => "A: table",
            DisplayMode.B => "B: meters",
            _ => "C: history"
        };
        AnsiConsole.Write(new Markup($"[bold]NvHtop[/] [grey][[{DateTime.Now:HH:mm:ss}]][/], Refresh every {displayInterval}s  [grey]Mode {modeDescription} | A/B/C or Tab to switch | Q to quit[/]\n"));
        if (!devices.Any())
        {
            AnsiConsole.Write(new Markup("[yellow]No GPUs detected.[/]"));
            return;
        }

        switch (mode)
        {
            case DisplayMode.A:
                AnsiConsole.Write(DeviceTableDisplay.Render(devices));
                break;
            case DisplayMode.B:
                AnsiConsole.Write(new Markup("[green]█[/] Utilization  [cyan]█[/] Memory\n"));
                AnsiConsole.Write(DeviceMeterDisplay.Render(devices, meterWidth, columnCount));
                break;
            case DisplayMode.C:
                AnsiConsole.Write(new Markup("[green]Utilization[/] and [cyan]memory[/] history, left to right\n"));
                AnsiConsole.Write(DeviceHistoryChartDisplay.Render(devices, graphWidth));
                break;
        }
    }
}

static class DeviceHistoryChartDisplay
{
    public static Grid Render(DeviceCollection devices, int graphWidth)
    {
        var grid = new Grid();
        grid.AddColumn();

        foreach (var device in devices)
            grid.AddRow(RenderDeviceChart(device, devices.GetHistory(device), graphWidth));
        return grid;
    }

    private static IRenderable RenderDeviceChart(DeviceInfo device, (int Util, int MemPct)[] samples, int graphWidth)
    {
        var chart = new Grid();
        chart.AddColumn();
        int currentMemPct = device.MemTotal == 0 ? 0 : Math.Clamp((int)(device.MemUsed * 100.0 / device.MemTotal), 0, 100);
        chart.AddRow(new Markup($"[bold]GPU {Markup.Escape(device.Id)} {Markup.Escape(device.Name)}[/] [grey]{device.Temp}°C[/]"));
        chart.AddRow(new Markup($"[green]U {device.Util,3}%[/] {RenderSeries(samples.Select(sample => sample.Util), graphWidth, true)}"));
        chart.AddRow(new Markup($"[cyan]M {currentMemPct,3}%[/] {RenderSeries(samples.Select(sample => sample.MemPct), graphWidth, false)}"));
        return chart;
    }

    private static string RenderSeries(IEnumerable<int> values, int width, bool utilization)
    {
        const string levels = "▁▂▃▄▅▆▇█";
        var samples = values.TakeLast(width).ToArray();
        int emptySamples = Math.Max(0, width - samples.Length);
        var sparkline = new List<string>(width);
        foreach (int value in samples)
        {
            int level = Math.Clamp(value * levels.Length / 101, 0, levels.Length - 1);
            string color = utilization
                ? value >= 80 ? "red" : value >= 40 ? "yellow" : "green"
                : "cyan";
            sparkline.Add($"[{color}]{levels[level]}[/]");
        }
        sparkline.AddRange(Enumerable.Repeat(" ", emptySamples));
        return string.Join("", sparkline);
    }
}

static class DeviceTableDisplay
{
    public static Table Render(DeviceCollection devices)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .Expand();
        table.AddColumn("[bold]GPU[/]");
        table.AddColumn("[bold]Name[/]");
        table.AddColumn("[bold]Temp[/]");
        table.AddColumn("[bold]Utilization[/]");
        table.AddColumn("[bold]Memory[/]");

        foreach (var device in devices)
        {
            int util = Math.Clamp(device.Util, 0, 100);
            int memPct = device.MemTotal == 0 ? 0 : Math.Clamp((int)(device.MemUsed * 100.0 / device.MemTotal), 0, 100);
            string tempColor = device.Temp >= 80 ? "red" : device.Temp >= 60 ? "yellow" : "green";
            string utilColor = util >= 80 ? "red" : util >= 40 ? "yellow" : "green";
            table.AddRow(
                $"[bold]{Markup.Escape(device.Id)}[/]",
                Markup.Escape(device.Name),
                $"[{tempColor}]{device.Temp}°C[/]",
                $"[{utilColor}]{util}%[/]",
                $"[cyan]{device.MemUsed:N0}[/]/[grey]{device.MemTotal:N0} MiB ({memPct}%)[/]"
            );
        }
        return table;
    }
}

static class DeviceMeterDisplay
{
    public static Grid Render(DeviceCollection devices, int meterWidth, int columnCount)
    {
        var grid = new Grid();
        for (int i = 0; i < columnCount; i++)
            grid.AddColumn();

        var renderables = new List<IRenderable>();
        foreach (var device in devices)
            renderables.Add(RenderDevicePanel(device, meterWidth));
        for (int i = 0; i < renderables.Count; i += columnCount)
        {
            var row = renderables.Skip(i).Take(columnCount).ToList();
            while (row.Count < columnCount)
                row.Add(new Text(""));
            grid.AddRow(row.ToArray());
        }
        return grid;
    }

    private static IRenderable RenderDevicePanel(DeviceInfo device, int meterWidth)
    {
        var grid = new Grid();
        grid.AddColumn();
        int util = Math.Clamp(device.Util, 0, 100);
        int memPct = device.MemTotal == 0 ? 0 : Math.Clamp((int)(device.MemUsed * 100.0 / device.MemTotal), 0, 100);
        string tempColor = device.Temp >= 80 ? "red" : device.Temp >= 60 ? "yellow" : "green";
        string utilColor = util >= 80 ? "red" : util >= 40 ? "yellow" : "green";
        string title = $"[bold]GPU {Markup.Escape(device.Id)} {Markup.Escape(device.Name)}[/]  [{tempColor}]{device.Temp}°C[/]";
        grid.AddRow(new Markup(title));
        grid.AddRow(new Markup(
            $"U [{utilColor}]{CreateBar(util, meterWidth)}[/] {util,3}%  " +
            $"M [cyan]{CreateBar(memPct, meterWidth)}[/] {memPct,3}%"));
        return grid;
    }

    private static string CreateBar(int percentage, int width)
    {
        int filled = percentage * width / 100;
        return new string('█', filled) + new string('░', width - filled);
    }
}