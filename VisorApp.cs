// visor のアプリを C# で作るための部品（1 ファイル）。アプリのリポジトリにこのファイルを写して使う。
// 元は visor のリポジトリの sdk/dotnet/VisorApp.cs。話し方の決まりは visor の docs/extensions.md。
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VisorSdk;

/// <summary>
/// visor との受け渡し: 標準入出力で 1 行 1 つの JSON を送り受けする。Run で visor から来るものを読み続ける。
/// 送るのはどのスレッドからでもよい。来たもの（ボタン・見えているか・道具の呼び出し）は Run を回しているスレッドで渡す。
/// </summary>
public sealed class VisorApp
{
    readonly TextWriter _out;
    readonly object _gate = new();
    readonly Dictionary<string, Func<JsonObject, Task<string>>> _tools = [];
    readonly List<JsonObject> _toolList = [];
    readonly Dictionary<string, TaskCompletionSource<JsonNode?>> _calls = [];
    int _nextCall;

    public VisorApp()
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        _out = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
    }

    /// <summary>このアプリの id（visor が入れたフォルダの名前）。</summary>
    public static string Id => Environment.GetEnvironmentVariable("VISOR_APP_ID") ?? "app";

    /// <summary>アプリが自分で残すもの（設定など）の置き場所。入れ直しても消えない。</summary>
    public static string DataDir => Environment.GetEnvironmentVariable("VISOR_APP_DATA") is { Length: > 0 } d ? d : Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>visor 自身の置き場所（%LOCALAPPDATA%\visor）。前の版の設定を引き継ぐときなどに読む。</summary>
    public static string? VisorDataDir => Environment.GetEnvironmentVariable("VISOR_DATA");

    /// <summary>ボタンが押された（ボタンの id）。</summary>
    public event Action<string>? Event;

    /// <summary>
    /// 入力欄（Ui.Input）の文字が変わった・Enter で送った（入力欄の id、文字、送ったか）。送ったとき、visor は欄を空にする。
    /// </summary>
    public event Action<string, string, bool>? Input;

    /// <summary>ほかの窓（ファイル・ギャラリー・写真）から運んだファイルを、この窓に置かれた（ファイルの場所）。app.json に "drop": true が要る。</summary>
    public event Action<IReadOnlyList<string>>? Drop;

    /// <summary>窓が見えるようになった・見えなくなった（見えていない間は更新を止めてよい）。</summary>
    public event Action<bool>? Visible;

    public bool IsVisible { get; private set; }

    /// <summary>
    /// 対応する VR のアプリ（app.json の vrApps）が動き出した・止まった（動いているか、SteamVR のアプリのキー、名前）。
    /// visor は対応するアプリが動き出すとこのアプリを起こし、起きたときにも今の様子を送る。
    /// </summary>
    public event Action<bool, string?, string?>? VrApp;

    /// <summary>対応する VR のアプリが今動いているか。</summary>
    public bool VrAppRunning { get; private set; }

    /// <summary>HUD のウィジェット（app.json の widgets）が置かれた・外された（ウィジェットの id、見えているか）。</summary>
    public event Action<string, bool>? WidgetVisible;

    readonly HashSet<string> _widgets = [];

    /// <summary>そのウィジェットが HUD に置かれているか（置かれている間だけ中身を送ればよい）。</summary>
    public bool IsWidgetVisible(string id)
    {
        lock (_widgets) return _widgets.Contains(id);
    }

    /// <summary>置かれているウィジェットがあるか。</summary>
    public bool AnyWidgetVisible
    {
        get
        {
            lock (_widgets) return _widgets.Count > 0;
        }
    }

    // --- 送る ---

    /// <summary>
    /// 窓の中身を入れ替える（Ui の部品で組んだもの）。left・bottom・right・top は窓の左・下・右・上の外に浮かべる板（app.json の ornaments で場所を取ったもの）の中身。
    /// null を渡したところは前のまま（変わったところだけ送れば、絵の多い一覧を毎秒組み直さずに済む）。ui も null なら窓の中身は前のまま。
    /// </summary>
    /// <remarks>popup は下の板の上に浮かべる小窓の中身（空の JsonObject でしまう）。</remarks>
    public void Render(JsonNode? ui, JsonNode? left = null, JsonNode? bottom = null, JsonNode? right = null, JsonNode? top = null, JsonNode? popup = null)
    {
        var msg = new JsonObject { ["type"] = "render" };
        if (ui != null) msg["ui"] = ui;
        if (left != null) msg["left"] = left;
        if (bottom != null) msg["bottom"] = bottom;
        if (right != null) msg["right"] = right;
        if (top != null) msg["top"] = top;
        if (popup != null) msg["popup"] = popup;
        Send(msg);
    }

    /// <summary>HUD のウィジェットの中身を入れ替える（画面と同じ部品。見るだけなので、ボタンは押せない）。</summary>
    public void Widget(string id, JsonNode ui) => Send(new JsonObject { ["type"] = "widget", ["id"] = id, ["ui"] = ui });

    /// <summary>Claude が窓を読んだときに渡す文。</summary>
    public void Describe(string text) => Send(new JsonObject { ["type"] = "describe", ["text"] = text });

    /// <summary>visor の通知を出す。</summary>
    public void Notify(string title, string body = "") => Send(new JsonObject { ["type"] = "notify", ["title"] = title, ["body"] = body });

    /// <summary>入力欄の文字を決める（送るボタンで送ったあとに空にする・送れなかった文字を戻すなど）。</summary>
    public void SetInput(string id, string text) => Send(new JsonObject { ["type"] = "input", ["id"] = id, ["text"] = text });

    /// <summary>窓を開く。</summary>
    public void Open() => Send(new JsonObject { ["type"] = "open" });

    /// <summary>
    /// 本人のこと（このアプリから見た名前と別名）を visor に知らせる。別名は「場所:ID」の形（vrchat:usr_… など）。
    /// visor 同士の共有で、連絡先と一緒にいる人をつき合わせるのに使う（対にするときに相手へ渡る）。
    /// </summary>
    public void Identity(string? name, params string[] handles) =>
        Send(new JsonObject { ["type"] = "identity", ["name"] = name, ["handles"] = new JsonArray([.. handles.Select(h => (JsonNode)h)]) });

    /// <summary>今、本人と一緒にいる人（別名と名前）。共有の送り先で、連絡先のうちこの人たちが上に並ぶ。変わるたびに全員を送り直す（いなければ空）。</summary>
    public void Together(IEnumerable<(string Handle, string Name)> people) => Send(new JsonObject { ["type"] = "together", ["people"] = PeopleJson(people) });

    /// <summary>このアプリが知っている人（友だちなど。別名と名前）。</summary>
    public void Contacts(IEnumerable<(string Handle, string Name)> people) => Send(new JsonObject { ["type"] = "contacts", ["people"] = PeopleJson(people) });

    static JsonArray PeopleJson(IEnumerable<(string Handle, string Name)> people) =>
        new([.. people.Select(p => (JsonNode)new JsonObject { ["handle"] = p.Handle, ["name"] = p.Name })]);

    /// <summary>
    /// Claude に渡す道具を足す（app_アプリ_名前 で見える）。parameters は 名前 → 型（string・number・boolean）。
    /// handler の返した文が Claude に渡る（20 秒以内に返す）。足し終えたら PublishTools で visor に知らせる。
    /// </summary>
    public void Tool(string name, string description, Func<JsonObject, Task<string>> handler, params (string Name, string Type)[] parameters)
    {
        _tools[name] = handler;
        var p = new JsonObject();
        foreach (var (n, t) in parameters) p[n] = t;
        _toolList.RemoveAll(t => t["name"]?.GetValue<string>() == name);
        _toolList.Add(new JsonObject { ["name"] = name, ["description"] = description, ["params"] = p });
    }

    public void Tool(string name, string description, Func<JsonObject, string> handler, params (string Name, string Type)[] parameters) =>
        Tool(name, description, a => Task.FromResult(handler(a)), parameters);

    public void PublishTools() => Send(new JsonObject { ["type"] = "tools", ["tools"] = new JsonArray(_toolList.Select(t => (JsonNode)t.DeepClone()).ToArray()) });

    /// <summary>visor に頼む（窓口と同じメソッド: vr.stats・mcp.call など）。</summary>
    public Task<JsonNode?> Call(string method, JsonObject? parameters = null)
    {
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        string id;
        lock (_gate)
        {
            id = (++_nextCall).ToString();
            _calls[id] = tcs;
        }
        Send(new JsonObject { ["type"] = "call", ["call"] = id, ["method"] = method, ["params"] = parameters ?? [] });
        return tcs.Task;
    }

    void Send(JsonObject msg)
    {
        lock (_gate) _out.WriteLine(msg.ToJsonString());
    }

    // --- 受ける ---

    /// <summary>visor から来るものを読み続ける（visor が止めるか、標準入力が閉じるまで）。</summary>
    public async Task Run()
    {
        var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        while (await input.ReadLineAsync() is { } line)
        {
            JsonObject? msg;
            try { msg = JsonNode.Parse(line) as JsonObject; }
            catch (JsonException) { continue; }
            if (msg == null) continue;
            switch (msg["type"]?.GetValue<string>())
            {
                case "event":
                    Event?.Invoke(msg["id"]?.GetValue<string>() ?? "");
                    break;
                case "input":
                    Input?.Invoke(msg["id"]?.GetValue<string>() ?? "", msg["text"]?.GetValue<string>() ?? "", msg["submit"]?.GetValue<bool>() == true);
                    break;
                case "drop":
                    Drop?.Invoke((msg["files"] as JsonArray ?? []).Select(f => f?.GetValue<string>()).OfType<string>().ToList());
                    break;
                case "visible":
                    IsVisible = msg["visible"]?.GetValue<bool>() == true;
                    Visible?.Invoke(IsVisible);
                    break;
                case "widget_visible":
                {
                    var id = msg["id"]?.GetValue<string>() ?? "";
                    var on = msg["visible"]?.GetValue<bool>() == true;
                    lock (_widgets)
                    {
                        if (on) _widgets.Add(id);
                        else _widgets.Remove(id);
                    }
                    WidgetVisible?.Invoke(id, on);
                    break;
                }
                case "vr_app":
                    VrAppRunning = msg["running"]?.GetValue<bool>() == true;
                    VrApp?.Invoke(VrAppRunning, msg["key"]?.GetValue<string>(), msg["name"]?.GetValue<string>());
                    break;
                case "tool_call":
                    _ = AnswerTool(msg);
                    break;
                case "call_result":
                {
                    TaskCompletionSource<JsonNode?>? tcs;
                    lock (_gate) _calls.Remove(msg["call"]?.ToString() ?? "", out tcs);
                    if (tcs == null) break;
                    if (msg["error"] is { } err) tcs.TrySetException(new InvalidOperationException(err.ToString()));
                    else tcs.TrySetResult(msg["result"]?.DeepClone());
                    break;
                }
            }
        }
    }

    async Task AnswerTool(JsonObject msg)
    {
        var name = msg["name"]?.GetValue<string>() ?? "";
        string text;
        try
        {
            text = _tools.TryGetValue(name, out var h) ? await h(msg["arguments"] as JsonObject ?? []) : $"知らない道具です: {name}";
        }
        catch (Exception ex)
        {
            text = $"失敗しました: {ex.Message}";
        }
        Send(new JsonObject { ["type"] = "tool_result", ["call"] = msg["call"]?.DeepClone(), ["text"] = text });
    }
}

/// <summary>窓の中身の部品（docs/extensions.md の「画面の部品」）を組む。</summary>
public static class Ui
{
    public static JsonObject Column(params JsonNode?[] children) => new() { ["type"] = "column", ["children"] = Children(children) };
    public static JsonObject Row(params JsonNode?[] children) => new() { ["type"] = "row", ["children"] = Children(children) };

    /// <summary>size: small・body・title・large・display。color: dim・faint・accent・warning・error・good。</summary>
    public static JsonObject Text(string text, string? size = null, string? color = null, bool bold = false) =>
        Set(new JsonObject { ["type"] = "text", ["text"] = text }, ("size", size), ("color", color), ("weight", bold ? "bold" : null));

    public static JsonObject Markdown(string text) => new() { ["type"] = "markdown", ["text"] = text };

    /// <summary>text が無ければ丸いアイコンのボタン（tip はしばらく指すと出す説明）。style: primary・selected・danger。</summary>
    public static JsonObject Button(string id, string? text = null, string? icon = null, string? style = null, bool small = false, string? tip = null) =>
        Set(new JsonObject { ["type"] = "button", ["id"] = id }, ("text", text), ("icon", icon), ("style", style), ("size", small ? "small" : null), ("tip", tip));

    /// <summary>
    /// 絵（base64 の JPEG か PNG）。height に収まるよう比を保って縮める。width も渡すと width×height の枠いっぱいに、はみ出す分を真ん中で切り抜く
    /// （一覧のジャケットの大きさをそろえる）。radius で角を丸める（枠の半分なら丸）。
    /// </summary>
    public static JsonObject Image(string base64, float height, float width = 0, float radius = 0)
    {
        var o = new JsonObject { ["type"] = "image", ["data"] = base64, ["height"] = height };
        if (width > 0) o["width"] = width;
        if (radius > 0) o["radius"] = radius;
        return o;
    }

    /// <summary>
    /// 文字の入力欄。押すとキーボードの窓が出て、そこから打てる（PC のキーボードでも）。Enter で送り、Shift+Enter で改行。
    /// 文字が変わるたび・送ったときに VisorApp.Input に届く。打った文字は画面を送り直しても残る。value は初めて出すときの文字。
    /// </summary>
    public static JsonObject Input(string id, string? placeholder = null, int lines = 5, string? value = null)
    {
        var o = Set(new JsonObject { ["type"] = "input", ["id"] = id }, ("placeholder", placeholder), ("value", value));
        o["lines"] = lines;
        return o;
    }

    public static JsonObject Icon(string name, string? color = null) => Set(new JsonObject { ["type"] = "icon", ["name"] = name }, ("color", color));
    public static JsonObject Divider() => new() { ["type"] = "divider" };
    public static JsonObject Spacer(float size = 0) => new() { ["type"] = "spacer", ["size"] = size };
    /// <summary>横に引くつまみ。離したときに「id:値」（値は 0〜1）が Event に届く。</summary>
    /// <summary>横に引くつまみ（離したときに「id:値」）。durationMs を渡すと再生の位置のつまみ（経過と長さを中に出す）。</summary>
    public static JsonObject Slider(string id, double value, string? color = null, long durationMs = 0)
    {
        var o = Set(new JsonObject { ["type"] = "slider", ["id"] = id, ["value"] = value }, ("color", color));
        if (durationMs > 0) o["duration"] = durationMs;
        return o;
    }

    public static JsonObject Progress(double value, string? color = null) => Set(new JsonObject { ["type"] = "progress", ["value"] = value }, ("color", color));
    /// <summary>id を付けると、画面を送り直しても同じ id の scroll はスクロールの位置を引き継ぐ（タブごとに違う id にする）。</summary>
    /// <remarks>bottom なら、いちばん下から始め、下にいる間は中身が増えても付いていく（会話の一覧）。</remarks>
    public static JsonObject Scroll(JsonNode child, string? id = null, bool bottom = false)
    {
        var o = Set(new JsonObject { ["type"] = "scroll", ["child"] = child }, ("id", id));
        if (bottom) o["bottom"] = true;
        return o;
    }

    // --- 並べ方（どの部品にも付けられる） ---

    public static JsonObject Grow(this JsonObject o) => With(o, "grow", true);
    /// <summary>文を 1 行に収め、はみ出す分を … にする。</summary>
    public static JsonObject OneLine(this JsonObject o) => With(o, "lines", 1);
    /// <summary>column・row を行ごと押せるようにする（押すと id が Event に届く。中のボタンはそのまま押せる）。</summary>
    public static JsonObject OnPress(this JsonObject o, string id) => With(o, "id", id);
    /// <summary>start・center・end。</summary>
    public static JsonObject Align(this JsonObject o, string align) => With(o, "align", align);
    public static JsonObject Gap(this JsonObject o, float gap) => With(o, "gap", gap);
    public static JsonObject Pad(this JsonObject o, float pad) => With(o, "pad", pad);
    public static JsonObject Pad(this JsonObject o, float x, float y) => With(With(o, "padX", x), "padY", y);
    /// <summary>panel・field・card・accent。</summary>
    public static JsonObject Background(this JsonObject o, string background, float radius = 18) => With(With(o, "background", background), "radius", radius);
    /// <summary>文の幅をこれより狭くしない（表の列をそろえる）。</summary>
    public static JsonObject Width(this JsonObject o, float width) => With(o, "width", width);
    /// <summary>文の寄せ方（start・center・end）。</summary>
    public static JsonObject TextAlign(this JsonObject o, string align) => With(o, "textAlign", align);

    static JsonObject With(JsonObject o, string key, JsonNode value)
    {
        o[key] = value;
        return o;
    }

    static JsonObject Set(JsonObject o, params (string Key, string? Value)[] values)
    {
        foreach (var (k, v) in values)
            if (v != null) o[k] = v;
        return o;
    }

    static JsonArray Children(JsonNode?[] children) => new(children.Where(c => c != null).Select(c => c!.Parent == null ? c : c.DeepClone()).ToArray());
}
