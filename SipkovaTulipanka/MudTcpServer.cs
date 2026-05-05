// ============================================================
// MudTcpServer.cs – TCP server, který přijímá hráče
// ============================================================
// Toto je "vrátný" celé aplikace. Jeho jediná práce je:
//   1. Naslouchat na portu
//   2. Když přijde nové spojení → vytvořit Player objekt
//   3. Spustit Player.HandleAsync() ASYNCHRONNĚ (nezablokovatelně)
//   4. Přijímat další spojení (vrátit se na bod 1)
//
// Klíčový princip: _ = player.HandleAsync() spustí úlohu
//   a ihned se vrátí – server nemusí čekat na jejíma dokončení.
//   Díky tomu zvládne obsloužit stovky hráčů současně.
// ============================================================

namespace MudServer;

using System.Net;
using System.Net.Sockets;
using System.Numerics;

public class MudTcpServer
{
    private readonly int _port;
    private readonly GameWorld _world;

    public MudTcpServer(int port, GameWorld world)
    {
        _port = port;
        _world = world;
    }

    // =========================================================
    // StartAsync – hlavní smyčka serveru
    // =========================================================
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // TcpListener naslouchá na zadaném portu
        // IPAddress.Any = přijímáme spojení ze všech síťových rozhraní
        //   (localhost i z jiných počítačů v síti)
        var listener = new TcpListener(IPAddress.Any, _port);
        listener.Start();

        Console.WriteLine($"[Server] Naslouchám na portu {_port}. Čekám na hráče...");

        // AcceptTcpClientAsync skončí výjimkou když zrušíme token (Ctrl+C)
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Čekáme na dalšího klienta – ASYNCHRONNĚ
                // Tato řádka "zablokuje" jen tento await, ostatní tasky běží dál
                TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);

                // Vypíšeme info o novém připojení
                string clientIp = client.Client.RemoteEndPoint?.ToString() ?? "neznámý";
                Console.WriteLine($"[Server] Nové spojení od: {clientIp}");

                // Vytvoříme objekt hráče
                Player player = new Player(client, _world);

                // Spustíme obsluhu hráče jako SAMOSTATNÝ TASK
                // Podtržítko "_" = záměrně ignorujeme vrácený Task
                //   (nepotřebujeme na něj čekat)
                // ContinueWith loguje případné neodchycené chyby
                _ = player.HandleAsync().ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        Console.WriteLine($"[Chyba] Neočekávaná chyba hráče: {t.Exception?.InnerException?.Message}");
                }, TaskScheduler.Default);

                // A hned přijímáme dalšího klienta! (bez čekání na předchozího)
            }
        }
        catch (OperationCanceledException)
        {
            // Normální ukončení přes CancellationToken – není to chyba
        }
        finally
        {
            listener.Stop();
        }
    }
}