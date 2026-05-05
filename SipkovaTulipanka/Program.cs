// ============================================================
// Program.cs – Vstupní bod aplikace (klasický styl s Main)
// ============================================================

using System;
using System.Threading;
using System.Threading.Tasks;
using MudServer;

class Program
{
    static async Task Main(string[] args)
    {
        // Konfigurovatelný port – lze změnit argumentem příkazové řádky
        // Příklad: dotnet run -- 5000
        int port = args.Length > 0 && int.TryParse(args[0], out int p) ? p : 4000;

        Console.WriteLine("=== MUD Server ===");
        Console.WriteLine($"Spouštím server na portu {port}...");
        Console.WriteLine("Připoj se přes: telnet localhost " + port);
        Console.WriteLine("Zastav server: Ctrl+C");
        Console.WriteLine();

        // Vytvoříme sdílený herní svět – JEDEN pro všechny hráče
        GameWorld world = new GameWorld();

        // Vytvoříme a spustíme TCP server
        MudTcpServer server = new MudTcpServer(port, world);

        // CancellationToken umožňuje čistě zastavit server při Ctrl+C
        using CancellationTokenSource cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // nezastavujeme proces hned, necháme cleanup
            Console.WriteLine("\nZastavuji server...");
            cts.Cancel();
        };

        await server.StartAsync(cts.Token);
        Console.WriteLine("Server zastaven.");
    }
}