using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Forms;

public class BootResult
{
    public bool Ok = false;
    public bool Demo = false;
    public string Root = "";
    public string Error = "";
}

public static class Bootstrap
{
    public static bool DemoMode = false;
    public const string BaseUrl = "http://127.0.0.1:3000";

    public static readonly string[] RequiredImages = new string[]
    {
        "compose-ace-document-parser",
        "ace-term-extractor",
        "ace-rag-service",
        "ace-docx-generator",
        "ace-vision-service",
        "ace-dashboard"
    };

    public static string LogPath()
    {
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup.log");
    }

    // 启动日志：按行追加，时间戳前缀；超过 1MB 截断重写，避免无限增长
    public static void Log(string msg)
    {
        try
        {
            string p = LogPath();
            FileInfo fi = new FileInfo(p);
            if (fi.Exists && fi.Length > 1024 * 1024) File.WriteAllText(p, "");
            File.AppendAllText(p, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine, new UTF8Encoding(false));
        }
        catch { }
    }

    public static string DockerExe()
    {
        string p = @"C:\Program Files\Docker\Docker\resources\bin\docker.exe";
        if (File.Exists(p)) return p;
        return "docker";
    }

    public static string FindRoot()
    {
        string env = Environment.GetEnvironmentVariable("AI_ACE_ROOT");
        if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(Path.Combine(env, "compose"), "docker-compose.yml"))) return env;
        string dir = Path.GetDirectoryName(Application.ExecutablePath);
        for (int i = 0; i < 6 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(Path.Combine(dir, "compose"), "docker-compose.yml"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        string fallback = @"G:\AI-ACE";
        if (File.Exists(Path.Combine(Path.Combine(fallback, "compose"), "docker-compose.yml"))) return fallback;
        return null;
    }

    public static bool RunDocker(string args, int timeoutMs, out string output)
    {
        return RunDocker(args, timeoutMs, out output, null);
    }

    public static bool RunDocker(string args, int timeoutMs, out string output, string workDir)
    {
        output = "";
        Process p = null;
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = DockerExe();
            psi.Arguments = args;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            if (!string.IsNullOrEmpty(workDir)) psi.WorkingDirectory = workDir;
            string dockerDir = Path.GetDirectoryName(psi.FileName);
            if (!string.IsNullOrEmpty(dockerDir))
            {
                string oldPath = psi.EnvironmentVariables["PATH"];
                if (oldPath == null) oldPath = Environment.GetEnvironmentVariable("PATH");
                psi.EnvironmentVariables["PATH"] = dockerDir + ";" + oldPath;
            }
            p = Process.Start(psi);
            StringBuilder sb = new StringBuilder();
            p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
            {
                if (e.Data != null) { lock (sb) { sb.AppendLine(e.Data); } }
            };
            p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
            {
                if (e.Data != null) { lock (sb) { sb.AppendLine(e.Data); } }
            };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            bool exited = p.WaitForExit(timeoutMs);
            if (!exited)
            {
                try { p.Kill(); } catch { }
            }
            if (exited) { try { p.WaitForExit(); } catch { } }
            lock (sb) { output = sb.ToString(); }
            if (!exited)
            {
                output += Environment.NewLine + "(命令超时)";
                return false;
            }
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            output = ex.Message;
            return false;
        }
        finally
        {
            if (p != null) { try { p.Dispose(); } catch { } }
        }
    }

    public static bool DockerReady()
    {
        string output;
        return RunDocker("version --format \"{{.Server.Version}}\"", 20000, out output);
    }

    public static void StartDockerDesktop()
    {
        try
        {
            string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Docker\Docker\Docker Desktop.exe");
            if (File.Exists(exe)) Process.Start(exe);
        }
        catch { }
    }

    // 镜像存在性检查用的原始命令（命令名是 docker.exe，参数从这里起）：
    //   docker images --format "{{.Repository}}:{{.Tag}}"
    public static List<string> ListImages()
    {
        List<string> list = new List<string>();
        string output;
        if (!RunDocker("images --format \"{{.Repository}}:{{.Tag}}\"", 60000, out output)) return list;
        string[] lines = output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < lines.Length; i++)
        {
            string s = lines[i].Trim();
            if (s.Length > 0) list.Add(s);
        }
        return list;
    }

    static bool MatchImage(string line, string name)
    {
        string repo = line.Trim();
        int c = repo.LastIndexOf(':');
        if (c > 0) repo = repo.Substring(0, c);
        if (repo.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
        if (repo.EndsWith("-" + name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static List<string> MissingImages()
    {
        List<string> images = ListImages();
        List<string> missing = new List<string>();
        for (int i = 0; i < RequiredImages.Length; i++)
        {
            bool found = false;
            for (int j = 0; j < images.Count; j++)
            {
                if (MatchImage(images[j], RequiredImages[i])) { found = true; break; }
            }
            if (!found) missing.Add(RequiredImages[i]);
        }
        return missing;
    }

    public static bool PingUrl(string url, int ms)
    {
        try
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Proxy = null;
            req.Timeout = ms;
            req.ReadWriteTimeout = ms;
            req.KeepAlive = false;
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            {
                int code = (int)resp.StatusCode;
                return code >= 200 && code < 400;
            }
        }
        catch { return false; }
    }

    public static bool WaitService(int waitSeconds, Action<string> progress)
    {
        for (int i = 0; i <= waitSeconds; i += 2)
        {
            if (PingUrl(BaseUrl + "/api/config", 2500)) return true;
            if (progress != null) progress("等待界面就绪… " + i + " 秒");
            Thread.Sleep(2000);
        }
        return false;
    }

    // 启动服务：docker compose -f "<root>\compose\docker-compose.yml" up -d --no-build
    public static bool EnsureStack(string root, Action<string> progress)
    {
        if (PingUrl(BaseUrl + "/api/config", 2500)) return true;
        if (progress != null) progress("正在拉起 AI-ACE 服务（docker compose up -d --no-build）…");
        string output;
        string compose = Path.Combine(Path.Combine(root, "compose"), "docker-compose.yml");
        bool started = RunDocker("compose -f \"" + compose + "\" up -d --no-build", 600000, out output, root);
        Log("compose up -d --no-build: " + (started ? "ok" : "failed") + " | " + Tail(output, 300));
        bool ready = WaitService(300, progress);
        Log("service ready after compose up: " + ready);
        return ready;
    }

    public static void Run(SplashForm ui)
    {
        BootResult res = new BootResult();
        ui.Result = res;
        try
        {
            ui.SetStep("检查环境", "正在查找 AI-ACE 目录…");
            Log("== boot ==");
            string root = FindRoot();
            if (root == null)
            {
                Log("root: NOT FOUND");
                res.Error = "找不到 AI-ACE 目录（compose\\docker-compose.yml）。" + Environment.NewLine +
                            "请把 AI-ACE 文件夹放在本程序上两级的同一磁盘，或在本程序目录新建 ai-ace-root.txt 写完整路径。";
                ui.FailAndStay(res.Error);
                return;
            }
            res.Root = root;
            Log("root: " + root);
            ui.SetDetail("AI-ACE 目录：" + root);

            ui.SetStep("启动 Docker", "正在检查 Docker 是否运行…");
            if (!DockerReady())
            {
                ui.SetDetail("Docker 未运行，正在尝试启动 Docker Desktop…");
                StartDockerDesktop();
                bool up = false;
                for (int i = 0; i < 150 && !up; i += 3)
                {
                    ui.SetDetail("等待 Docker 就绪… " + i + " 秒");
                    Thread.Sleep(3000);
                    up = DockerReady();
                }
                if (!up)
                {
                    Log("docker: not ready after 150s");
                    res.Error = "Docker 没有启动起来。" + Environment.NewLine +
                                "请先手动打开 Docker Desktop，等它显示 Running 后再双击 AI-ACE.exe。";
                    ui.FailAndStay(res.Error);
                    return;
                }
            }

            ui.SetStep("检查镜像", "正在读取本地镜像列表…");
            Log("docker: ready");
            List<string> missing = MissingImages();
            Log("missing images (" + missing.Count + "): " + string.Join(", ", missing.ToArray()));
            int rounds = 0;
            while (missing.Count > 0 && rounds < 5)
            {
                rounds++;
                int choice = ui.AskMissing(missing);
                if (choice == 2) { res.Ok = false; ui.CloseNow(); return; }
                if (choice == 1) { res.Demo = true; break; }
                string tar = ui.PickTar();
                if (tar == null) continue;
                ui.SetStep("导入镜像", "正在导入 " + Path.GetFileName(tar) + "（可能要几分钟）…");
                string output;
                // tar 导入：docker load -i "<tar 路径>"（长超时，镜像大时要几分钟）
                bool loaded = RunDocker("load -i \"" + tar + "\"", 3600000, out output);
                Log("docker load -i \"" + tar + "\": " + (loaded ? "ok" : "failed") + " | " + Tail(output, 300));
                if (!loaded) ui.ShowInfo("导入失败：" + Environment.NewLine + Tail(output, 600) + Environment.NewLine + Environment.NewLine + "可以换一个 tar 再试，或点「以演示模式打开」。");
                ui.SetStep("检查镜像", "重新检查镜像…");
                missing = MissingImages();
            }
            if (missing.Count > 0 && !res.Demo)
            {
                res.Error = "仍然缺少镜像：" + string.Join("、", missing.ToArray()) + Environment.NewLine +
                            "请换一个完整的 tar 包重试，或选择「以演示模式打开」。";
                ui.FailAndStay(res.Error);
                return;
            }

            if (res.Demo)
            {
                Log("demo mode: service checks skipped");
                DemoMode = true;
                ui.SetStep("演示模式", "跳过服务检查，直接打开界面");
                res.Ok = true;
                ui.FinishOk();
                return;
            }

            ui.SetStep("启动服务", "正在启动 AI-ACE 服务…");
            bool ready = EnsureStack(root, delegate(string m) { ui.SetDetail(m); });
            if (!ready)
            {
                string err = "服务没有启动成功（已等待 5 分钟）。" + Environment.NewLine +
                             "请确认 Docker Desktop 正在运行；也可以先打开界面，稍后在设置里检查服务地址。";
                ui.SetStep("服务未就绪", "");
                int c = ui.AskRetryOrClose(err);
                if (c != 0)
                {
                    res.Error = err;
                    ui.CloseNow();
                    return;
                }
            }
            if (ready)
            {
                try
                {
                    ui.SetStep("安全自检", "正在核对端口暴露面与联网隔离规则…");
                    NetGuardResult guard = NetGuard.SecurityCheck();
                    Log("安全自检：" + guard.Summary);
                    Log("安全自检明细：" + guard.Detail.Replace("\r", "").Replace("\n", " | "));
                    ui.SetDetail(guard.Summary);
                }
                catch (Exception gex) { Log("安全自检：跳过（" + gex.Message + "）"); }
            }
            Log("ready: opening main window");
            res.Ok = true;
            ui.SetStep("界面就绪", "服务已就绪，正在打开窗口…");
            ui.FinishOk();
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.ToString());
            res.Error = "启动过程出错：" + ex.Message;
            DemoMode = false;
            ui.FailAndStay(res.Error);
        }
    }

    static string Tail(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Trim();
        if (s.Length <= max) return s;
        return "…" + s.Substring(s.Length - max);
    }
}

public class MissingImagesForm : Form
{
    public int Choice = 2;

    public MissingImagesForm(List<string> missing)
    {
        Text = "缺少基础镜像";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(600, 300);
        BackColor = Theme.PanelBg;
        ForeColor = Theme.Text;
        Font = Theme.Ui(9.5f);
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        Label head = new Label();
        head.Text = "这台电脑上还缺 " + missing.Count + " 个基础镜像：";
        head.Font = Theme.Ui(11f, FontStyle.Bold);
        head.ForeColor = Theme.Text;
        head.AutoSize = false;
        head.SetBounds(24, 22, 552, 24);
        Controls.Add(head);

        Label list = new Label();
        list.Text = string.Join(Environment.NewLine, missing.ToArray());
        list.Font = Theme.Mono(9f);
        list.ForeColor = Theme.Accent;
        list.AutoSize = false;
        list.SetBounds(24, 54, 552, 96);
        Controls.Add(list);

        Label hint = new Label();
        hint.Text = "点「选择 tar 导入…」选中你拷过来的镜像包（*.tar），程序会自动 docker load，导入完接着启动。" + Environment.NewLine +
                    "也可以点「以演示模式打开」先看界面（不启动服务）。";
        hint.ForeColor = Theme.Muted;
        hint.AutoSize = false;
        hint.SetBounds(24, 158, 552, 60);
        Controls.Add(hint);

        FlatButton btnImport = new FlatButton();
        btnImport.Text = "选择 tar 导入…";
        btnImport.Primary = true;
        btnImport.SetBounds(24, 240, 150, 34);
        btnImport.Click += delegate { Choice = 0; DialogResult = DialogResult.OK; Close(); };
        Controls.Add(btnImport);

        FlatButton btnDemo = new FlatButton();
        btnDemo.Text = "以演示模式打开";
        btnDemo.SetBounds(186, 240, 150, 34);
        btnDemo.Click += delegate { Choice = 1; DialogResult = DialogResult.OK; Close(); };
        Controls.Add(btnDemo);

        FlatButton btnClose = new FlatButton();
        btnClose.Text = "关闭";
        btnClose.SetBounds(348, 240, 100, 34);
        btnClose.Click += delegate { Choice = 2; DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(btnClose);

        AcceptButton = btnImport;
        CancelButton = btnClose;
    }
}

public class SplashForm : Form
{
    public BootResult Result = new BootResult();
    Label lblStep;
    Label lblDetail;
    Label lblErr;
    Panel track;
    Panel glow;
    System.Windows.Forms.Timer anim;
    int animX = 0;
    FlatButton btnClose;
    FlatButton btnOpen;
    Thread worker;

    public SplashForm()
    {
        Text = "AI-ACE 正在启动";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(560, 300);
        BackColor = Theme.PanelBg;
        ForeColor = Theme.Text;
        ShowInTaskbar = true;
        TopMost = true;
        DoubleBuffered = true;
        try
        {
            Icon = AppIcon.LoadAppIcon();
        }
        catch { Icon = SystemIcons.Application; }

        Panel bar = new Panel();
        bar.SetBounds(0, 0, 560, 3);
        bar.BackColor = Theme.Accent;
        Controls.Add(bar);

        Label title = new Label();
        title.Text = "AI-ACE";
        title.Font = Theme.Ui(20f, FontStyle.Bold);
        title.ForeColor = Theme.Accent;
        title.AutoSize = true;
        title.Location = new Point(28, 24);
        Controls.Add(title);

        Label sub = new Label();
        sub.Text = "桌面客户端 · 正在启动";
        sub.Font = Theme.Ui(9f);
        sub.ForeColor = Theme.Muted;
        sub.AutoSize = true;
        sub.Location = new Point(30, 62);
        Controls.Add(sub);

        lblStep = new Label();
        lblStep.Text = "准备中…";
        lblStep.Font = Theme.Ui(12.5f, FontStyle.Bold);
        lblStep.ForeColor = Theme.Text;
        lblStep.AutoSize = false;
        lblStep.SetBounds(28, 106, 504, 26);
        Controls.Add(lblStep);

        lblDetail = new Label();
        lblDetail.Text = "";
        lblDetail.Font = Theme.Ui(9f);
        lblDetail.ForeColor = Theme.Muted;
        lblDetail.AutoSize = false;
        lblDetail.AutoEllipsis = true;
        lblDetail.SetBounds(28, 136, 504, 40);
        Controls.Add(lblDetail);

        track = new Panel();
        track.SetBounds(28, 190, 504, 4);
        track.BackColor = Theme.Border;
        Controls.Add(track);

        glow = new Panel();
        glow.SetBounds(0, 0, 120, 4);
        glow.BackColor = Theme.Accent;
        track.Controls.Add(glow);

        lblErr = new Label();
        lblErr.Font = Theme.Ui(9f);
        lblErr.ForeColor = Theme.Danger;
        lblErr.AutoSize = false;
        lblErr.SetBounds(28, 200, 504, 54);
        lblErr.Visible = false;
        Controls.Add(lblErr);

        btnClose = new FlatButton();
        btnClose.Text = "关闭";
        btnClose.SetBounds(444, 254, 88, 30);
        btnClose.Visible = false;
        btnClose.Click += delegate { Close(); };
        Controls.Add(btnClose);

        btnOpen = new FlatButton();
        btnOpen.Text = "仍然打开界面";
        btnOpen.Primary = true;
        btnOpen.SetBounds(300, 254, 132, 30);
        btnOpen.Visible = false;
        Controls.Add(btnOpen);

        anim = new System.Windows.Forms.Timer();
        anim.Interval = 30;
        anim.Tick += delegate
        {
            animX += 8;
            if (animX > 384) animX = 0;
            glow.Left = animX;
        };
        anim.Start();

        Shown += delegate
        {
            worker = new Thread(new ThreadStart(BootThread));
            worker.IsBackground = true;
            worker.Start();
        };
        FormClosed += delegate { try { anim.Stop(); } catch { } };
    }

    void BootThread()
    {
        Bootstrap.Run(this);
    }

    void OnUi(Action a)
    {
        try
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired) Invoke(a);
            else a();
        }
        catch { }
    }

    public void SetStep(string step, string detail)
    {
        OnUi(delegate
        {
            lblStep.Text = step;
            lblStep.ForeColor = Theme.Text;
            if (detail != null) lblDetail.Text = detail;
        });
    }

    public void SetDetail(string detail)
    {
        OnUi(delegate { lblDetail.Text = detail; });
    }

    public int AskMissing(List<string> missing)
    {
        int picked = 2;
        OnUi(delegate
        {
            using (MissingImagesForm f = new MissingImagesForm(missing))
            {
                f.ShowDialog(this);
                picked = f.Choice;
            }
        });
        return picked;
    }

    public string PickTar()
    {
        string picked = null;
        OnUi(delegate
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "选择镜像 tar 文件";
                d.Filter = "镜像包 (*.tar)|*.tar|所有文件 (*.*)|*.*";
                d.CheckFileExists = true;
                d.Multiselect = false;
                if (d.ShowDialog(this) == DialogResult.OK) picked = d.FileName;
            }
        });
        return picked;
    }

    public void ShowInfo(string text)
    {
        OnUi(delegate { MessageBox.Show(this, text, "导入镜像", MessageBoxButtons.OK, MessageBoxIcon.Warning); });
    }

    public void FinishOk()
    {
        OnUi(delegate
        {
            try { anim.Stop(); } catch { }
            lblStep.Text = "界面就绪";
            lblDetail.Text = "正在打开窗口…";
            Close();
        });
    }

    public void CloseNow()
    {
        OnUi(delegate { try { anim.Stop(); } catch { } Close(); });
    }

    public void FailAndStay(string err)
    {
        OnUi(delegate
        {
            try { anim.Stop(); } catch { }
            lblStep.Text = "启动失败";
            lblStep.ForeColor = Theme.Danger;
            lblDetail.Text = "";
            lblErr.Text = err;
            lblErr.Visible = true;
            btnClose.Visible = true;
            btnOpen.Visible = false;
        });
    }

    public int AskRetryOrClose(string err)
    {
        int choice = 1;
        OnUi(delegate
        {
            try { anim.Stop(); } catch { }
            lblStep.Text = "服务未就绪";
            lblStep.ForeColor = Theme.Danger;
            lblDetail.Text = "";
            lblErr.Text = err;
            lblErr.Visible = true;
            btnClose.Visible = true;
            btnOpen.Visible = true;
            bool decided = false;
            EventHandler onOpen = delegate(object s, EventArgs e) { choice = 0; decided = true; btnOpen.Visible = false; btnClose.Visible = false; };
            btnOpen.Click += onOpen;
            while (!decided && !IsDisposed)
            {
                Application.DoEvents();
                Thread.Sleep(40);
            }
            btnOpen.Click -= onOpen;
        });
        return choice;
    }
}
