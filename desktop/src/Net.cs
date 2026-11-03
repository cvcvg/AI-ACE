using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

public class ApiError : Exception
{
    public ApiError(string m) : base(m) { }
}

public static class Api
{
    public static string RootDir()
    {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        DirectoryInfo d = new DirectoryInfo(dir);
        for (int i = 0; i < 5 && d != null; i++)
        {
            string probe = Path.Combine(d.FullName, "compose", "docker-compose.yml");
            if (File.Exists(probe)) return d.FullName;
            d = d.Parent;
        }
        string env = Environment.GetEnvironmentVariable("AI_ACE_ROOT");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;
        return @"G:\AI-ACE";
    }

    public static int LastStatus;
    public static string LastContentType = "";

    static HttpClient Make(string baseUrl, string token, int seconds)
    {
        HttpClientHandler h = new HttpClientHandler();
        h.UseProxy = false;
        h.AllowAutoRedirect = true;
        HttpClient c = new HttpClient(h);
        c.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        c.Timeout = TimeSpan.FromSeconds(seconds);
        if (!string.IsNullOrEmpty(token))
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    public static async Task<bool> Ping(Settings s)
    {
        try
        {
            using (HttpClient c = Make(s.BaseUrl, null, 5))
            using (HttpResponseMessage r = await c.GetAsync("api/config"))
                return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public static async Task<List<string>> Models(Settings s)
    {
        List<string> outList = new List<string>();
        using (HttpClient c = Make(s.BaseUrl, s.Auth(), 15))
        using (HttpResponseMessage r = await c.GetAsync("api/models"))
        {
            string body = await r.Content.ReadAsStringAsync();
            if (!r.IsSuccessStatusCode) throw new ApiError("获取模型失败: " + Short(body));
            int scan = body.IndexOf("\"id\"");
            while (scan >= 0)
            {
                int colon = body.IndexOf(':', scan);
                int q1 = colon < 0 ? -1 : body.IndexOf('"', colon);
                int q2 = q1 < 0 ? -1 : body.IndexOf('"', q1 + 1);
                if (q2 > q1)
                {
                    string id = body.Substring(q1 + 1, q2 - q1 - 1);
                    if (id.Length > 0 && !outList.Contains(id)) outList.Add(id);
                }
                scan = body.IndexOf("\"id\"", scan + 1);
            }
        }
        return outList;
    }

    public static async Task<string> Signin(Settings s, string email, string password)
    {
        using (HttpClient c = Make(s.BaseUrl, null, 20))
        {
            string json = new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "email", email }, { "password", password } });
            StringContent content = new StringContent(json, Encoding.UTF8, "application/json");
            using (HttpResponseMessage r = await c.PostAsync("api/v1/auths/signin", content))
            {
                string body = await r.Content.ReadAsStringAsync();
                if (!r.IsSuccessStatusCode) throw new ApiError(Short(body));
                Dictionary<string, object> d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body) as Dictionary<string, object>;
                if (d != null && d.ContainsKey("token")) return Convert.ToString(d["token"]);
                throw new ApiError("登录响应异常");
            }
        }
    }

    public static async Task StreamChat(Settings s, List<Msg> history, string prompt,
                                        Action<string, bool> onDelta, CancellationToken ct)
    {
        List<Dictionary<string, object>> msgs = new List<Dictionary<string, object>>();
        int turns = s.HistoryTurns;
        int start = Math.Max(0, history.Count - turns * 2);
        for (int i = start; i < history.Count; i++)
        {
            Msg m = history[i];
            if (m.Error) continue;
            msgs.Add(new Dictionary<string, object> { { "role", m.Role }, { "content", m.Text } });
        }
        msgs.Add(new Dictionary<string, object> { { "role", "user" }, { "content", prompt } });

        Dictionary<string, object> payload = new Dictionary<string, object>();
        payload["model"] = s.Model;
        payload["messages"] = msgs;
        payload["stream"] = true;
        payload["temperature"] = s.Temperature;
        payload["chat_template_kwargs"] = new Dictionary<string, object> { { "enable_thinking", s.Thinking } };

        string json = new JavaScriptSerializer().Serialize(payload);
        using (HttpClient c = Make(s.BaseUrl, s.Auth(), 300))
        using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, "api/chat/completions"))
        {
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using (HttpResponseMessage resp = await c.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                LastStatus = (int)resp.StatusCode;
                LastContentType = resp.Content.Headers.ContentType == null ? "" : resp.Content.Headers.ContentType.ToString();
                if (!resp.IsSuccessStatusCode)
                {
                    string err = await resp.Content.ReadAsStringAsync();
                    throw new ApiError("接口返回 " + (int)resp.StatusCode + ": " + Short(err));
                }
                using (Stream st = await resp.Content.ReadAsStreamAsync())
                using (StreamReader rd = new StreamReader(st, Encoding.UTF8))
                {
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        string line = await rd.ReadLineAsync();
                        if (line == null) break;
                        line = line.Trim();
                        if (line.Length == 0) continue;
                        if (!line.StartsWith("data:")) continue;
                        string data = line.Substring(5).Trim();
                        if (data == "[DONE]") break;
                        bool think;
                        string delta = ExtractDelta(data, out think);
                        if (!string.IsNullOrEmpty(delta)) onDelta(delta, think);
                    }
                }
            }
        }
    }

    static string ExtractDelta(string json, out bool thinking)
    {
        thinking = false;
        try
        {
            JavaScriptSerializer js = new JavaScriptSerializer();
            IDictionary<string, object> d = js.Deserialize<IDictionary<string, object>>(json);
            if (d == null || !d.ContainsKey("choices")) return "";
            System.Collections.IList choices = d["choices"] as System.Collections.IList;
            if (choices == null || choices.Count == 0) return "";
            IDictionary<string, object> ch = choices[0] as IDictionary<string, object>;
            if (ch == null) return "";
            if (ch.ContainsKey("delta"))
            {
                IDictionary<string, object> dl = ch["delta"] as IDictionary<string, object>;
                if (dl != null)
                {
                    if (dl.ContainsKey("content") && dl["content"] != null)
                    {
                        string c = Convert.ToString(dl["content"]);
                        if (c.Length > 0) return c;
                    }
                    if (dl.ContainsKey("reasoning_content") && dl["reasoning_content"] != null)
                    {
                        string rc = Convert.ToString(dl["reasoning_content"]);
                        if (rc.Length > 0) { thinking = true; return rc; }
                    }
                }
            }
            if (ch.ContainsKey("message"))
            {
                IDictionary<string, object> mm = ch["message"] as IDictionary<string, object>;
                if (mm != null && mm.ContainsKey("content") && mm["content"] != null)
                {
                    string mc = Convert.ToString(mm["content"]);
                    if (mc.Length > 0) return mc;
                }
            }
            if (ch.ContainsKey("text") && ch["text"] != null) return Convert.ToString(ch["text"]);
        }
        catch { }
        return "";
    }

    public static async Task<string> AgentRun(Settings s, string goal)
    {
        using (HttpClient c = Make(s.AgentUrl, null, 30))
        {
            string json = new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "goal", goal }, { "wait_seconds", 0 } });
            using (HttpResponseMessage r = await c.PostAsync("tasks", new StringContent(json, Encoding.UTF8, "application/json")))
            {
                string body = await r.Content.ReadAsStringAsync();
                if (!r.IsSuccessStatusCode) throw new ApiError(Short(body));
                Dictionary<string, object> d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body) as Dictionary<string, object>;
                if (d != null && d.ContainsKey("id")) return Convert.ToString(d["id"]);
                if (d != null && d.ContainsKey("task_id")) return Convert.ToString(d["task_id"]);
                throw new ApiError("任务接口响应异常");
            }
        }
    }

    public static async Task<Dictionary<string, object>> AgentStatus(Settings s, string id)
    {
        using (HttpClient c = Make(s.AgentUrl, null, 20))
        using (HttpResponseMessage r = await c.GetAsync("tasks/" + id))
        {
            string body = await r.Content.ReadAsStringAsync();
            if (!r.IsSuccessStatusCode) throw new ApiError(Short(body));
            return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body) as Dictionary<string, object>;
        }
    }

    public static string ReadLocalText(string path, int maxChars)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext != ".txt" && ext != ".md" && ext != ".csv" && ext != ".json" && ext != ".log" && ext != ".py" && ext != ".cs" && ext != ".js" && ext != ".xml" && ext != ".yml" && ext != ".yaml" && ext != ".sql" && ext != ".html")
            throw new ApiError("只支持文本类文件（txt/md/csv/json/py/cs/js/xml/yml…）；PDF/Word 请在网页端或任务模式里读取。");
        string s = File.ReadAllText(path, Encoding.UTF8);
        if (s.Length > maxChars) s = s.Substring(0, maxChars) + "\n…（已截断）";
        return s;
    }

    public static string Short(string s)
    {
        if (s == null) return "";
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= 160 ? s : s.Substring(0, 160) + "…";
    }
}
