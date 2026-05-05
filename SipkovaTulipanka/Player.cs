// ============================================================
// Player.cs
// ============================================================

namespace MudServer;

using System.Net.Sockets;
using System.Text;

public class Player
{
    public string Name { get; private set; } = "Neznamy";
    public Room CurrentRoom { get; private set; }

    private readonly List<Item> _inventory = new();
    private const int MaxInventorySize = 10;

    // Penize hrace
    private int _gold = 0;

    // Stav kvízu: pokud hrac prave odpovida na otazku NPC
    private Npc? _activeQuizNpc = null;
    private int _activeQuestionIndex = 0;
    private int _quizCorrect = 0;

    private readonly NetworkStream _stream;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly GameWorld _world;

    public Player(TcpClient client, GameWorld world)
    {
        _world = world;
        CurrentRoom = world.StartRoom;
        _stream = client.GetStream();
        _reader = new StreamReader(_stream, Encoding.UTF8, leaveOpen: true);
        _writer = new StreamWriter(_stream, Encoding.UTF8, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\r\n"
        };
    }

    public async Task HandleAsync()
    {
        try
        {
            await SendAsync("Vitej v MUD svete!\r\nZadej sve jmeno: ");
            string? nameInput = await _reader.ReadLineAsync();
            Name = string.IsNullOrWhiteSpace(nameInput) ? "Dobrodruh" : nameInput.Trim();

            _world.AddPlayer(this);

            await SendLineAsync($"\nVitej, {Name}! Mas {_gold} zlatych. Napis 'pomoc' pro prikazy.");
            await ShowRoomAsync();

            while (true)
            {
                await _writer.WriteAsync("\n> ");
                string? line = await _reader.ReadLineAsync();
                if (line == null) break;

                string input = line.Trim();
                if (string.IsNullOrEmpty(input)) continue;

                // Pokud probiha kviz, vstup je odpoved na otazku
                if (_activeQuizNpc != null)
                {
                    await HandleQuizAnswerAsync(input);
                    continue;
                }

                bool quit = await ProcessCommandAsync(input);
                if (quit) break;
            }
        }
        catch (IOException)
        {
            Console.WriteLine($"[{Name}] Nahlé prerušení spojení.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{Name}] Chyba: {ex.Message}");
        }
        finally
        {
            _world.RemovePlayer(Name);
            _reader.Dispose();
            _writer.Dispose();
            _stream.Dispose();
            Console.WriteLine($"[Server] Hrac '{Name}' se odpojil.");
        }
    }

    // =========================================================
    // ZPRACOVÁNÍ PŘÍKAZŮ
    // =========================================================
    private async Task<bool> ProcessCommandAsync(string input)
    {
        // Normalizujeme vstup – "Jdi Sever" == "jdi sever" == "jdi sěver"
        string norm = TextHelper.Normalize(input);
        string[] parts = norm.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string verb = parts[0];
        string arg = parts.Length > 1 ? parts[1].Trim() : "";

        switch (verb)
        {
            case "jdi":
            case "go":
                await MoveAsync(arg); break;

            case "prozkoumej":
            case "look":
            case "p":
                await ShowRoomAsync(); break;

            case "vezmi":
            case "take":
                await TakeItemAsync(arg); break;

            case "poloz":
            case "drop":
                await DropItemAsync(arg); break;

            case "inventar":
            case "inv":
            case "i":
                await ShowInventoryAsync(); break;

            case "penize":
            case "zlato":
            case "gold":
                await SendLineAsync($"Mas {_gold} zlatych."); break;

            case "mluv":
            case "talk":
                await TalkToNpcAsync(arg); break;

            // Kvíz – "kviz <jmeno npc>"
            case "kviz":
            case "quiz":
                await StartQuizAsync(arg); break;

            // Shop – "shop" nebo "obchod" – dostupný jen ve Vesnici
            case "shop":
            case "obchod":
                await ShowShopAsync(); break;

            // Koupit – "kup <nazev zbozi>"
            case "kup":
            case "buy":
                await BuyItemAsync(arg); break;

            case "pomoc":
            case "help":
            case "?":
                await ShowHelpAsync(); break;

            case "konec":
            case "quit":
            case "exit":
                await SendLineAsync("Nashledanou, dobrodrhu!");
                return true;

            default:
                await SendLineAsync($"Prikaz '{verb}' neznam. Napis 'pomoc'."); break;
        }
        return false;
    }

    // =========================================================
    // POHYB – kontrola klíčů pro Hrad
    // =========================================================
    private async Task MoveAsync(string direction)
    {
        if (string.IsNullOrEmpty(direction))
        {
            await SendLineAsync("Kam chces jit? Priklad: jdi sever");
            return;
        }

        if (!CurrentRoom.Exits.TryGetValue(direction, out Room? target))
        {
            await SendLineAsync($"Smer '{direction}' odsud nevede. Dostupné: " +
                string.Join(", ", CurrentRoom.Exits.Keys));
            return;
        }

        // Kontrola zamčené místnosti (Hrad – 3 klíče)
        if (target.RequiredKeys > 0)
        {
            var requiredKeys = GameWorld.HradKeys;
            var missing = requiredKeys
                .Where(k => !_inventory.Any(i => TextHelper.Normalize(i.Name) == k))
                .ToList();

            if (missing.Count > 0)
            {
                await SendLineAsync($"Vstup do '{target.Name}' je zamlceny!");
                await SendLineAsync($"Chybi ti tyto klice: {string.Join(", ", missing)}");
                await SendLineAsync("Klice muzes koupit v obchodu ve Vesnici.");
                return;
            }
        }

        CurrentRoom = target;
        await SendLineAsync($"Jdes {direction}...");
        await ShowRoomAsync();
    }

    // =========================================================
    // KVÍZ
    // =========================================================
    private async Task StartQuizAsync(string npcName)
    {
        if (string.IsNullOrEmpty(npcName))
        {
            await SendLineAsync("S kym chces delat kviz? Priklad: kviz Princezna");
            return;
        }

        Npc? npc = CurrentRoom.Npcs
            .FirstOrDefault(n => TextHelper.Match(n.Name, npcName));

        if (npc == null)
        {
            await SendLineAsync($"'{npcName}' tu neni.");
            return;
        }

        if (!npc.HasQuiz)
        {
            await SendLineAsync($"{npc.Name} nema zadny kviz. Zkus 'mluv {npcName}'.");
            return;
        }

        // Spustíme kvíz
        _activeQuizNpc = npc;
        _activeQuestionIndex = 0;
        _quizCorrect = 0;

        await SendLineAsync($"\n{npc.Name} spousti kviz! Odpovez na {npc.Questions.Count} otazky.");
        await SendLineAsync("(Napis 'preskoc' pro preskoceni otazky)\n");
        await AskCurrentQuestionAsync();
    }

    private async Task AskCurrentQuestionAsync()
    {
        if (_activeQuizNpc == null) return;
        var q = _activeQuizNpc.Questions[_activeQuestionIndex];
        await SendLineAsync($"Otazka {_activeQuestionIndex + 1}/{_activeQuizNpc.Questions.Count}:");
        await SendLineAsync($"  {q.Text}");
    }

    private async Task HandleQuizAnswerAsync(string answer)
    {
        if (_activeQuizNpc == null) return;

        string norm = TextHelper.Normalize(answer);

        if (norm == "preskoc")
        {
            await SendLineAsync("Otazka preskocena.");
        }
        else
        {
            var q = _activeQuizNpc.Questions[_activeQuestionIndex];
            if (q.Check(answer))
            {
                _gold += q.Reward;
                _quizCorrect++;
                await SendLineAsync($"Spravne! +{q.Reward} zlatych. (Celkem: {_gold})");
                // Odměna předmětem (klíč od Princezny / Krále)
                if (q.ItemReward != null)
                {
                    if (_inventory.Count < MaxInventorySize)
                    {
                        _inventory.Add(new Item(q.ItemReward.Name, q.ItemReward.Description));
                        await SendLineAsync($"  *** Ziskal/a jsi: {q.ItemReward.Name}! ***");
                    }
                    else
                    {
                        await SendLineAsync($"  Inventar je plny! {q.ItemReward.Name} si nevzal/a.");
                    }
                }
            }
            else
            {
                await SendLineAsync($"Spatne. Spravna odpoved byla: {q.Answer}");
            }
        }

        _activeQuestionIndex++;

        if (_activeQuestionIndex >= _activeQuizNpc.Questions.Count)
        {
            // Kvíz skončil
            await SendLineAsync($"\nKviz konci! Spravne: {_quizCorrect}/{_activeQuizNpc.Questions.Count}");
            await SendLineAsync($"Celkovy stav: {_gold} zlatych.");
            _activeQuizNpc = null;
        }
        else
        {
            await AskCurrentQuestionAsync();
        }
    }

    // =========================================================
    // SHOP
    // =========================================================
    private async Task ShowShopAsync()
    {
        // Shop je dostupný jen ve Vesnici
        if (TextHelper.Normalize(CurrentRoom.Name) != "vesnice")
        {
            await SendLineAsync("Obchod je jen ve Vesnici! Jdi tam nejdrive.");
            return;
        }
        await SendLineAsync(_world.Shop.Describe());
        await SendLineAsync($"Tvoje zlato: {_gold}");
    }

    private async Task BuyItemAsync(string itemName)
    {
        if (TextHelper.Normalize(CurrentRoom.Name) != "vesnice")
        {
            await SendLineAsync("Nakupovat muzes jen ve Vesnici!");
            return;
        }

        if (string.IsNullOrEmpty(itemName))
        {
            await SendLineAsync("Co chces koupit? Priklad: kup pochoden");
            return;
        }

        ShopItem? shopItem = _world.Shop.Items
            .FirstOrDefault(s => TextHelper.Match(s.Name, itemName));

        if (shopItem == null)
        {
            await SendLineAsync($"'{itemName}' v obchodu neni. Napis 'shop' pro seznam.");
            return;
        }

        if (_inventory.Count >= MaxInventorySize)
        {
            await SendLineAsync("Inventar je plny! Poloz nejaky predmet.");
            return;
        }

        if (_gold < shopItem.Price)
        {
            await SendLineAsync($"Nemas dost zlata! Treba {shopItem.Price}, mas {_gold}.");
            return;
        }

        _gold -= shopItem.Price;
        _inventory.Add(new Item(shopItem.Name, shopItem.Description));
        await SendLineAsync($"Koupil/a jsi: {shopItem.Name} za {shopItem.Price} zlatych.");
        await SendLineAsync($"Zbyvajici zlato: {_gold}");
    }

    // =========================================================
    // OSTATNÍ AKCE
    // =========================================================
    private async Task ShowRoomAsync()
    {
        var others = _world.GetPlayersInRoom(CurrentRoom, Name);
        await SendLineAsync(CurrentRoom.Describe(others));
    }

    private async Task TalkToNpcAsync(string npcName)
    {
        if (string.IsNullOrEmpty(npcName))
        {
            await SendLineAsync("S kym chces mluvit? Priklad: mluv Farmar");
            return;
        }

        Npc? npc = CurrentRoom.Npcs
            .FirstOrDefault(n => TextHelper.Match(n.Name, npcName));

        if (npc == null)
        {
            await SendLineAsync($"'{npcName}' tu neni.");
            return;
        }

        await SendLineAsync(npc.Speak());
        if (npc.HasQuiz)
            await SendLineAsync($"  (Tip: prikaz 'kviz {npc.Name}' pro kviz s odmenou!)");
    }

    private async Task TakeItemAsync(string itemName)
    {
        if (string.IsNullOrEmpty(itemName))
        {
            await SendLineAsync("Co chces vzit? Priklad: vezmi jablko");
            return;
        }
        if (_inventory.Count >= MaxInventorySize)
        {
            await SendLineAsync($"Inventar je plny! ({MaxInventorySize}/{MaxInventorySize})");
            return;
        }

        Item? found = CurrentRoom.Items
            .FirstOrDefault(i => TextHelper.Match(i.Name, itemName));

        if (found == null)
        {
            await SendLineAsync($"Predmet '{itemName}' tu nevidim.");
            return;
        }

        CurrentRoom.Items.Remove(found);
        _inventory.Add(found);
        await SendLineAsync($"Vzal/a jsi: {found.Name}");
    }

    private async Task DropItemAsync(string itemName)
    {
        if (string.IsNullOrEmpty(itemName))
        {
            await SendLineAsync("Co chces polozit? Priklad: poloz lano");
            return;
        }

        Item? found = _inventory
            .FirstOrDefault(i => TextHelper.Match(i.Name, itemName));

        if (found == null)
        {
            await SendLineAsync($"'{itemName}' nemas u sebe.");
            return;
        }

        _inventory.Remove(found);
        CurrentRoom.Items.Add(found);
        await SendLineAsync($"Polozil/a jsi: {found.Name}");
    }

    private async Task ShowInventoryAsync()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"\n[ Inventar ] ({_inventory.Count}/{MaxInventorySize}) | Zlato: {_gold}");

        if (_inventory.Count == 0)
            sb.AppendLine("  Nic u sebe nemas.");
        else
            foreach (var item in _inventory)
                sb.AppendLine($"  * {item.Name} – {item.Description}");

        await SendLineAsync(sb.ToString());
    }

    private async Task ShowHelpAsync()
    {
        await SendLineAsync("""

            +=======================================+
            |          SEZNAM PRIKAZU               |
            +=======================================+
            | jdi <smer>      Pohyb (sever,jih...)  |
            | prozkoumej      Prohlédni mistnost    |
            | vezmi <vec>     Vezmi predmet         |
            | poloz <vec>     Poloz predmet         |
            | inventar        Zobraz inventar       |
            | penize          Kolik mas zlata       |
            | mluv <jmeno>    Mluv s postavou       |
            | kviz <jmeno>    Kviz s NPC za zlato   |
            | shop            Zobraz obchod         |
            | kup <vec>       Kup predmet v obchode |
            | pomoc           Tato napoveda         |
            | konec           Odpojit se            |
            +=======================================+
            | HRAD: potrebujes 3 klice z obchodu!   |
            | KVIZ: odpovedej spravne = zlato       |
            +=======================================+
            """);
    }

    private async Task SendLineAsync(string text)
        => await _writer.WriteLineAsync(text);

    private async Task SendAsync(string text)
        => await _writer.WriteAsync(text);
}