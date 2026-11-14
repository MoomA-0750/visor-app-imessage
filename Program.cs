// visor のアプリ「iMessage」: Mac で動く imsg-web の API につないで、会話を読み、返事を打って送る。
// 左の板に会話の一覧、窓の中に選んだ会話、下の板に入力欄と送るボタン、上の板に会話の名前。
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using VisorImessage;
using VisorSdk;

var lang = Environment.GetEnvironmentVariable("VISOR_LANG") == "en" ? "en" : "ja";
string T(string ja, string en) => lang == "en" ? en : ja;

var app = new VisorApp();
var gate = new object();
var config = Config.Load();
ImsgClient? client = config.Ready ? NewClient(config) : null;

// 会話の一覧と、選んでいる会話（id はサーバーの epoch の中だけのもの。epoch が変われば名前で選び直す）
var chats = new List<JsonObject>();
string? epoch = null;
string? selectedId = null;
string? selectedKey = null;
var messages = new List<JsonObject>();
string? historyFor = null;
string? historyAt = null;
bool loadingHistory = false;

// 送れるか（live・dry-run・off。読むだけのトークンなら off）
var sendMode = "unknown";
string? connectionError = null;
string? notice = null;
var noticeError = false;
var noticeUntil = DateTimeOffset.MinValue;
var compose = "";
var sending = false;

// 設定の画面（つなぐ先が無ければ、はじめから出す）
var settingsOpen = !config.Ready;
string setUrl = config.Url, setToken = "", setCert = config.Cert ?? "";
string? settingsMessage = null;

// 絵（"face:id"・"att:id" → base64 の JPEG。null は読めなかった）。epoch が変われば捨てる
var pictures = new Dictionary<string, string?>();
var wanted = new Queue<string>();
var fetching = false;

// 新着の知らせ: 会話ごとの最後の時刻（初めの読み込みでは知らせない）
var lastSeen = new Dictionary<string, string>();
var firstPoll = true;
var wake = new SemaphoreSlim(0);

string? sentMain = null, sentLeft = null, sentBottom = null, sentTop = null;

app.Event += OnEvent;
app.Input += (id, text, submit) =>
{
    if (id == "compose")
    {
        lock (gate) compose = submit ? "" : text;
        if (submit) _ = SendText(text);
        return;
    }
    lock (gate)
    {
        if (id == "url") setUrl = text;
        else if (id == "token") setToken = text;
        else if (id == "cert") setCert = text;
    }
    if (submit) _ = Connect();
};
// 窓が見えたら、すぐ読み直す（見えていない間は 1 分おき、見えている間は 15 秒おき）
app.Visible += on => { if (on) wake.Release(); };

app.Tool("list_chats", "iMessage の会話の一覧（新しい順。名前・最後のやりとりの時刻・未読の数・最後の文）を見る。", async _ =>
{
    await Refresh();
    lock (gate)
    {
        if (client == null) return T("imsg-web につなぐ設定がまだありません。iMessage の窓の設定から入れてください", "Not set up yet. Enter the imsg-web settings in the iMessage window");
        if (connectionError != null) return connectionError;
        return string.Join("\n", chats.Take(30).Select(c =>
            $"- {Str(c, "name")}（{Str(c, "service")}）・{When(Str(c, "lastMessageAt"))}" +
            (c["unreadCount"]?.GetValue<int>() is > 0 and var n ? T($"・未読 {n}", $" · {n} unread") : "") +
            (c["preview"] is JsonObject p ? $"・{(p["fromMe"]?.GetValue<bool>() == true ? T("自分", "Me") + ": " : "")}{Str(p, "text")}" : "")));
    }
});
app.Tool("read_chat", "iMessage の会話の最近のやりとりを読む。name は会話の名前（list_chats に出る名前。一部でもよい）。", async a =>
{
    var chat = await FindChat(Str(a, "name"));
    if (chat == null) return T("その名前の会話が見つかりません", "No conversation with that name");
    var history = await FetchHistory(Str(chat, "id")!, 50);
    var count = Math.Clamp(a["count"]?.GetValue<int>() ?? 20, 1, 50);
    return $"{Str(chat, "name")}:\n" + string.Join("\n", history.TakeLast(count).Select(m =>
        $"[{When(Str(m, "createdAt"))}] {(m["isFromMe"]?.GetValue<bool>() == true ? T("自分", "Me") : Str(m, "sender") ?? Str(chat, "name"))}: {Body(m)}"));
}, ("name", "string"), ("count", "number"));
app.Tool("send_message", "iMessage を送る（本人として送られ、取り消せない）。本人がはっきり頼んだときだけ使う。送る前に、相手の会話の名前と文を本人に見せて確かめる。name は会話の名前。", async a =>
{
    var chat = await FindChat(Str(a, "name"));
    if (chat == null) return T("その名前の会話が見つかりません", "No conversation with that name");
    var text = Str(a, "text") ?? "";
    if (text.Trim().Length == 0) return T("送る文がありません", "Nothing to send");
    return await Send(Str(chat, "id")!, text);
}, ("name", "string"), ("text", "string"));
app.Tool("open_chat", "iMessage の窓でその会話を開いて見せる。", async a =>
{
    var chat = await FindChat(Str(a, "name"));
    if (chat == null) return T("その名前の会話が見つかりません", "No conversation with that name");
    Select(chat);
    app.Open();
    return T($"{Str(chat, "name")} を開きました", $"Opened {Str(chat, "name")}");
}, ("name", "string"));
app.PublishTools();

Draw();
_ = Task.Run(Poll);
await app.Run();
return;

// --- つなぐ・読む ---

ImsgClient? NewClient(Config c)
{
    try
    {
        return new ImsgClient(c.Url, c.Token, c.Cert);
    }
    catch (Exception)
    {
        return null;
    }
}

async Task Poll()
{
    while (true)
    {
        try
        {
            await Refresh();
        }
        catch (Exception)
        {
            // Refresh の中で本人に見せる。ここでは回し続ける
        }
        await wake.WaitAsync(app.IsVisible ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(60));
    }
}

async Task Refresh()
{
    ImsgClient? c;
    lock (gate) c = client;
    if (c == null) return;
    try
    {
        if (sendMode == "unknown")
        {
            var caps = await c.GetJson("api/capabilities");
            var send = caps["features"]?["send"];
            lock (gate)
                sendMode = send?["state"]?.GetValue<string>() != "available" ? "off" : send["reasonCode"]?.GetValue<string>() == "SEND_DRY_RUN" ? "dry-run" : "live";
        }
        var snap = await c.GetJson("api/chats?limit=50");
        string? reload = null;
        var news = new List<(string Name, string Text)>();
        lock (gate)
        {
            connectionError = null;
            var newEpoch = Str(snap, "epoch");
            if (newEpoch != epoch)
            {
                // id が全部入れ替わった: 絵を捨て、選んでいた会話を名前で選び直す
                epoch = newEpoch;
                pictures.Clear();
                wanted.Clear();
                historyFor = null;
            }
            chats = (snap["chats"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            if (selectedKey != null)
            {
                var again = chats.FirstOrDefault(x => Key(x) == selectedKey);
                selectedId = again != null ? Str(again, "id") : null;
                if (again == null) selectedKey = null;
            }
            foreach (var chat in chats)
            {
                var key = Key(chat);
                var at = Str(chat, "lastMessageAt") ?? "";
                var seen = lastSeen.GetValueOrDefault(key);
                lastSeen[key] = at;
                if (firstPoll || seen == at || chat["preview"] is not JsonObject p || p["fromMe"]?.GetValue<bool>() == true) continue;
                news.Add((Str(chat, "name") ?? "iMessage", Str(p, "text") ?? T("新しいメッセージ", "New message")));
            }
            firstPoll = false;
            foreach (var chat in chats.Take(40))
                if (FaceId(chat) is { } f) Want("face:" + f);
            if (selectedId != null)
            {
                var sel = chats.First(x => Str(x, "id") == selectedId);
                if (historyFor != selectedId || historyAt != Str(sel, "lastMessageAt")) reload = selectedId;
            }
        }
        // 窓を出している間は visor が出さないので、いつ送ってもよい
        foreach (var (name, text) in news.Take(3)) app.Notify(name, text);
        if (reload != null) await LoadHistory(reload);
    }
    catch (Exception ex)
    {
        lock (gate)
        {
            connectionError = ImsgClient.Explain(ex, T);
            if (ImsgClient.Stale(ex)) epoch = null;
        }
    }
    Draw();
    FetchPictures();
}

async Task<List<JsonObject>> FetchHistory(string id, int limit)
{
    ImsgClient? c;
    lock (gate) c = client;
    if (c == null) return [];
    var snap = await c.GetJson($"api/chats/{Uri.EscapeDataString(id)}/messages?limit={limit}");
    return (snap["messages"] as JsonArray ?? []).OfType<JsonObject>().ToList();
}

async Task LoadHistory(string id)
{
    lock (gate) loadingHistory = true;
    try
    {
        var list = await FetchHistory(id, 50);
        lock (gate)
        {
            if (selectedId != id) return;
            messages = list;
            historyFor = id;
            historyAt = chats.FirstOrDefault(x => Str(x, "id") == id) is { } sel ? Str(sel, "lastMessageAt") : null;
            foreach (var m in list.TakeLast(30))
            {
                foreach (var a in m["attachments"] as JsonArray ?? [])
                    if (a?["kind"]?.GetValue<string>() == "image" && Str(a, "id") is { } aid) Want("att:" + aid);
                if (m["link"]?["image"] is JsonObject li && Str(li, "id") is { } lid) Want("att:" + lid);
                if (Str(m, "avatarId") is { } av) Want("face:" + av);
            }
        }
    }
    catch (Exception ex)
    {
        lock (gate)
        {
            if (ImsgClient.Stale(ex))
            {
                epoch = null;
                wake.Release();
            }
            else Say(ImsgClient.Explain(ex, T), true);
        }
    }
    finally
    {
        lock (gate) loadingHistory = false;
    }
    Draw();
    FetchPictures();
}

void Want(string key)
{
    if (!pictures.ContainsKey(key) && !wanted.Contains(key)) wanted.Enqueue(key);
}

/// <summary>欲しい絵を 1 つずつ取りにいき、何枚か取れるごとに描き直す。</summary>
void FetchPictures()
{
    lock (gate)
    {
        if (fetching || wanted.Count == 0) return;
        fetching = true;
    }
    _ = Task.Run(async () =>
    {
        var got = 0;
        while (true)
        {
            string key;
            ImsgClient? c;
            lock (gate)
            {
                if (!wanted.TryDequeue(out key!)) break;
                c = client;
            }
            if (c == null) break;
            string? data = null;
            try
            {
                var face = key.StartsWith("face:");
                var (bytes, _) = await c.GetBytes(face ? $"api/avatars/{Uri.EscapeDataString(key[5..])}" : $"api/attachments/{Uri.EscapeDataString(key[4..])}");
                data = Pictures.Shrink(bytes, face ? 128 : 720);
            }
            catch (Exception)
            {
                // 読めない絵は「写真」の札にする
            }
            lock (gate) pictures[key] = data;
            if (++got % 4 == 0) Draw();
        }
        lock (gate) fetching = false;
        Draw();
    });
}

async Task<JsonObject?> FindChat(string? name)
{
    if (string.IsNullOrWhiteSpace(name)) return null;
    await Refresh();
    lock (gate)
        return chats.FirstOrDefault(c => string.Equals(Str(c, "name"), name, StringComparison.OrdinalIgnoreCase))
            ?? chats.FirstOrDefault(c => Str(c, "name")?.Contains(name, StringComparison.OrdinalIgnoreCase) == true);
}

void Select(JsonObject chat)
{
    var id = Str(chat, "id");
    lock (gate)
    {
        if (id == selectedId) return;
        selectedId = id;
        selectedKey = Key(chat);
        messages = [];
        historyFor = null;
        settingsOpen = false;
    }
    Draw();
    if (id != null) _ = LoadHistory(id);
}

// --- 送る ---

async Task SendText(string text)
{
    string? id;
    lock (gate)
    {
        id = selectedId;
        if (sending || id == null || text.Trim().Length == 0) return;
    }
    var result = await Send(id, text);
    // 送れなかったら、打った文を入力欄に戻す
    if (result != "sent") app.SetInput("compose", text);
}

/// <summary>送って、本人に見せる文を返す（送れたら "sent"）。</summary>
async Task<string> Send(string chatId, string text)
{
    ImsgClient? c;
    lock (gate)
    {
        c = client;
        if (c == null) return T("imsg-web につなぐ設定がまだありません", "Not set up yet");
        if (sendMode == "off") return T("このトークンでは送れません（読むだけのトークンです）", "This token can't send (it's read-only)");
        sending = true;
    }
    Draw();
    var body = new JsonObject { ["chatId"] = chatId, ["text"] = text };
    string answer;
    try
    {
        // imsg-web は 2 KiB より大きい中身を受けない
        if (Encoding.UTF8.GetByteCount(body.ToJsonString()) > 2000)
            throw new InvalidOperationException(T("文が長すぎます（日本語でおよそ 600 文字まで）。分けて送ってください", "The message is too long. Split it up"));
        var res = await c.PostJson("api/send", body);
        answer = Str(res, "state") switch
        {
            "sent" => "sent",
            "dry_run" => T("試しのモードなので、送ったことにして何も送っていません（imsg-web が dry-run）", "Dry-run mode: nothing was sent"),
            "unknown" => T("届いたか分かりません。Messages で確かめてください（送り直すと二重になるかもしれません）", "It may or may not have gone. Check in Messages before sending again"),
            _ => T("送れませんでした。もう一度送れます", "Couldn't send. You can try again"),
        };
    }
    catch (InvalidOperationException ex)
    {
        answer = ex.Message;
    }
    catch (Exception ex)
    {
        answer = ImsgClient.Explain(ex, T);
    }
    lock (gate)
    {
        sending = false;
        if (answer != "sent") Say(answer, true);
        historyAt = null;
    }
    Draw();
    // 送ったものが会話に出るよう、少し待って読み直す
    _ = Task.Delay(1500).ContinueWith(_ => wake.Release());
    return answer == "sent" ? "sent" : answer;
}

// --- 設定 ---

async Task Connect()
{
    Config next;
    lock (gate)
    {
        next = new Config
        {
            Url = setUrl.Trim(),
            // トークンを打ち直していなければ、前のものを使う
            Token = setToken.Trim().Length > 0 ? setToken.Trim() : config.Token,
            Cert = setCert.Trim().Length > 0 ? setCert.Trim() : null,
        };
        settingsMessage = null;
    }
    string? problem = null;
    if (!next.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !IsLocalHttp(next.Url))
        problem = T("URL は https:// から始めてください", "The URL must start with https://");
    else if (next.Token.Length == 0) problem = T("トークンを入れてください", "Enter the token");
    else if (next.Cert != null && !next.Cert.Contains("BEGIN CERTIFICATE")) problem = T("証明書は cert.pem の中身を BEGIN から END まで貼ってください", "Paste cert.pem from BEGIN to END");
    if (problem == null)
    {
        var c = NewClient(next);
        if (c == null) problem = T("その URL か証明書が読めません", "Couldn't read that URL or certificate");
        else
        {
            try
            {
                var caps = await c.GetJson("api/capabilities");
                var send = caps["features"]?["send"];
                next.Save();
                lock (gate)
                {
                    client?.Dispose();
                    client = c;
                    config = next;
                    setToken = "";
                    sendMode = "unknown";
                    epoch = null;
                    firstPoll = true;
                    settingsOpen = false;
                    connectionError = null;
                    Say(send?["state"]?.GetValue<string>() == "available" ? T("つながりました。送れます", "Connected. You can send") : T("つながりました（読むだけ）", "Connected (read-only)"), false);
                }
                app.SetInput("token", "");
                wake.Release();
                Draw();
                return;
            }
            catch (Exception ex)
            {
                c.Dispose();
                problem = ImsgClient.Explain(ex, T);
            }
        }
    }
    lock (gate) settingsMessage = problem;
    Draw();
}

// 試しのサーバー（tools/mock_imsg.py）だけは http を許す（この PC の中だけ）
static bool IsLocalHttp(string url) =>
    Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == "http" && (u.Host == "127.0.0.1" || u.Host == "localhost");

// --- ボタン ---

void OnEvent(string id)
{
    if (id.StartsWith("chat:"))
    {
        JsonObject? chat;
        lock (gate) chat = chats.FirstOrDefault(c => Str(c, "id") == id[5..]);
        if (chat != null) Select(chat);
        return;
    }
    switch (id)
    {
        case "send":
            string text;
            lock (gate) text = compose;
            if (text.Trim().Length == 0) return;
            lock (gate) compose = "";
            app.SetInput("compose", "");
            _ = SendText(text);
            break;
        case "refresh":
            lock (gate) historyAt = null;
            wake.Release();
            break;
        case "settings":
            lock (gate)
            {
                settingsOpen = !settingsOpen || !config.Ready;
                setUrl = config.Url;
                setCert = config.Cert ?? "";
                settingsMessage = null;
            }
            Draw();
            break;
        case "connect":
            _ = Connect();
            break;
    }
}

void Say(string text, bool error)
{
    notice = text;
    noticeError = error;
    noticeUntil = DateTimeOffset.UtcNow.AddSeconds(error ? 10 : 4);
    _ = Task.Delay(TimeSpan.FromSeconds(error ? 10.2 : 4.2)).ContinueWith(_ => Draw());
}

// --- 描く ---

void Draw()
{
    string main, left, bottom, top, describe;
    bool sendMain, sendLeft, sendBottom, sendTop;
    lock (gate)
    {
        main = Main().ToJsonString();
        left = ChatList().ToJsonString();
        bottom = ComposeBar().ToJsonString();
        top = TopBar().ToJsonString();
        describe = Describe();
        sendMain = main != sentMain;
        sendLeft = left != sentLeft;
        sendBottom = bottom != sentBottom;
        sendTop = top != sentTop;
        sentMain = main;
        sentLeft = left;
        sentBottom = bottom;
        sentTop = top;
    }
    // 変わった板だけ送る（入力欄のある下の板を送り直さなければ、打っている欄はそのまま）
    if (sendMain || sendLeft || sendBottom || sendTop)
        app.Render(sendMain ? JsonNode.Parse(main) : null, sendLeft ? JsonNode.Parse(left) : null, sendBottom ? JsonNode.Parse(bottom) : null,
            null, sendTop ? JsonNode.Parse(top) : null);
    app.Describe(describe);
}

JsonObject TopBar()
{
    var chat = Selected();
    var title = settingsOpen ? Ui.Text(T("設定", "Settings"), "title", bold: true).OneLine()
        : chat != null ? Ui.Column(Ui.Text(Str(chat, "name") ?? "", "title", bold: true).OneLine(), Ui.Text(Str(chat, "service") ?? "", "small", "dim").OneLine()).Gap(0)
        : Ui.Text("iMessage", "title", bold: true).OneLine();
    return Ui.Row(
        Ui.Icon("sms").Align("center"),
        title.Grow().Align("center"),
        Ui.Button("refresh", icon: "refresh", tip: T("読み直す", "Reload")).Align("center"),
        Ui.Button("settings", icon: "settings", style: settingsOpen ? "selected" : null, tip: T("つなぐ先", "Connection")).Align("center")).Gap(12).Pad(16, 0);
}

JsonObject ChatList()
{
    if (client == null) return Ui.Column(Ui.Text(T("会話", "Conversations"), "title", bold: true), Ui.Text(T("つなぐと、ここに会話が並びます", "Conversations appear here once connected"), color: "dim"));
    if (chats.Count == 0)
        return Ui.Column(Ui.Text(T("会話", "Conversations"), "title", bold: true), Ui.Text(connectionError != null ? T("読めません", "Couldn't load") : T("読んでいます", "Loading"), color: "dim"));
    var rows = chats.Select(ChatRow).ToArray();
    return Ui.Column(Ui.Scroll(Ui.Column(rows).Gap(4), "chats").Grow()).Grow();
}

JsonObject ChatRow(JsonObject chat)
{
    var id = Str(chat, "id") ?? "";
    var preview = chat["preview"] as JsonObject;
    var previewText = preview == null ? "" : (preview["fromMe"]?.GetValue<bool>() == true ? T("自分: ", "Me: ") : "") + (Str(preview, "text") ?? "");
    var unread = chat["unreadCount"]?.GetValue<int>() ?? 0;
    var nameRow = Ui.Row(
        Ui.Text(Str(chat, "name") ?? "", bold: unread > 0).OneLine().Grow(),
        Ui.Text(Short(Str(chat, "lastMessageAt")), "small", "dim").Align("center")).Gap(12);
    var previewRow = Ui.Row(Ui.Text(previewText.Replace('\n', ' '), "small", unread > 0 ? null : "dim").OneLine().Grow()).Gap(8);
    if (unread > 0) previewRow["children"]!.AsArray().Add(Ui.Text(unread.ToString(), "small", "accent", bold: true).Pad(12, 2).Background("field", 16).Align("center"));
    var row = Ui.Row(Face(chat, 64), Ui.Column(nameRow, previewRow).Gap(2).Grow().Align("center")).Gap(14).Pad(12, 10).OnPress("chat:" + id);
    if (id == selectedId) row["background"] = "accent";
    return row;
}

/// <summary>会話の顔（連絡先の絵か、名前の頭文字の丸）。</summary>
JsonObject Face(JsonObject chat, float size)
{
    if (FaceId(chat) is { } f && pictures.GetValueOrDefault("face:" + f) is { } data) return Ui.Image(data, size, size, size / 2);
    return Initial(Str(chat, "name") ?? "?", size);
}

JsonObject Initial(string name, float size)
{
    var first = name.TrimStart('+', ' ', '(').Length > 0 ? new StringInfo(name.TrimStart('+', ' ', '(')).SubstringByTextElements(0, 1).ToUpperInvariant() : "?";
    if (char.IsDigit(first[0])) return Ui.Column(Ui.Icon("person", "dim").Align("center")).Width(size).Pad(0, (size - 44) / 2).Background("field", size / 2);
    return Ui.Column(Ui.Text(first, bold: true).TextAlign("center").Align("center")).Width(size).Pad(0, (size - 40) / 2).Background("field", size / 2);
}

static string? FaceId(JsonObject chat) => (chat["faces"] as JsonArray ?? []).Select(x => x?.GetValue<string>()).FirstOrDefault(x => x != null);

JsonObject Main()
{
    var parts = new List<JsonNode>();
    if (notice != null && DateTimeOffset.UtcNow < noticeUntil)
        parts.Add(Ui.Row(Ui.Icon(noticeError ? "error" : "check_circle", noticeError ? "error" : "good").Align("center"), Ui.Text(notice).Grow().Align("center")).Gap(12).Pad(20, 14).Background("field", 30));
    if (settingsOpen || client == null) parts.Add(Settings());
    else if (connectionError != null && chats.Count == 0) parts.Add(Problem(connectionError));
    else if (Selected() is not { } chat)
        parts.Add(Ui.Column(Ui.Spacer(200), Ui.Icon("forum", "dim").Align("center"), Ui.Text(T("左の一覧から会話を選んでください", "Pick a conversation on the left"), "title").Align("center")).Gap(16).Grow());
    else parts.Add(Conversation(chat));
    if (connectionError != null && chats.Count > 0 && !settingsOpen)
        parts.Insert(0, Ui.Row(Ui.Icon("cloud_off", "warning").Align("center"), Ui.Text(connectionError, "small").Grow().Align("center")).Gap(12).Pad(20, 10).Background("field", 30));
    return Ui.Column([.. parts]).Gap(14).Grow();
}

JsonObject Problem(string text) =>
    Ui.Column(Ui.Spacer(160), Ui.Icon("cloud_off", "dim").Align("center"), Ui.Text(text, "title").TextAlign("center").Align("center"),
        Ui.Button("refresh", T("もう一度", "Try again"), "refresh").Align("center")).Gap(18).Grow();

JsonObject Settings()
{
    var items = new List<JsonNode>
    {
        Ui.Text(T("Mac で動く imsg-web につなぎます", "Connect to imsg-web running on your Mac"), "title", bold: true),
        Ui.Text(T("トークンは Mac で次を実行して出します（送るなら --send を付ける）。出たトークンを下に貼ります",
            "Get a token by running this on the Mac (add --send to allow sending), then paste it below"), "small", "dim"),
        Ui.Text("~/Library/Application\\ Support/imsg-web/imsg-web client add visor --send", "small", "accent").Pad(16, 10).Background("field", 20),
        Ui.Text(T("URL（Tailscale の名前なら https://〈Mac の名前〉.〈tailnet〉.ts.net）", "URL (with Tailscale: https://<mac>.<tailnet>.ts.net)"), "small", "dim"),
        Ui.Input("url", "https://my-mac.example.ts.net", 1, setUrl),
        Ui.Text(config.Token.Length > 0 ? T("トークン（入れてあります。替えるときだけ貼る）", "Token (set — paste only to replace it)") : T("トークン", "Token"), "small", "dim"),
        Ui.Input("token", T("ここに貼る", "Paste here"), 1),
        Ui.Text(T("証明書（LAN の名前・IP でつなぐときだけ。Mac の state/tls/cert.pem の中身）", "Certificate (only for a LAN name or IP: contents of state/tls/cert.pem on the Mac)"), "small", "dim"),
        Ui.Input("cert", "-----BEGIN CERTIFICATE----- …", 3, setCert),
        Ui.Row(Ui.Button("connect", T("つなぐ", "Connect"), "link", "primary")).Gap(12),
    };
    if (settingsMessage != null) items.Add(Ui.Row(Ui.Icon("error", "error").Align("center"), Ui.Text(settingsMessage).Grow().Align("center")).Gap(12));
    items.Add(Ui.Text(T("トークンはこの PC のこの人だけが戻せる形（Windows の DPAPI）で残します", "The token is stored so only you on this PC can read it (Windows DPAPI)"), "small", "faint"));
    return Ui.Scroll(Ui.Column([.. items]).Gap(14), "settings").Grow();
}

JsonObject Conversation(JsonObject chat)
{
    if (messages.Count == 0)
        return Ui.Column(Ui.Spacer(200), Ui.Text(loadingHistory || historyFor != selectedId ? T("読んでいます", "Loading") : T("まだやりとりがありません", "No messages yet"), color: "dim").Align("center")).Grow();
    var group = chat["isGroup"]?.GetValue<bool>() == true;
    var items = new List<JsonNode>();
    string? lastDay = null;
    foreach (var m in messages)
    {
        var at = Time(Str(m, "createdAt"));
        var day = at?.ToString("yyyy-MM-dd");
        if (day != null && day != lastDay)
        {
            items.Add(Ui.Text(DayLabel(at!.Value), "small", "dim").Align("center"));
            lastDay = day;
        }
        items.Add(Bubble(m, group));
    }
    return Ui.Scroll(Ui.Column([.. items]).Gap(10).Pad(28, 8), "msgs:" + selectedKey, bottom: true).Grow();
}

JsonObject Bubble(JsonObject m, bool group)
{
    var mine = m["isFromMe"]?.GetValue<bool>() == true;
    var inside = new List<JsonNode>();
    if (group && !mine && Str(m, "sender") is { } sender) inside.Add(Ui.Text(sender, "small", "accent", bold: true).OneLine());
    if (m["replyTo"] is JsonObject r)
        inside.Add(Ui.Text($"{Str(r, "sender") ?? T("自分", "Me")}: {Str(r, "text")}", "small", "dim").OneLine().Pad(12, 6).Background("field", 14));
    foreach (var a in m["attachments"] as JsonArray ?? [])
    {
        if (a is not JsonObject att) continue;
        var kind = Str(att, "kind");
        if (kind == "image" && Str(att, "id") is { } aid && pictures.GetValueOrDefault("att:" + aid) is { } data)
            inside.Add(Ui.Image(data, att["sticker"]?.GetValue<bool>() == true ? 160 : 360, 0, 22));
        else
            inside.Add(Ui.Row(Ui.Icon(kind switch { "image" => "image", "audio" => "mic", "video" => "movie", _ => "attach_file" }, "dim"),
                Ui.Text(kind switch { "image" => T("写真", "Photo"), "audio" => T("音声", "Audio"), "video" => T("動画", "Video"), _ => T("ファイル", "File") }, "small", "dim")).Gap(8));
    }
    if ((Str(m, "text") ?? "").Trim().Length > 0) inside.Add(Ui.Text(Str(m, "text")!));
    if (m["link"] is JsonObject link)
    {
        var card = new List<JsonNode>();
        if (link["image"] is JsonObject li && Str(li, "id") is { } lid && pictures.GetValueOrDefault("att:" + lid) is { } ld) card.Add(Ui.Image(ld, 200, 0, 14));
        if (Str(link, "title") is { Length: > 0 } lt) card.Add(Ui.Text(lt, bold: true).OneLine());
        card.Add(Ui.Text(Str(link, "siteName") is { Length: > 0 } site ? site : Str(link, "url") ?? "", "small", "dim").OneLine());
        inside.Add(Ui.Column([.. card]).Gap(4).Pad(12, 10).Background("field", 16));
    }
    if (inside.Count == 0) inside.Add(Ui.Text(T("（表示できないメッセージ）", "(Can't show this message)"), "small", "dim"));
    var bubble = Ui.Column([.. inside]).Gap(8).Pad(22, 14).Background(mine ? "accent" : "card", 30);
    var meta = new List<JsonNode> { Ui.Text(Clock(Str(m, "createdAt")), "small", "faint") };
    foreach (var re in m["reactions"] as JsonArray ?? [])
        if (re is JsonObject ro) meta.Add(Ui.Text(Str(ro, "emoji") + (ro["count"]?.GetValue<int>() is > 1 and var n ? n.ToString() : ""), "small"));
    var stack = Ui.Column(bubble.Align(mine ? "end" : "start"), Ui.Row([.. meta]).Gap(8).Align(mine ? "end" : "start")).Gap(4).Grow();
    var avatar = !mine && group ? (Str(m, "avatarId") is { } av && pictures.GetValueOrDefault("face:" + av) is { } face ? Ui.Image(face, 48, 48, 24) : Initial(Str(m, "sender") ?? "?", 48)) : null;
    // 相手の吹き出しは左、自分のは右。反対の側を空けて、窓いっぱいに広がらないようにする
    return mine ? Ui.Row(Ui.Spacer(140), stack).Gap(0)
        : avatar != null ? Ui.Row(avatar.Align("start"), stack, Ui.Spacer(100)).Gap(10)
        : Ui.Row(stack, Ui.Spacer(140)).Gap(0);
}

JsonObject ComposeBar()
{
    if (client == null || settingsOpen)
        return Ui.Row(Ui.Text(T("つなぐ先を入れると、ここから送れます", "Connect to send from here"), color: "dim").Grow().Align("center")).Pad(24, 0);
    if (Selected() == null)
        return Ui.Row(Ui.Text(T("会話を選ぶと、ここから返事を送れます", "Pick a conversation to reply"), color: "dim").Grow().Align("center")).Pad(24, 0);
    if (sendMode == "off")
        return Ui.Row(Ui.Icon("lock", "dim").Align("center"), Ui.Text(T("読むだけのトークンなので送れません", "Read-only token: sending is off"), color: "dim").Grow().Align("center")).Gap(12).Pad(24, 0);
    return Ui.Row(
        Ui.Input("compose", sendMode == "dry-run" ? T("iMessage（試しのモード）", "iMessage (dry run)") : "iMessage", 1).Grow().Align("center"),
        Ui.Button("send", icon: sending ? "hourglass_top" : "arrow_upward", style: "primary", tip: T("送る", "Send")).Align("center")).Gap(12);
}

string Describe()
{
    if (client == null || settingsOpen) return T("iMessage の窓: つなぐ先（imsg-web）の設定を出しています", "iMessage window: showing the connection settings");
    if (Selected() is not { } chat) return T($"iMessage の窓: 会話の一覧（{chats.Count} 件）。会話は選んでいません", $"iMessage window: {chats.Count} conversations, none selected");
    var sb = new StringBuilder(T($"iMessage の窓: {Str(chat, "name")} との会話を出しています。最近のやりとり:\n", $"iMessage window: conversation with {Str(chat, "name")}. Recent messages:\n"));
    foreach (var m in messages.TakeLast(10))
        sb.Append($"[{When(Str(m, "createdAt"))}] {(m["isFromMe"]?.GetValue<bool>() == true ? T("自分", "Me") : Str(m, "sender") ?? Str(chat, "name"))}: {Body(m)}\n");
    return sb.ToString();
}

JsonObject? Selected() => selectedId == null ? null : chats.FirstOrDefault(c => Str(c, "id") == selectedId);

string Body(JsonObject m)
{
    var parts = new List<string>();
    foreach (var a in m["attachments"] as JsonArray ?? [])
        parts.Add(Str(a, "kind") switch { "image" => T("（写真）", "(photo)"), "audio" => T("（音声）", "(audio)"), "video" => T("（動画）", "(video)"), _ => T("（ファイル）", "(file)") });
    if (Str(m, "text") is { Length: > 0 } t) parts.Add(t);
    if (m["link"] is JsonObject l) parts.Add(Str(l, "url") ?? "");
    return string.Join(" ", parts);
}

// --- 時刻 ---

static DateTimeOffset? Time(string? iso) =>
    iso != null && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t.ToLocalTime() : null;

string Clock(string? iso) => Time(iso)?.ToString("H:mm") ?? "";

/// <summary>一覧の時刻: 今日なら時刻、7 日以内なら曜日、それより前なら日付。</summary>
string Short(string? iso)
{
    if (Time(iso) is not { } t) return "";
    var days = (DateTime.Today - t.Date).Days;
    var culture = CultureInfo.GetCultureInfo(lang == "en" ? "en-US" : "ja-JP");
    return days == 0 ? t.ToString("H:mm") : days == 1 ? T("昨日", "Yesterday") : days < 7 ? t.ToString("ddd", culture) : lang == "en" ? t.ToString("MMM d", culture) : t.ToString("M/d");
}

string When(string? iso) => Time(iso) is { } t ? t.ToString(lang == "en" ? "MMM d H:mm" : "M/d H:mm", CultureInfo.InvariantCulture) : "";

string DayLabel(DateTimeOffset t)
{
    var days = (DateTime.Today - t.Date).Days;
    var culture = CultureInfo.GetCultureInfo(lang == "en" ? "en-US" : "ja-JP");
    return days == 0 ? T("今日", "Today") : days == 1 ? T("昨日", "Yesterday") : lang == "en" ? t.ToString("ddd, MMM d", culture) : t.ToString("M月d日（ddd）", culture);
}

static string? Str(JsonNode? o, string key) => o?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

// 会話を epoch をまたいで見分ける（id は epoch ごとに変わる）
static string Key(JsonObject chat) => $"{Str(chat, "name")}|{Str(chat, "service")}|{chat["isGroup"]}";
