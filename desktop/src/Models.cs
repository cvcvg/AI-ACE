using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

public class Msg
{
    public string Role = "user";
    public string Text = "";
    public string Time = "";
    public bool Error = false;

    public Msg() { }
    public Msg(string role, string text)
    {
        Role = role;
        Text = text;
        Time = DateTime.Now.ToString("HH:mm");
    }
}

public class Conv
{
    public string Id = "";
    public string Title = "新对话";
    public List<Msg> Msgs = new List<Msg>();
    public string Updated = "";

    public Conv() { }
    public Conv(string id) { Id = id; Updated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"); }

    public void Touch() { Updated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"); }

    public string Preview()
    {
        for (int i = Msgs.Count - 1; i >= 0; i--)
        {
            if (Msgs[i].Role == "assistant" && !Msgs[i].Error && Msgs[i].Text.Length > 0)
                return Short(Msgs[i].Text, 40);
        }
        return "空对话";
    }

    static string Short(string s, int n)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= n ? s : s.Substring(0, n) + "…";
    }
}

public class Settings
{
    public string BaseUrl = "http://127.0.0.1:3000";
    public string AgentUrl = "http://127.0.0.1:8011";
    public string FilesUrl = "http://127.0.0.1:8010";
    public string ApiKey = "";
    public string Token = "";
    public string Email = "";
    public string Model = "";
    public double Temperature = 0.7;
    public int HistoryTurns = 10;
    public bool ShowTime = true;
    public bool Thinking = false;

    public string Auth()
    {
        if (!string.IsNullOrEmpty(Token)) return Token;
        return ApiKey;
    }
}

public static class Store
{
    public static string Dir
    {
        get
        {
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AI-ACE-Desktop");
            if (!Directory.Exists(d)) Directory.CreateDirectory(d);
            return d;
        }
    }

    public static string SettingsPath { get { return Path.Combine(Dir, "config.json"); } }
    public static string ConvsPath { get { return Path.Combine(Dir, "chats.json"); } }

    public static Settings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                Settings s = new JavaScriptSerializer().Deserialize<Settings>(json);
                if (s != null)
                {
                    if (s.BaseUrl == null) s.BaseUrl = "http://127.0.0.1:3000";
                    if (s.AgentUrl == null) s.AgentUrl = "http://127.0.0.1:8011";
                    if (s.FilesUrl == null) s.FilesUrl = "http://127.0.0.1:8010";
                    return s;
                }
            }
        }
        catch { }
        return new Settings();
    }

    public static void SaveSettings(Settings s)
    {
        try { File.WriteAllText(SettingsPath, new JavaScriptSerializer().Serialize(s)); }
        catch { }
    }

    public static List<Conv> LoadConvs()
    {
        try
        {
            if (File.Exists(ConvsPath))
            {
                string json = File.ReadAllText(ConvsPath);
                List<Conv> list = new JavaScriptSerializer().Deserialize<List<Conv>>(json);
                if (list != null) return list;
            }
        }
        catch { }
        return new List<Conv>();
    }

    public static void SaveConvs(List<Conv> list)
    {
        try { File.WriteAllText(ConvsPath, new JavaScriptSerializer().Serialize(list)); }
        catch { }
    }
}
