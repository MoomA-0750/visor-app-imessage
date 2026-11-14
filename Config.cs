using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace VisorImessage;

/// <summary>
/// つなぐ先（imsg-web の URL・トークン・証明書）。VISOR_APP_DATA の config.json に残す（入れ直しても消えない）。
/// トークンは Windows の DPAPI でこの PC のこの人だけが戻せる形にして書く（送れるトークンは、持っていれば本人として送れるため）。
/// </summary>
sealed class Config
{
    public string Url { get; set; } = "";
    public string Token { get; set; } = "";
    /// <summary>LAN の自己署名の証明書（cert.pem の中身）。Tailscale Serve の名前なら要らない。</summary>
    public string? Cert { get; set; }

    public bool Ready => Url.Length > 0 && Token.Length > 0;

    static string FilePath => Path.Combine(VisorSdk.VisorApp.DataDir, "config.json");

    public static Config Load()
    {
        var c = new Config();
        try
        {
            if (!File.Exists(FilePath)) return c;
            var o = JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject;
            c.Url = o?["url"]?.GetValue<string>() ?? "";
            c.Cert = o?["cert"]?.GetValue<string>();
            // 守った形（tokenProtected）か、手で書いた素の形（token。次に保存するときに守った形にする）
            if (o?["tokenProtected"]?.GetValue<string>() is { Length: > 0 } p)
                c.Token = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(p), null, DataProtectionScope.CurrentUser));
            else if (o?["token"]?.GetValue<string>() is { Length: > 0 } plain)
            {
                c.Token = plain;
                c.Save();
            }
            // cert.pem を同じ場所に置いてもよい
            var pem = Path.Combine(VisorSdk.VisorApp.DataDir, "cert.pem");
            if (string.IsNullOrWhiteSpace(c.Cert) && File.Exists(pem)) c.Cert = File.ReadAllText(pem);
        }
        catch (Exception)
        {
            // 読めなければ、設定の画面から入れ直してもらう
        }
        return c;
    }

    public void Save()
    {
        Directory.CreateDirectory(VisorSdk.VisorApp.DataDir);
        var o = new JsonObject { ["url"] = Url };
        if (Token.Length > 0)
            o["tokenProtected"] = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(Token), null, DataProtectionScope.CurrentUser));
        if (!string.IsNullOrWhiteSpace(Cert)) o["cert"] = Cert;
        File.WriteAllText(FilePath, o.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
}
