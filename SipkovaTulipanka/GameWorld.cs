// ============================================================
// GameWorld.cs
// ============================================================

namespace MudServer;

using System.Text;

// ------ POMOCNÁ TŘÍDA: normalizace textu bez diakritiky ------
// Převádí vstup hráče na jednoduchý tvar bez háčků/čárek.
// "Jdi Sever" → "jdi sever", "Sevěr" → "sever"
public static class TextHelper
{
    private static readonly (string From, string To)[] DiacriticMap =
    {
        ("á","a"),("č","c"),("ď","d"),("é","e"),("ě","e"),
        ("í","i"),("ň","n"),("ó","o"),("ř","r"),("š","s"),
        ("ť","t"),("ú","u"),("ů","u"),("ý","y"),("ž","z"),
        ("Á","A"),("Č","C"),("Ď","D"),("É","E"),("Ě","E"),
        ("Í","I"),("Ň","N"),("Ó","O"),("Ř","R"),("Š","S"),
        ("Ť","T"),("Ú","U"),("Ů","U"),("Ý","Y"),("Ž","Z"),
    };

    // Odstraní diakritiku a převede na malá písmena
    public static string Normalize(string s)
    {
        foreach (var (from, to) in DiacriticMap)
            s = s.Replace(from, to);
        return s.ToLower();
    }

    // Porovná dva řetězce ignorując diakritiku a velikost
    public static bool Match(string a, string b)
        => Normalize(a).Contains(Normalize(b));
}

// ------ PŘEDMĚT -----------------------------------------------
public class Item
{
    public string Name { get; }
    public string Description { get; }

    public Item(string name, string description)
    {
        Name = name;
        Description = description;
    }
}

// ------ SHOP ITEM – zboží v obchodě --------------------------
// Každé zboží v shopu má cenu a při koupi se přidá do inventáře.
public class ShopItem
{
    public string Name { get; }
    public string Description { get; }
    public int Price { get; }

    public ShopItem(string name, string description, int price)
    {
        Name = name;
        Description = description;
        Price = price;
    }
}

// ------ OTÁZKA PRO NPC KVÍZ ----------------------------------
public class Question
{
    public string Text { get; }
    public string Answer { get; }
    public int Reward { get; }
    // Volitelná odměna předmětem (null = jen zlaté)
    public Item? ItemReward { get; }

    public Question(string text, string answer, int reward, Item? itemReward = null)
    {
        Text = text;
        Answer = TextHelper.Normalize(answer);
        Reward = reward;
        ItemReward = itemReward;
    }

    public bool Check(string playerAnswer)
        => TextHelper.Normalize(playerAnswer).Trim() == Answer;
}

// ------ NPC ---------------------------------------------------
public class Npc
{
    public string Name { get; }
    private readonly string _greeting;

    // Pokud má NPC otázky, může být "kvízový"
    public List<Question> Questions { get; } = new();

    public Npc(string name, string greeting)
    {
        Name = name;
        _greeting = greeting;
    }

    public string Speak() => $"{Name} rika: \"{_greeting}\"";
    public bool HasQuiz => Questions.Count > 0;
}

// ------ MÍSTNOST ----------------------------------------------
public class Room
{
    public string Name { get; }
    public string Description { get; }
    public Dictionary<string, Room> Exits { get; } = new();
    public List<Item> Items { get; } = new();
    public List<Npc> Npcs { get; } = new();

    // Zda je místnost zamčená a kolik klíčů je potřeba
    public int RequiredKeys { get; set; } = 0;

    public Room(string name, string description)
    {
        Name = name;
        Description = description;
    }

    public void AddExit(string direction, Room target)
        => Exits[direction] = target;

    public string Describe(IEnumerable<string> otherPlayerNames)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"\n+== {Name.ToUpper()} ==+");
        sb.AppendLine(Description);

        sb.AppendLine("\n[ Vychody ]");
        if (Exits.Count == 0)
            sb.AppendLine("  Zadne vychody.");
        else
            foreach (var exit in Exits)
                sb.AppendLine($"  {exit.Key,8} -> {exit.Value.Name}");

        sb.AppendLine("\n[ Predmety ]");
        if (Items.Count == 0)
            sb.AppendLine("  Nic tu nelezi.");
        else
            foreach (var item in Items)
                sb.AppendLine($"  * {item.Name}");

        sb.AppendLine("\n[ Postavy ]");
        var allChars = new List<string>();
        allChars.AddRange(Npcs.Select(n => n.Name + (n.HasQuiz ? " (kviz)" : "") + " (NPC)"));
        allChars.AddRange(otherPlayerNames.Select(n => n + " (hrac)"));

        if (allChars.Count == 0)
            sb.AppendLine("  Nikdo tu neni.");
        else
            foreach (var c in allChars)
                sb.AppendLine($"  * {c}");

        return sb.ToString();
    }
}

// ------ SHOP --------------------------------------------------
// Jeden globální obchod – dostupný příkazem "shop" odkudkoli,
// nebo fyzicky jen ve Vesnici (viz Player.cs).
public class Shop
{
    public string Name { get; } = "Obchod u Vesnicana";
    public List<ShopItem> Items { get; } = new();

    public Shop()
    {
        Items.Add(new ShopItem("lektvar zdravi",
            "Obnovi tvoje zdravi a dobraci náladu.", 5));
        Items.Add(new ShopItem("pochoden",
            "Svetlo do tmy. Hori dlouho.", 3));
        Items.Add(new ShopItem("chléb",
            "Cerstvy chléb z peceni. Syta vec.", 2));
        Items.Add(new ShopItem("klic bronzovy",
            "Jeden z tri klicu potrebnych pro vstup do hradu. Dalsi dva ziskas od NPC!", 10));
    }

    public string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine("\n+== OBCHOD ==+");
        sb.AppendLine("Zbozi na prodej (prikaz: kup <nazev>):");
        foreach (var item in Items)
            sb.AppendLine($"  * {item.Name,-20} – {item.Price} zlat. | {item.Description}");
        return sb.ToString();
    }
}

// ------ HERNÍ SVĚT --------------------------------------------
public class GameWorld
{
    private readonly Dictionary<string, Room> _rooms = new();
    private readonly Dictionary<string, Player> _players = new();
    private readonly object _playersLock = new();

    public Room StartRoom { get; private set; }
    public Shop Shop { get; } = new Shop();

    // Klice potrebne pro vstup do Hradu
    public static readonly string[] HradKeys =
        { "klic bronzovy", "klic zelezny", "klic stribrny" };

    public GameWorld()
    {
        BuildWorld();
        StartRoom = _rooms["farma"];
    }

    public void AddPlayer(Player player)
    {
        lock (_playersLock) _players[player.Name] = player;
    }

    public void RemovePlayer(string name)
    {
        lock (_playersLock) _players.Remove(name);
    }

    public List<string> GetPlayersInRoom(Room room, string excludeName)
    {
        lock (_playersLock)
            return _players.Values
                .Where(p => p.CurrentRoom == room && p.Name != excludeName)
                .Select(p => p.Name)
                .ToList();
    }

    private void BuildWorld()
    {
        // ---- Místnosti ----
        var hrad = new Room(
            "Hrad",
            "Mohutne kamenné hradby se tyci do nebe. Ve velkém sale\n" +
            "hori krb a na trone sedi Kral. Princezna se prochazi\n" +
            "po nadvori. POZOR: vstup vyzaduje 3 klice!");

        // Hrad je zamceny – potreba 3 klice
        hrad.RequiredKeys = 3;

        var les = new Room(
            "Les",
            "Husty jehicnatý les. Vune pryskyrice a mechem pokryté\n" +
            "kameny. Mezi stromy se mihne Myslivec s kusi na rameni.");

        var vesnice = new Room(
            "Vesnice",
            "Malebna vesnice s doskovych strechami. Slepice pobihaji\n" +
            "po navsi. Vesnicane opravuje plot. Tady je OBCHOD!");

        var jezero = new Room(
            "U jezera",
            "Klidné modré jezero s pruzracnou vodou. Na brehu sedi\n" +
            "Zaba a pozoruje kruhy na hladine.");

        var farma = new Room(
            "Farma",
            "Cervena stodola uprostred zlatych poli. Farmar opravuje\n" +
            "plot a hvizda si. Pes line lezi ve stinu stodoly.");

        var reky = new Room(
            "U reky",
            "Klidna reka se tu mirne vine krajinou. Ryby se trepyti\n" +
            "v ciste vode.");

        var hory = new Room(
            "Hory",
            "Skalnaté vrcholky pokryté vecnym snehem. Ostry vitr\n" +
            "reze do obliceje. Horolezec si kontroluje lano.");

        var pole = new Room(
            "Pole",
            "Rozlehle zelené pole se vlni ve vetru jako more.\n" +
            "Mala Myska pobíha mezi stebly travy.");

        var lesJih = new Room(
            "Les na jihu",
            "Svetlejsi les nez na severu. Slunecni paprsky pronikaji\n" +
            "korunami stromu. Srna se klidne pase.");

        // ---- Propojení ----
        hrad.AddExit("vychod", les); les.AddExit("zapad", hrad);
        les.AddExit("vychod", vesnice); vesnice.AddExit("zapad", les);
        jezero.AddExit("vychod", farma); farma.AddExit("zapad", jezero);
        farma.AddExit("vychod", reky); reky.AddExit("zapad", farma);
        hory.AddExit("vychod", pole); pole.AddExit("zapad", hory);
        pole.AddExit("vychod", lesJih); lesJih.AddExit("zapad", pole);
        hrad.AddExit("jih", jezero); jezero.AddExit("sever", hrad);
        jezero.AddExit("jih", hory); hory.AddExit("sever", jezero);
        les.AddExit("jih", farma); farma.AddExit("sever", les);
        farma.AddExit("jih", pole); pole.AddExit("sever", farma);
        vesnice.AddExit("jih", reky); reky.AddExit("sever", vesnice);
        reky.AddExit("jih", lesJih); lesJih.AddExit("sever", reky);

        // =========================================================
        // NPC
        // =========================================================

        // --- Princezna – kvíz z matematiky ---
        var princezna = new Npc("Princezna",
            "Ach, konecne nekdo novy! Zvladni muj kviz a ziskas\n" +
            "  zlate jako odmenu! Prikaz: kviz Princezna");
        princezna.Questions.Add(new Question("Kolik je 7 x 8?", "56", 5));
        princezna.Questions.Add(new Question("Kolik je 15 + 27?", "42", 5));
        princezna.Questions.Add(new Question("Kolik je 100 - 63?", "37", 5));
        hrad.Npcs.Add(princezna);

        // --- Král – obecné věno ---
        var kral = new Npc("Kral",
            "Vitej, pocestny! Zodpovez moje otazky a ziskas\n" +
            "  zlate jako odmenu! Prikaz: kviz Kral");
        kral.Questions.Add(new Question("Jak se jmenuje hlavni mesto Ceske republiky?", "Praha", 8));
        kral.Questions.Add(new Question("Kolik planet je v soluarni soustave?", "8", 6));
        kral.Questions.Add(new Question("Ktery je nejvetsi ocean na Zemi?", "Tichy", 7));
        hrad.Npcs.Add(kral);

        // --- Myslivec – kvíz, odměna železný klíč ---
        var myslivec = new Npc("Myslivec",
            "Tise! Plasic zver... Ale jestli zodpovís moje otazky\n" +
            "  o prirode, dam ti zelezny klic! Prikaz: kviz Myslivec");
        myslivec.Questions.Add(new Question("Kolik noh ma jelen?", "4", 3));
        myslivec.Questions.Add(new Question("Jak se jmenuje mlada liška?", "lisce", 4));
        myslivec.Questions.Add(new Question("Ktery ptak umi napodobit hlas cloveka?", "papousek", 5,
            new Item("klic zelezny", "Zelezny klic ziskany od Myslivce. Jeden z tri klicu do hradu.")));
        les.Npcs.Add(myslivec);

        // --- Vesničan – kvíz ---
        var vesnicanNpc = new Npc("Vesnicanee",
            "Nazdar! Urcite vyhras v mem kvizu! Prikaz: kviz Vesnicanee");
        vesnicanNpc.Questions.Add(new Question("Kolik dni ma rok?", "365", 4));
        vesnicanNpc.Questions.Add(new Question("Kolik je 12 x 12?", "144", 5));
        vesnicanNpc.Questions.Add(new Question("Jak se jmenuje nase planeta?", "Zeme", 3));
        vesnice.Npcs.Add(vesnicanNpc);

        // --- Žába – kvíz z přírody ---
        var zaba = new Npc("Zaba",
            "*kvak* Vis toho vic nez ja? Zkus kviz! Prikaz: kviz Zaba");
        zaba.Questions.Add(new Question("Kolik noh ma pavouk?", "8", 4));
        zaba.Questions.Add(new Question("Jak se jmenuje nejvetsi zivocich na Zemi?", "velryba", 6));
        zaba.Questions.Add(new Question("Kolik zaber ma zlata rybka? (cislo)", "0", 5));
        jezero.Npcs.Add(zaba);

        // --- Farmář – bez kvízu ---
        farma.Npcs.Add(new Npc("Farmar",
            "Hej, pomuzes mi? Potrebuji odvézt seno do stodoly.\n" +
            "  Za odmenu ti dam cerstve jablko ze zahrady!"));

        farma.Npcs.Add(new Npc("Pes",
            "*vrti ocasem* Haf! Haf haf! *privilave olizuje ruku*"));

        // --- Horolezec – kvíz o zeměpisu, odměna stříbrný klíč ---
        var horolezec = new Npc("Horolezec",
            "Tohle jsou nejkrasnejsi hory! Zvladni muj kviz\n" +
            "  a ziskas stribrny klic do hradu! Prikaz: kviz Horolezec");
        horolezec.Questions.Add(new Question("Jak se jmenuje nejvyssi hora sveta?", "Everest", 8));
        horolezec.Questions.Add(new Question("Ve které zemi jsou Alpy?", "Svycarsko", 6));
        horolezec.Questions.Add(new Question("Kolik metros ma 1 kilometer?", "1000", 4,
            new Item("klic stribrny", "Stribrny klic ziskany od Horolezce. Jeden z tri klicu do hradu.")));
        hory.Npcs.Add(horolezec);

        // --- Myška – bez kvízu ---
        pole.Npcs.Add(new Npc("Myska",
            "*pip pip* Davej pozor kde slapes! Tady mam schovana\n" +
            "  zrnka na zimu! *pip*"));

        // --- Srna – bez kvízu ---
        lesJih.Npcs.Add(new Npc("Srna",
            "*zvedne hlavu a pozoruje te klidnyma ocima*\n" +
            "  ...ticho... *vrati se ke spasani travy*"));

        // =========================================================
        // PŘEDMĚTY
        // =========================================================

        // Zlaty klíč je ve hradu (uvnitr – neni potreba k vstupu)
        hrad.Items.Add(new Item("zlaty klic",
            "Lesklý zlatý klíč s královskou korunou."));

        farma.Items.Add(new Item("jablko",
            "Cervené, stavinaté jablko rovnou ze zahrady. Mnam!"));

        hory.Items.Add(new Item("lano",
            "Pevné horolezecké lano."));

        les.Items.Add(new Item("lesni kviti",
            "Krasna kytice lesnich kvetin. Princezna by se urcite potesila!"));

        pole.Items.Add(new Item("zrnko psenice",
            "Velke zlaté zrnko psenice."));

        lesJih.Items.Add(new Item("vetvicka",
            "Rovná, pevná vetvicka ze starého dubu."));

        reky.Items.Add(new Item("oblazek",
            "Hladky oblazek vyhlazeny rekou."));

        // Ulozime
        _rooms["hrad"] = hrad;
        _rooms["les"] = les;
        _rooms["vesnice"] = vesnice;
        _rooms["jezero"] = jezero;
        _rooms["farma"] = farma;
        _rooms["reky"] = reky;
        _rooms["hory"] = hory;
        _rooms["pole"] = pole;
        _rooms["lesjih"] = lesJih;
    }
}