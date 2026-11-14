using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;

namespace VisorImessage;

/// <summary>imsg-web の答えがエラーだった（状態の番号と、imsg-web の code）。</summary>
sealed class ImsgException(int status, string? code, string message) : Exception(message)
{
    public int Status => status;
    public string? Code => code;
}

/// <summary>
/// imsg-web（Mac で動く iMessage の Web サーバー）の API を呼ぶ。決まりは imsg-web の docs/api.md:
/// <list type="bullet">
/// <item>トークンは Authorization: Bearer で渡す。Origin・Cookie・Sec-Fetch-Site は付けない（付けると 403 で断られる）</item>
/// <item>LAN の自己署名の証明書なら、imsg-web の cert.pem を根として確かめる（確かめるのを切ることはしない）。Tailscale Serve の名前なら、ふつうに確かめる</item>
/// <item>id は一時のもの（サーバーの epoch の中だけ）。残さない</item>
/// </list>
/// </summary>
sealed class ImsgClient : IDisposable
{
    readonly HttpClient _http;
    readonly Uri _base;

    public ImsgClient(string url, string token, string? certPem)
    {
        _base = new Uri(url.TrimEnd('/') + "/");
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        if (!string.IsNullOrWhiteSpace(certPem))
        {
            var roots = new X509Certificate2Collection();
            roots.ImportFromPem(certPem);
            handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, errors) =>
            {
                if (errors == SslPolicyErrors.None) return true;
                // 名前が合わないものは通さない。根が知らないものだけ、渡された cert.pem を根として確かめ直す
                if (cert == null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != 0) return false;
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.AddRange(roots);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(new X509Certificate2(cert));
            };
        }
        // 待つ長さは頼みごとごとに決める（読むのは 20 秒、送るのは 75 秒、ファイルを上げるのは 5 分）
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("visor-imessage/0.1");
    }

    public async Task<JsonObject> GetJson(string path, CancellationToken ct = default)
    {
        using var res = await Send(new HttpRequestMessage(HttpMethod.Get, new Uri(_base, path)), ct, ReadTimeout);
        return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct)) as JsonObject ?? throw new ImsgException(0, null, "bad response");
    }

    public async Task<(byte[] Bytes, string Type)> GetBytes(string path, CancellationToken ct = default)
    {
        using var res = await Send(new HttpRequestMessage(HttpMethod.Get, new Uri(_base, path)), ct, ReadTimeout);
        var bytes = await res.Content.ReadAsByteArrayAsync(ct);
        return (bytes, res.Content.Headers.ContentType?.MediaType ?? "");
    }

    static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(20);

    /// <summary>ファイルを上げる（送る前に 1 つずつ）。返す uploadId を send の uploadIds に入れる。</summary>
    public async Task<string> Upload(string name, Stream content, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_base, "api/uploads?name=" + Uri.EscapeDataString(name)))
        {
            Content = new StreamContent(content),
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var res = await Send(req, ct, TimeSpan.FromMinutes(5));
        var o = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct)) as JsonObject;
        return o?["uploadId"]?.GetValue<string>() ?? throw new ImsgException(0, null, "bad response");
    }

    public async Task<JsonObject> PostJson(string path, JsonObject body, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_base, path))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        using var res = await Send(req, ct, timeout ?? ReadTimeout);
        return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct)) as JsonObject ?? throw new ImsgException(0, null, "bad response");
    }

    async Task<HttpResponseMessage> Send(HttpRequestMessage req, CancellationToken outer, TimeSpan timeout)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(timeout);
        var ct = cts.Token;
        HttpResponseMessage res;
        try
        {
            res = await _http.SendAsync(req, ct);
        }
        catch (HttpRequestException ex) when (ex.InnerException is System.Security.Authentication.AuthenticationException)
        {
            throw new ImsgException(0, "CERT", ex.Message);
        }
        catch (HttpRequestException ex)
        {
            throw new ImsgException(0, "UNREACHABLE", ex.Message);
        }
        catch (OperationCanceledException) when (!outer.IsCancellationRequested)
        {
            throw new ImsgException(0, "TIMEOUT", "timeout");
        }
        if (res.IsSuccessStatusCode) return res;
        string? code = null;
        try
        {
            code = (JsonNode.Parse(await res.Content.ReadAsStringAsync(ct)) as JsonObject)?["code"]?.GetValue<string>();
        }
        catch (Exception) { }
        var status = (int)res.StatusCode;
        res.Dispose();
        throw new ImsgException(status, code, $"{status} {code}");
    }

    public void Dispose() => _http.Dispose();

    /// <summary>エラーを本人に見せる文にする。</summary>
    public static string Explain(Exception ex, Func<string, string, string> t) => ex switch
    {
        ImsgException { Status: 401 } => t("imsg-web がトークンを受け付けませんでした。Mac で発行し直して、設定に入れてください",
            "imsg-web didn't accept the token. Issue a new one on the Mac and enter it in the settings"),
        ImsgException { Status: 403, Code: "CLIENT_FROM_BROWSER" } => t("imsg-web がブラウザからの呼び出しとみなして断りました", "imsg-web refused the request as coming from a browser"),
        ImsgException { Status: 403 } => t("このトークンではできません（読むだけのトークンです）", "This token can't do that (it's read-only)"),
        ImsgException { Status: 409 } => t("会話の一覧が新しくなりました。読み直します", "The conversation list changed. Reloading"),
        ImsgException { Status: 413 } => t("大きすぎます", "Too large"),
        ImsgException { Status: 429 } => t("imsg-web が混んでいます（送るのは 1 分に 10 件まで）。少し待ってください", "imsg-web is busy (up to 10 sends a minute). Wait a moment"),
        ImsgException { Code: "CERT" } => t("imsg-web の証明書を確かめられません。Tailscale の名前でつなぐか、Mac の state/tls/cert.pem を設定に入れてください",
            "Couldn't verify imsg-web's certificate. Connect with the Tailscale name, or put the Mac's state/tls/cert.pem in the settings"),
        ImsgException { Code: "TIMEOUT" } => t("imsg-web から返事がありません", "No response from imsg-web"),
        ImsgException { Code: "UNREACHABLE" } => t("imsg-web につながりません（Mac が眠っているか、imsg-web が止まっています）", "Can't reach imsg-web (the Mac is asleep or imsg-web is stopped)"),
        ImsgException e => t($"imsg-web でうまくいきませんでした（{e.Status} {e.Code}）", $"imsg-web ran into a problem ({e.Status} {e.Code})"),
        _ => t($"うまくいきませんでした（{ex.Message}）", $"Something went wrong ({ex.Message})"),
    };

    /// <summary>id が古くなった（サーバーの起こし直し・Messages のデータベースの入れ替え）。会話の一覧から読み直す。</summary>
    public static bool Stale(Exception ex) => ex is ImsgException { Status: 409 };

    public static bool Unauthorized(Exception ex) => ex is ImsgException { Status: 401 };

    public static HttpStatusCode? StatusOf(Exception ex) => ex is ImsgException e && e.Status > 0 ? (HttpStatusCode)e.Status : null;
}
