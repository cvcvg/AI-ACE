using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// ════════════════════════════════════════════════════════════════════
//  数据目录：定位 / 校验 / 建子目录 / 写 compose\.env / 整体迁移
//  契约：数据目录含 5 个子目录 open-webui documents output knowledge database
//        <安装目录>\compose\.env 唯一键 AIACE_DATA=<正斜杠绝对路径>
//        system\install-state.json 里的 dataDir 记录当前数据目录
//  依赖：.NET Framework 4.x + WinForms，零第三方库
// ════════════════════════════════════════════════════════════════════

public static class DataDir
{
    public const string EnvKey = "AIACE_DATA";
    public const double MinFreeGB = 5.0;

    public static readonly string[] SubDirs = new string[]
    {
        "open-webui", "documents", "output", "knowledge", "database"
    };

    // ── 路径 ───────────────────────────────────────────────────

    public static string InstallRoot()
    {
        string r = null;
        try { r = Bootstrap.FindRoot(); }
        catch { }
        if (string.IsNullOrEmpty(r)) r = Setup.ExeDir();
        return r == null ? "" : r.TrimEnd('\\');
    }

    public static string EnvPath(string root)
    {
        return Path.Combine(Path.Combine(root, "compose"), ".env");
    }

    public static string StatePath(string root)
    {
        return Path.Combine(Path.Combine(root, "system"), "install-state.json");
    }

    public static string ComposePath(string root)
    {
        return Path.Combine(Path.Combine(root, "compose"), "docker-compose.yml");
    }

    // 是否落在 C 盘（C: 只放系统，程序/数据/镜像都不放）
    public static bool OnCDrive(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return false;
            string root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return false;
            return string.Equals(root.TrimEnd('\\'), "C:", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // 第一个可用的非 C: 固定盘：D: 优先，其次按盘符顺序
    public static string FirstFixedNonC()
    {
        try
        {
            DriveInfo[] all = DriveInfo.GetDrives();
            for (int i = 0; i < all.Length; i++)
            {
                DriveInfo d = all[i];
                if (d.DriveType != DriveType.Fixed) continue;
                if (!d.IsReady) continue;
                if (d.Name.StartsWith("C:", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(d.Name, "D:\\", StringComparison.OrdinalIgnoreCase)) return "D:\\";
            }
            for (int i = 0; i < all.Length; i++)
            {
                DriveInfo d = all[i];
                if (d.DriveType != DriveType.Fixed) continue;
                if (!d.IsReady) continue;
                if (d.Name.StartsWith("C:", StringComparison.OrdinalIgnoreCase)) continue;
                return d.Name;
            }
        }
        catch { }
        return null;
    }

    // 默认数据目录：D:\AI-ACE-data；没有 D: 就退到第一个可用的非 C: 固定盘
    public static string DefaultRoot()
    {
        string drive = FirstFixedNonC();
        if (string.IsNullOrEmpty(drive)) return "D:\\AI-ACE-data";
        return Path.Combine(drive.TrimEnd('\\'), "AI-ACE-data");
    }

    // ── 读配置 ─────────────────────────────────────────────────

    static Dictionary<string, object> ReadJson(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            string json = File.ReadAllText(path);
            if (json == null || json.Trim().Length == 0) return null;
            return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
        }
        catch { return null; }
    }

    static string JsonString(Dictionary<string, object> d, string key)
    {
        if (d == null || key == null || !d.ContainsKey(key)) return "";
        object v = d[key];
        if (v == null) return "";
        string s = v as string;
        return s == null ? Convert.ToString(v) : s;
    }

    // 读 compose\.env 的 AIACE_DATA（兼容 KEY = value 与引号）
    public static string ReadEnv(string root)
    {
        try
        {
            string p = EnvPath(root);
            if (!File.Exists(p)) return "";
            string[] lines = File.ReadAllLines(p);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i] == null ? "" : lines[i].Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                if (!string.Equals(line.Substring(0, eq).Trim(), EnvKey, StringComparison.OrdinalIgnoreCase)) continue;
                string v = line.Substring(eq + 1).Trim().Trim('"');
                if (v.Length > 0) return v;
            }
        }
        catch { }
        return "";
    }

    // 只认已配置的值（install-state.json 的 dataDir，其次 .env），没有就返回空
    public static string Configured(string root)
    {
        string s = JsonString(ReadJson(StatePath(root)), "dataDir");
        if (!string.IsNullOrEmpty(s)) return s.TrimEnd('\\');
        s = ReadEnv(root);
        if (!string.IsNullOrEmpty(s)) return s.TrimEnd('\\');
        return "";
    }

    // 当前实际数据目录：已配置的优先；旧版把数据放在安装目录里则认安装目录
    public static string Current(string root)
    {
        string s = Configured(root);
        if (!string.IsNullOrEmpty(s)) return s;
        try
        {
            if (File.Exists(Path.Combine(Path.Combine(root, "open-webui"), "webui.db"))) return root.TrimEnd('\\');
        }
        catch { }
        return DefaultRoot();
    }

    // ── 校验 ───────────────────────────────────────────────────

    public static bool IsSameOrUnder(string child, string parent)
    {
        if (string.IsNullOrEmpty(child) || string.IsNullOrEmpty(parent)) return false;
        try
        {
            string c = Path.GetFullPath(child).TrimEnd('\\');
            string p = Path.GetFullPath(parent).TrimEnd('\\');
            if (c.Length == 0 || p.Length == 0) return false;
            if (string.Equals(c, p, StringComparison.OrdinalIgnoreCase)) return true;
            return (c + "\\").StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // 非 C: 盘、非可移动盘、非盘根、不在安装目录内（相等/子目录都拒绝）、可用 ≥ 5 GB
    public static bool IsValid(string input, string installRoot, out string dir, out string error)
    {
        dir = "";
        error = "";
        if (input == null || input.Trim().Length == 0)
        {
            error = "请填写数据目录，例如 D:\\AI-ACE-data。";
            return false;
        }
        string d = "";
        try { d = Path.GetFullPath(input.Trim()); }
        catch { error = "路径格式不对：" + input; return false; }
        d = d.TrimEnd('\\');
        if (d.Length <= 2)
        {
            error = "数据目录不能是磁盘根目录，请指定一个文件夹，例如 D:\\AI-ACE-data。";
            return false;
        }
        if (OnCDrive(d))
        {
            error = "数据与程序都不放 C 盘：数据目录不能用 C 盘，请改用 D:\\AI-ACE-data（或其他非 C 盘目录）。";
            return false;
        }
        DriveInfo di = null;
        try { di = Setup.DriveOf(d); }
        catch { di = null; }
        if (di == null)
        {
            error = "无法识别磁盘：" + d;
            return false;
        }
        if (di.DriveType == DriveType.Removable)
        {
            error = "数据目录不能放在可移动盘（" + di.Name + "），请改用本机硬盘，例如 D:\\AI-ACE-data。";
            return false;
        }
        if (IsSameOrUnder(d, installRoot))
        {
            error = "数据目录不能和安装目录相同，也不能是安装目录的子目录：" + d;
            return false;
        }
        double free = Math.Round(di.AvailableFreeSpace / 1073741824.0, 1);
        if (free < MinFreeGB)
        {
            error = "数据目录所在磁盘 " + di.Name + " 只剩 " + free.ToString("0.0") + " GB，至少需要 5 GB。";
            return false;
        }
        dir = d;
        return true;
    }

    // ── 写入 ───────────────────────────────────────────────────

    public static bool EnsureDirs(string dataDir)
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            for (int i = 0; i < SubDirs.Length; i++)
                Directory.CreateDirectory(Path.Combine(dataDir, SubDirs[i]));
            return true;
        }
        catch { return false; }
    }

    // 覆盖写 compose\.env：单行 AIACE_DATA=D:/AI-ACE-data（UTF-8 无 BOM）
    public static bool WriteEnv(string root, string dataDir, out string error)
    {
        error = "";
        try
        {
            string dir = Path.Combine(root, "compose");
            Directory.CreateDirectory(dir);
            string v = dataDir.Replace('\\', '/').TrimEnd('/');
            File.WriteAllText(Path.Combine(dir, ".env"), EnvKey + "=" + v + "\r\n", new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    // 迁移/回滚用：原样写回旧 .env 文本（null = 原来没有 .env，删掉）
    public static void RestoreEnv(string root, string oldText)
    {
        try
        {
            string p = EnvPath(root);
            if (oldText == null) { if (File.Exists(p)) File.Delete(p); }
            else File.WriteAllText(p, oldText, new UTF8Encoding(false));
        }
        catch { }
    }

    static string ReadEnvText(string root)
    {
        try
        {
            string p = EnvPath(root);
            return File.Exists(p) ? File.ReadAllText(p) : null;
        }
        catch { return null; }
    }

    // 只改 dataDir，保留 install-state.json 里已有的 mode / oldDir
    public static bool WriteState(string root, string dataDir, out string error)
    {
        error = "";
        try
        {
            Dictionary<string, object> d = ReadJson(StatePath(root));
            string mode = JsonString(d, "mode");
            if (mode.Length == 0) mode = "inplace";
            string oldDir = JsonString(d, "oldDir");
            Setup.WriteInstallState(root, mode, oldDir, dataDir);
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    // ── 统计 / 校验用量 ────────────────────────────────────────

    public static long CountFiles(string dir, out long bytes)
    {
        bytes = 0;
        long n = 0;
        if (string.IsNullOrEmpty(dir)) return 0;
        try
        {
            if (!Directory.Exists(dir)) return 0;
            string[] files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
            for (int i = 0; i < files.Length; i++)
            {
                n++;
                try { bytes += new FileInfo(files[i]).Length; }
                catch { }
            }
        }
        catch { }
        return n;
    }

    // 只统计 5 个数据子目录，与迁移口径一致（数据目录 == 安装根时不含 models / docker-images 等）
    public static long UsageBytes(string dataRoot, out long bytes)
    {
        bytes = 0; long n = 0;
        if (string.IsNullOrEmpty(dataRoot)) return 0;
        foreach (string sub in SubDirs)
        {
            long b = 0;
            try { n += CountFiles(Path.Combine(dataRoot, sub), out b); } catch { }
            bytes += b;
        }
        return n;
    }

    public static string SizeText(long bytes)
    {
        double mb = bytes / 1048576.0;
        if (mb >= 1024.0) return (mb / 1024.0).ToString("0.0") + " GB";
        return mb.ToString("0.0") + " MB";
    }

    // ── Docker Desktop 镜像盘（只读探测，不代改）────────────────

    public static string DockerDataFolder()
    {
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(appData)) return "";
            string p = Path.Combine(Path.Combine(appData, "Docker"), "settings-store.json");
            if (!File.Exists(p)) return "";
            Dictionary<string, object> d = ReadJson(p);
            string v = JsonString(d, "dataFolder");
            return v == null ? "" : v;
        }
        catch { return ""; }
    }

    // ── 迁移 ───────────────────────────────────────────────────

    static bool CopyData(string src, string dst, out int code, out string output)
    {
        string robocopy = "robocopy";
        try
        {
            string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (!string.IsNullOrEmpty(sys))
            {
                string p = Path.Combine(sys, "robocopy.exe");
                if (File.Exists(p)) robocopy = p;
            }
        }
        catch { }
        string args = "\"" + src.TrimEnd('\\') + "\" \"" + dst.TrimEnd('\\') + "\" /E /NFL /NDL /NJH /NP /R:1 /W:1";
        Process p2 = null;
        code = -1;
        output = "";
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = robocopy;
            psi.Arguments = args;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            p2 = Process.Start(psi);
            StringBuilder sb = new StringBuilder();
            p2.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
            {
                if (e.Data != null) { lock (sb) { sb.AppendLine(e.Data); } }
            };
            p2.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
            {
                if (e.Data != null) { lock (sb) { sb.AppendLine(e.Data); } }
            };
            p2.BeginOutputReadLine();
            p2.BeginErrorReadLine();
            if (!p2.WaitForExit(6 * 3600 * 1000)) { try { p2.Kill(); } catch { } }
            else { try { p2.WaitForExit(); } catch { } }
            lock (sb) { output = sb.ToString(); }
            if (p2.HasExited) code = p2.ExitCode;
        }
        catch (Exception ex) { output = ex.Message; code = -1; }
        finally { if (p2 != null) { try { p2.Dispose(); } catch { } } }
        return code >= 0 && code < 8;   // 0..7 都是成功码，1 = 有文件被复制
    }

    static string Short(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Trim().Replace("\r", "").Replace("\n", " ");
        return s.Length <= max ? s : s.Substring(0, max) + "…";
    }

    // 整体迁移：down（失败只告警）→ 逐个子目录 robocopy（只搬 SubDirs）→ 校验文件数/字节数
    //           → 改写 .env → up -d --no-build → 起不来就把 .env 写回旧值
    public static bool Migrate(string root, string from, string to, Action<string> log, out string error)
    {
        error = "";
        if (log == null) log = delegate(string s) { };
        if (string.IsNullOrEmpty(root)) { error = "找不到 AI-ACE 安装目录。"; return false; }

        string target; string verr;
        if (!IsValid(to, root, out target, out verr)) { error = verr; return false; }
        to = target;

        string old = from == null ? "" : from.TrimEnd('\\');
        if (old.Length > 0 && string.Equals(old, to, StringComparison.OrdinalIgnoreCase))
        {
            error = "新数据目录和当前数据目录相同：" + to;
            return false;
        }
        if (old.Length > 0 && IsSameOrUnder(old, to))
        {
            error = "新数据目录不能位于旧数据目录内部：" + to;
            return false;
        }

        string compose = ComposePath(root);
        string output;
        log("停止服务（docker compose down）…");
        bool downed = Bootstrap.RunDocker("compose -f \"" + compose + "\" down", 300000, out output, root);
        if (!downed) log("警告：compose down 没成功，继续迁移（容器可能仍占用旧数据）：" + Short(output, 200));

        if (!EnsureDirs(to)) { error = "无法创建数据目录：" + to; return false; }

        long fromCount = 0;
        long fromBytes = 0;
        List<string> subs = new List<string>();
        List<long> wantCount = new List<long>();
        List<long> wantBytes = new List<long>();
        bool hasFrom = old.Length > 0 && Directory.Exists(old);
        if (hasFrom)
        {
            // 只搬数据子目录；models / docker-images / services / desktop 等安装目录内容一律不碰
            for (int i = 0; i < SubDirs.Length; i++)
            {
                string d = Path.Combine(old, SubDirs[i]);
                if (!Directory.Exists(d)) continue;      // 旧目录没有这个子目录：跳过，不计入期望值
                long b;
                long n = CountFiles(d, out b);
                subs.Add(SubDirs[i]);
                wantCount.Add(n);
                wantBytes.Add(b);
                fromCount += n;
                fromBytes += b;
            }
        }

        if (hasFrom && subs.Count > 0)
        {
            log("复制数据：" + old + " → " + to + "（" + subs.Count + " 个数据子目录，" + fromCount + " 个文件，" + SizeText(fromBytes) + "）");
            for (int i = 0; i < subs.Count; i++)
            {
                int code;
                string copOut;
                if (!CopyData(Path.Combine(old, subs[i]), Path.Combine(to, subs[i]), out code, out copOut))
                {
                    error = "复制 " + subs[i] + " 失败（robocopy 退出码 " + code + "）：" + Short(copOut, 300);
                    return false;
                }
                log("  " + subs[i] + "：" + wantCount[i] + " 个文件 / " + SizeText(wantBytes[i]) + "，robocopy 退出码 " + code);
            }
            log("复制完成");
        }
        else if (hasFrom)
        {
            log("旧数据目录里没有数据子目录（" + string.Join(" ", SubDirs) + "），按空目录处理：" + old);
        }
        else
        {
            log("旧数据目录不存在或未配置，按空目录处理：" + (old.Length == 0 ? "（无）" : old));
        }

        long toCount = 0;
        long toBytes = 0;
        string bad = "";
        for (int i = 0; i < subs.Count; i++)
        {
            long b;
            long n = CountFiles(Path.Combine(to, subs[i]), out b);
            toCount += n;
            toBytes += b;
            if (n < wantCount[i] || b < wantBytes[i]) bad += (bad.Length == 0 ? "" : "、") + subs[i];
        }
        log("校验：" + subs.Count + " 个数据子目录，旧 " + fromCount + " 个文件 / " + SizeText(fromBytes) +
            "，新 " + toCount + " 个文件 / " + SizeText(toBytes));
        if (bad.Length > 0)
        {
            error = "校验没通过：" + bad + " 的文件数或字节数少于旧目录（旧 " + fromCount + " 个文件 " + SizeText(fromBytes) +
                    "，新 " + toCount + " 个文件 " + SizeText(toBytes) + "）。\r\n旧数据仍在原处：" + old;
            return false;
        }

        string oldEnv = ReadEnvText(root);
        string eerr;
        if (!WriteEnv(root, to, out eerr))
        {
            error = "写 compose\\.env 失败：" + eerr;
            return false;
        }
        log("已写 " + EnvPath(root) + "：" + EnvKey + "=" + to.Replace('\\', '/'));
        string serr;
        if (!WriteState(root, to, out serr)) log("警告：install-state.json 没写成功：" + serr);

        log("启动服务（docker compose up -d --no-build）…");
        bool up = Bootstrap.RunDocker("compose -f \"" + compose + "\" up -d --no-build", 900000, out output, root);
        bool ready = up && Bootstrap.WaitService(300, null);
        if (!ready)
        {
            log("服务没起来，回滚 compose\\.env 与 install-state.json…");
            RestoreEnv(root, oldEnv);
            string rerr;
            WriteState(root, old, out rerr);
            string upOut;
            Bootstrap.RunDocker("compose -f \"" + compose + "\" up -d --no-build", 900000, out upOut, root);
            error = "服务没有起来，已把 compose\\.env 写回旧值。" + Environment.NewLine + Environment.NewLine +
                    "旧数据仍在原处：" + (old.Length == 0 ? "（未配置）" : old);
            return false;
        }

        log("迁移完成：数据目录 = " + to + "（旧数据保留在 " + (old.Length == 0 ? "（无）" : old) + "，可自行删除）");
        return true;
    }
}

// ════════════════════════════════════════════════════════════════════
//  「数据位置」对话框：查看当前路径与占用、改路径、迁移
// ════════════════════════════════════════════════════════════════════
public class DataDirForm : Form
{
    public bool AutoStart = false;      // 启动检测里「一键搬走」直接开迁
    public bool Migrated = false;
    public string NewDir = "";

    readonly string root;
    readonly string current;
    readonly TextBox box;
    readonly Button btnBrowse;
    readonly FlatButton btnGo;
    readonly FlatButton btnCancel;
    readonly Label lblSize;
    readonly Label lblStatus;
    volatile bool running = false;

    public DataDirForm(string root, string current, string preset)
    {
        this.root = root == null ? "" : root;
        this.current = current == null ? "" : current.TrimEnd('\\');

        Text = "数据位置";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(720, 330);
        BackColor = Theme.PanelBg;
        ForeColor = Theme.Text;
        Font = Theme.Ui(9.5f);

        Label cap1 = new Label();
        cap1.Text = "当前数据位置：";
        cap1.ForeColor = Theme.Muted;
        cap1.SetBounds(20, 14, 680, 18);
        Controls.Add(cap1);

        Label lblCur = new Label();
        lblCur.Text = this.current.Length == 0 ? "（未配置）" : this.current;
        lblCur.ForeColor = Theme.Text;
        lblCur.Font = Theme.Mono(9f);
        lblCur.AutoEllipsis = true;
        lblCur.SetBounds(20, 34, 680, 20);
        Controls.Add(lblCur);

        lblSize = new Label();
        lblSize.Text = "正在统计占用…";
        lblSize.ForeColor = Theme.Muted;
        lblSize.SetBounds(20, 58, 680, 18);
        Controls.Add(lblSize);

        Label cap2 = new Label();
        cap2.Text = "新的数据位置（不能是 C 盘、可移动盘、磁盘根目录，也不能在安装目录内部）：";
        cap2.ForeColor = Theme.Muted;
        cap2.SetBounds(20, 90, 680, 18);
        Controls.Add(cap2);

        box = new TextBox();
        box.Text = preset == null ? "" : preset;
        box.BackColor = Theme.PanelBg2;
        box.ForeColor = Theme.Text;
        box.BorderStyle = BorderStyle.FixedSingle;
        box.SetBounds(20, 112, 566, 26);
        Controls.Add(box);

        btnBrowse = new Button();
        btnBrowse.Text = "浏览…";
        btnBrowse.SetBounds(594, 111, 106, 28);
        btnBrowse.FlatStyle = FlatStyle.Flat;
        btnBrowse.BackColor = Theme.BtnBg;
        btnBrowse.ForeColor = Theme.Text;
        btnBrowse.Click += delegate { Browse(); };
        Controls.Add(btnBrowse);

        Label hint = new Label();
        hint.Text = "迁移顺序：停止服务（compose down）→ 只复制 5 个数据子目录 → 校验文件数与总字节 → 改写 compose\\.env → 重新启动服务。" +
                    "旧数据不会删除；迁移期间请勿关闭本窗口。";
        hint.ForeColor = Theme.Muted;
        hint.SetBounds(20, 148, 680, 44);
        Controls.Add(hint);

        lblStatus = new Label();
        lblStatus.Text = "数据子目录：open-webui、documents、output、knowledge、database";
        lblStatus.ForeColor = Theme.Muted;
        lblStatus.SetBounds(20, 198, 680, 40);
        Controls.Add(lblStatus);

        btnGo = new FlatButton();
        btnGo.Primary = true;
        btnGo.Text = "开始迁移";
        btnGo.SetBounds(386, 272, 160, 36);
        btnGo.Click += delegate { Go(false); };
        Controls.Add(btnGo);

        btnCancel = new FlatButton();
        btnCancel.Text = "关闭";
        btnCancel.SetBounds(562, 272, 138, 36);
        btnCancel.Click += delegate { Close(); };
        Controls.Add(btnCancel);

        FormClosing += delegate(object s, FormClosingEventArgs e)
        {
            if (!running) return;
            e.Cancel = true;
            MessageBox.Show(this, "正在迁移数据，请等迁移结束后再关闭。", "数据位置",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ThreadPool.QueueUserWorkItem(delegate
        {
            long bytes = 0;
            long count = 0;
            bool has = current.Length > 0 && Directory.Exists(current);
            try
            {
                if (has) count = DataDir.UsageBytes(current, out bytes);
            }
            catch { }
            try
            {
                BeginInvoke(new Action(delegate
                {
                    lblSize.Text = has
                        ? "占用：" + DataDir.SizeText(bytes) + "（" + count + " 个文件）"
                        : "占用：目录还不存在（首次安装或还没用过）";
                }));
            }
            catch { }
        });
        if (AutoStart) BeginInvoke(new Action(delegate { Go(true); }));
    }

    void Browse()
    {
        using (FolderBrowserDialog d = new FolderBrowserDialog())
        {
            d.Description = "选择 AI-ACE 的数据目录（不能是 C 盘、可移动盘或磁盘根目录）";
            d.ShowNewFolderButton = true;
            string cur = box.Text == null ? "" : box.Text.Trim();
            if (cur.Length > 0 && Directory.Exists(cur)) d.SelectedPath = cur;
            if (d.ShowDialog(this) == DialogResult.OK) box.Text = d.SelectedPath;
        }
    }

    void Go(bool skipConfirm)
    {
        if (running) return;
        string target = (box.Text == null ? "" : box.Text).Trim().Trim('"').TrimEnd('\\');
        if (target.Length == 0)
        {
            MessageBox.Show(this, "请先填一个数据目录，例如 D:\\AI-ACE-data。", "数据位置",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        string err;
        string okDir;
        if (!DataDir.IsValid(target, root, out okDir, out err))
        {
            MessageBox.Show(this, err, "这个位置不能用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (current.Length > 0)
        {
            if (string.Equals(target, current, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "新位置和当前数据目录是同一个，不用迁移。", "数据位置",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (DataDir.IsSameOrUnder(target, current))
            {
                MessageBox.Show(this, "新位置不能是当前数据目录的子目录：" + Environment.NewLine + current, "数据位置",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (DataDir.IsSameOrUnder(current, target))
            {
                MessageBox.Show(this, "新位置不能包含当前数据目录，请换一个更具体的目录名：" + Environment.NewLine + current, "数据位置",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }
        if (!skipConfirm)
        {
            if (MessageBox.Show(this,
                "现在把数据从" + Environment.NewLine +
                (current.Length == 0 ? "（未配置）" : current) + Environment.NewLine +
                "迁移到" + Environment.NewLine + target + " 吗？" + Environment.NewLine + Environment.NewLine +
                "迁移期间服务会停止几分钟，旧数据不会删除。",
                "数据位置", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        }
        Run(target);
    }

    void Run(string target)
    {
        running = true;
        box.Enabled = false;
        btnBrowse.Enabled = false;
        btnGo.Enabled = false;
        btnGo.Text = "迁移中…";
        lblStatus.Text = "正在迁移，请勿关闭窗口…";
        Bootstrap.Log("数据位置：开始迁移 " + (current.Length == 0 ? "（未配置）" : current) + " → " + target);

        ThreadPool.QueueUserWorkItem(delegate
        {
            string err = "";
            bool ok = false;
            try
            {
                ok = DataDir.Migrate(root, current, target, delegate(string line)
                {
                    Bootstrap.Log("数据位置：" + line);
                    try { BeginInvoke(new Action(delegate { lblStatus.Text = line; })); }
                    catch { }
                }, out err);
            }
            catch (Exception ex)
            {
                ok = false;
                err = "迁移出错：" + ex.Message;
            }
            try { BeginInvoke(new Action(delegate { Done(ok, err, target); })); }
            catch { }
        });
    }

    void Done(bool ok, string err, string target)
    {
        running = false;
        btnGo.Text = "开始迁移";
        if (ok)
        {
            Migrated = true;
            NewDir = target;
            Bootstrap.Log("数据位置：迁移成功 " + target);
            MessageBox.Show(this,
                "迁移完成，数据已经搬到：" + Environment.NewLine + target + Environment.NewLine + Environment.NewLine +
                "compose\\.env 与 install-state.json 已指向新位置，服务已按新位置重启。" + Environment.NewLine +
                "旧数据没有删除，确认新位置一切正常后可以自行删掉旧目录。",
                "数据位置", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
            Close();
            return;
        }
        box.Enabled = true;
        btnBrowse.Enabled = true;
        btnGo.Enabled = true;
        lblStatus.Text = "迁移没有完成，数据仍在原处。";
        Bootstrap.Log("数据位置：迁移失败 | " + err);
        MessageBox.Show(this, string.IsNullOrEmpty(err) ? "迁移没有完成，数据仍在原处。" : err,
            "迁移未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}


