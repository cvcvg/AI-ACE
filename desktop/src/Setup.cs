using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// ════════════════════════════════════════════════════════════════════
//  一键部署：环境探测 / 安装引擎 / 向导窗口
//  约定：exe 同级 system\install-state.json 不存在 = 首次运行 = 部署向导
//        exe 同级 system\install-state.json 存在 = 已部署 = 原启动器
//  依赖：.NET Framework 4.x + WinForms，零第三方库
// ════════════════════════════════════════════════════════════════════

public class EnvReport
{
    public bool Ok = false;
    public string Error = "";
    public string Root = "";
    public string DriveType = "";
    public bool DockerDesktop = false;
    public bool DockerCli = false;
    public bool DockerReady = false;
    public List<string> ImagesMissing = new List<string>();
    public double FreeSpaceGB = 0;
    public bool NvidiaSmi = false;
    public bool Deployed = false;
    public string InstallStatePath = "";
}

public static class Setup
{
    public const double MinFreeGB = 20.0;      // 第 1／2 步的磁盘下限
    public const int DockerWaitSeconds = 180;  // 拉起引擎最多等 180 秒
    public const int HealthWaitSeconds = 300;  // 健康检查最多 300 秒
    public const int RootProbeSeconds = 120;   // 健康检查失败后再探根路径 /
    public const int TarTimeoutMs = 3600000;   // 单条 docker load 的超时

    // 向导完成后由 Program.Main 读取：复制安装时用它把主界面指到新目录
    public static string LastInstallDir = "";

    // ── 路径 ───────────────────────────────────────────────────

    public static string ExeDir()
    {
        string d = null;
        try { d = Path.GetDirectoryName(Application.ExecutablePath); }
        catch { }
        if (string.IsNullOrEmpty(d)) d = AppDomain.CurrentDomain.BaseDirectory;
        return d.TrimEnd('\\');
    }

    // exe 同级的 system\install-state.json —— 首次运行判定的唯一凭据
    public static string InstallStatePath()
    {
        return Path.Combine(Path.Combine(ExeDir(), "system"), "install-state.json");
    }

    public static bool HasGguf(string dir)
    {
        try { return Directory.Exists(dir) && Directory.GetFiles(dir, "*.gguf").Length > 0; }
        catch { return false; }
    }

    // 完整包 = compose\docker-compose.yml + models\qwen3-4b 下有 .gguf
    public static bool IsCompletePackage(string dir)
    {
        if (string.IsNullOrEmpty(dir)) return false;
        if (!File.Exists(Path.Combine(Path.Combine(dir, "compose"), "docker-compose.yml"))) return false;
        return HasGguf(Path.Combine(Path.Combine(dir, "models"), "qwen3-4b"));
    }

    // 从 exe 所在目录向上最多 6 级，找带 compose\docker-compose.yml 的目录
    public static string FindPackageRoot()
    {
        string dir = ExeDir();
        for (int i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
        {
            if (File.Exists(Path.Combine(Path.Combine(dir, "compose"), "docker-compose.yml"))) return dir;
            DirectoryInfo p = Directory.GetParent(dir);
            dir = p == null ? null : p.FullName.TrimEnd('\\');
        }
        return null;
    }

    // 已部署 = install-state.json 在 + compose 在 + models\qwen3-4b 有 .gguf
    public static bool IsDeployed()
    {
        try
        {
            if (!File.Exists(InstallStatePath())) return false;
            string root = FindPackageRoot();
            if (root == null) return false;
            return IsCompletePackage(root);
        }
        catch { return false; }
    }

    // ── 基础探测 ───────────────────────────────────────────────

    public static string DockerDesktopPath()
    {
        string pf = null;
        try { pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles); }
        catch { }
        if (string.IsNullOrEmpty(pf)) pf = @"C:\Program Files";
        return Path.Combine(Path.Combine(Path.Combine(pf, "Docker"), "Docker"), "Docker Desktop.exe");
    }

    public static bool HasDockerDesktop()
    {
        try { return File.Exists(DockerDesktopPath()); }
        catch { return false; }
    }

    public static string WhereFile(string fileName)
    {
        try
        {
            string path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(path)) return null;
            string[] parts = path.Split(';');
            for (int i = 0; i < parts.Length; i++)
            {
                string d = parts[i].Trim().Trim('"');
                if (d.Length == 0) continue;
                try
                {
                    string p = Path.Combine(d, fileName);
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    public static bool HasDockerCli()
    {
        try
        {
            string exe = Bootstrap.DockerExe();
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe)) return true;
            return WhereFile("docker.exe") != null;
        }
        catch { return false; }
    }

    public static bool HasNvidiaSmi()
    {
        string exe = null;
        try
        {
            string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (!string.IsNullOrEmpty(sys))
            {
                string p = Path.Combine(sys, "nvidia-smi.exe");
                if (File.Exists(p)) exe = p;
            }
        }
        catch { }
        if (exe == null) exe = WhereFile("nvidia-smi.exe");
        if (exe == null) return false;
        string output;
        return RunProcess(exe, "-L", 15000, out output, null) == 0;
    }

    public static string DriveTypeName(string path)
    {
        try
        {
            DriveInfo di = DriveOf(path);
            if (di == null) return "Unknown";
            return di.DriveType.ToString();   // Fixed / Removable / Network / CDRom
        }
        catch { return "Unknown"; }
    }

    public static DriveInfo DriveOf(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return null;
            return new DriveInfo(root);
        }
        catch { return null; }
    }

    public static double FreeSpaceGB(string path)
    {
        try
        {
            DriveInfo di = DriveOf(path);
            if (di == null) return 0;
            return Math.Round(di.AvailableFreeSpace / 1073741824.0, 1);
        }
        catch { return 0; }
    }

    // ── 诊断开关：AI-ACE.exe --probe <输出文件>（无窗口，退出码 0）───

    public static EnvReport ProbeEnvironment()
    {
        EnvReport e = new EnvReport();
        e.Root = FindPackageRoot();
        if (string.IsNullOrEmpty(e.Root)) e.Root = ExeDir();
        e.InstallStatePath = InstallStatePath();
        e.DriveType = DriveTypeName(e.Root);
        e.DockerDesktop = HasDockerDesktop();
        e.DockerCli = HasDockerCli();
        e.DockerReady = e.DockerCli && Bootstrap.DockerReady();
        if (e.DockerReady) e.ImagesMissing = Bootstrap.MissingImages();
        else e.ImagesMissing = new List<string>(Bootstrap.RequiredImages);
        e.FreeSpaceGB = FreeSpaceGB(e.Root);
        e.NvidiaSmi = HasNvidiaSmi();
        e.Deployed = IsDeployed();
        return e;
    }

    public static int RunProbe(string outFile)
    {
        try
        {
            EnvReport e = ProbeEnvironment();
            StringBuilder sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"root\": ").Append(Q(e.Root)).Append(",\n");
            sb.Append("  \"driveType\": ").Append(Q(e.DriveType)).Append(",\n");
            sb.Append("  \"dockerDesktop\": ").Append(e.DockerDesktop ? "true" : "false").Append(",\n");
            sb.Append("  \"dockerCli\": ").Append(e.DockerCli ? "true" : "false").Append(",\n");
            sb.Append("  \"dockerReady\": ").Append(e.DockerReady ? "true" : "false").Append(",\n");
            sb.Append("  \"imagesMissing\": [");
            for (int i = 0; i < e.ImagesMissing.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Q(e.ImagesMissing[i]));
            }
            sb.Append("],\n");
            sb.Append("  \"freeSpaceGB\": ").Append(e.FreeSpaceGB.ToString("0.0", CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("  \"nvidiaSmi\": ").Append(e.NvidiaSmi ? "true" : "false").Append(",\n");
            sb.Append("  \"deployed\": ").Append(e.Deployed ? "true" : "false").Append(",\n");
            sb.Append("  \"installStatePath\": ").Append(Q(e.InstallStatePath)).Append("\n");
            sb.Append("}\n");

            string full = Path.GetFullPath(outFile);
            string dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(full, sb.ToString(), new UTF8Encoding(false));
            AppendLogFile("诊断运行（--probe）：root=" + e.Root + "，dockerReady=" + (e.DockerReady ? "true" : "false") + "，deployed=" + (e.Deployed ? "true" : "false"));
            return 0;
        }
        catch { return 1; }
    }

    static string Q(string s)
    {
        if (s == null) return "\"\"";
        StringBuilder sb = new StringBuilder("\"");
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"') sb.Append("\\\"");
            else if (c == '\\') sb.Append("\\\\");
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\t') sb.Append("\\t");
            else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
        }
        return sb.Append("\"").ToString();
    }

    // ════════════════════════════════════════════════════════════
    //  第 1 步：环境检查（Docker Desktop / 引擎 / 磁盘 / GPU）
    // ════════════════════════════════════════════════════════════
    public static EnvReport CheckEnvironment(Action<string> log, Action<string> detail)
    {
        if (log == null) log = delegate(string s) { };
        if (detail == null) detail = delegate(string s) { };
        EnvReport e = new EnvReport();
        e.Root = FindPackageRoot();
        if (string.IsNullOrEmpty(e.Root)) e.Root = ExeDir();
        e.InstallStatePath = InstallStatePath();

        // 1) Docker Desktop 是否安装（固定路径，或 PATH 上有 docker 命令）
        e.DockerDesktop = HasDockerDesktop();
        e.DockerCli = HasDockerCli();
        log("Docker Desktop：" + (e.DockerDesktop ? "已安装（" + DockerDesktopPath() + "）" : "未检测到 " + DockerDesktopPath()));
        log("docker 命令：" + (e.DockerCli ? Bootstrap.DockerExe() : "未找到"));
        if (!e.DockerDesktop && !e.DockerCli)
        {
            e.Error = "没有检测到 Docker Desktop，也没有找到 docker 命令。" + Environment.NewLine + Environment.NewLine +
                      "请先安装 Docker Desktop（需要它才能跑 AI-ACE 的容器），装好并启动一次后再运行本程序。" + Environment.NewLine +
                      "本程序不代装 Docker Desktop。";
            return e;
        }

        // 2) 引擎没启动就自动拉起，最多等 180 秒
        detail("正在检查 Docker 引擎…");
        e.DockerReady = Bootstrap.DockerReady();
        if (e.DockerReady) log("Docker 引擎：已就绪");
        else
        {
            log("Docker 引擎未运行，正在尝试启动 Docker Desktop…");
            detail("正在启动 Docker Desktop…");
            Bootstrap.StartDockerDesktop();
            for (int i = 0; i < DockerWaitSeconds; i += 3)
            {
                detail("等待 Docker 引擎就绪… " + i + " 秒");
                Thread.Sleep(3000);
                if (Bootstrap.DockerReady()) { e.DockerReady = true; break; }
            }
            log(e.DockerReady ? "Docker 引擎：已就绪" : "Docker 引擎：等了 180 秒仍未就绪");
        }
        if (!e.DockerReady)
        {
            e.Error = "Docker 引擎 180 秒内没有启动成功。" + Environment.NewLine + Environment.NewLine +
                      "请打开 Docker Desktop，等左下角显示 Running（或 Engine running）后，再运行本程序。";
            return e;
        }

        // 3) 磁盘剩余 ≥ 20 GB（此处看部署包所在盘；最终安装盘在第 2 步再校验一次）
        detail("正在检查磁盘空间…");
        e.FreeSpaceGB = FreeSpaceGB(e.Root);
        string drive = "";
        try { drive = Path.GetPathRoot(e.Root); } catch { }
        log("磁盘 " + drive + " 剩余：" + e.FreeSpaceGB.ToString("0.0", CultureInfo.InvariantCulture) + " GB（需要 ≥ 20 GB）");
        if (e.FreeSpaceGB < MinFreeGB)
        {
            e.Error = "磁盘空间不够： " + drive + " 只剩 " + e.FreeSpaceGB.ToString("0.0", CultureInfo.InvariantCulture) +
                      " GB，至少需要 20 GB。" + Environment.NewLine + Environment.NewLine + "请清理磁盘后再运行。";
            return e;
        }

        // 4) nvidia-smi（没显卡只警告不阻断，但第 5 步起容器很可能失败：compose 的三个模型服务都占用 nvidia GPU）
        detail("正在检查显卡驱动…");
        e.NvidiaSmi = HasNvidiaSmi();
        log("nvidia-smi：" + (e.NvidiaSmi ? "可用" : "不可用（没装 NVIDIA 驱动或没有 NVIDIA 显卡；模型容器需要 NVIDIA 显卡，第 5 步很可能起不来。这里只警告，不阻断安装）"));

        // 5) Docker Desktop 镜像盘（只提示，不代改；读不到就静默跳过）
        string dockerData = DataDir.DockerDataFolder();
        if (dockerData.Length > 0 && DataDir.OnCDrive(dockerData))
        {
            log("警告：Docker 镜像盘还在 C 盘（" + dockerData + "），镜像和容器会占掉大量系统盘空间。");
            log("改法：Docker Desktop → Settings → Resources → Advanced → Disk image location 改到 D 盘（本程序不代改）。");
        }
        else if (dockerData.Length > 0) log("Docker 镜像盘：" + dockerData);

        e.Ok = true;
        return e;
    }

    // ════════════════════════════════════════════════════════════
    //  第 2 步：选择安装位置
    // ════════════════════════════════════════════════════════════

    // 默认 D:\AI-ACE；exe 所在目录本身是完整包且不是可移动盘 → 默认就地安装
    public static string SuggestInstallDir(out bool inPlace)
    {
        inPlace = false;
        string exeDir = ExeDir();
        DriveInfo di = DriveOf(exeDir);
        bool removable = di != null && di.DriveType == DriveType.Removable;
        // 整个包在 C 盘时不建议就地安装（程序与数据都不放 C 盘）
        if (IsCompletePackage(exeDir) && !removable && !DataDir.OnCDrive(exeDir))
        {
            inPlace = true;
            return exeDir;
        }
        if (Directory.Exists("D:\\") && !DataDir.OnCDrive("D:\\")) return "D:\\AI-ACE";
        string drv = DataDir.FirstFixedNonC();
        if (drv.Length > 0) return Path.Combine(drv + "\\", "AI-ACE");
        return "D:\\AI-ACE";
    }

    public static bool ValidateInstallDir(string input, out string dir, out bool inPlace, out string error)
    {
        dir = "";
        inPlace = false;
        error = "";
        if (input == null || input.Trim().Length == 0)
        {
            error = "请填写安装位置，例如 D:\\AI-ACE。";
            return false;
        }
        string d = "";
        try { d = Path.GetFullPath(input.Trim()); }
        catch { error = "路径格式不对：" + input; return false; }
        d = d.TrimEnd('\\');
        if (d.Length <= 2)
        {
            error = "不能装到磁盘根目录，请指定一个文件夹，例如 D:\\AI-ACE。";
            return false;
        }
        string parent = "";
        try { parent = Path.GetDirectoryName(d); } catch { }
        if (string.IsNullOrEmpty(parent))
        {
            error = "路径格式不对：" + input;
            return false;
        }
        if (!Directory.Exists(parent))
        {
            try { Directory.CreateDirectory(parent); }
            catch { error = "无法创建上级目录：" + parent; return false; }
        }

        string exeDir = ExeDir();
        bool same = string.Equals(d, exeDir, StringComparison.OrdinalIgnoreCase);
        if (!same && IsUnder(d, exeDir))
        {
            error = "安装位置不能是程序所在目录的子目录（会把自己拷进自己）：" + d;
            return false;
        }

        // 程序与数据都不放 C 盘：C 盘留给系统
        if (DataDir.OnCDrive(d))
        {
            if (same)
            {
                error = "当前整个包放在 C 盘（" + exeDir + "），不能就地安装。" + Environment.NewLine +
                        "请把这个部署包整体拷到 D 盘（或其它非 C 盘）再双击运行，例如 D:\\AI-ACE。";
            }
            else
            {
                error = "数据与程序都不放 C 盘，请改用 D:\\AI-ACE 这类非 C 盘目录。" + Environment.NewLine +
                        "C 盘只留给系统和软件缓存，装到 C 盘会很快把系统盘塞满。";
            }
            return false;
        }

        DriveInfo di = DriveOf(d);
        if (di == null)
        {
            error = "无法识别磁盘：" + d;
            return false;
        }
        if (same)
        {
            // 就地安装：可移动盘一律禁止，必须复制到本机硬盘
            if (di.DriveType == DriveType.Removable)
            {
                error = "当前程序在可移动盘（" + di.Name + "）上，不能就地安装。" + Environment.NewLine +
                        "请换一个本机硬盘上的目录，例如 D:\\AI-ACE。";
                return false;
            }
            inPlace = true;
            dir = d;
            return true;
        }

        double free = Math.Round(di.AvailableFreeSpace / 1073741824.0, 1);
        if (free < MinFreeGB)
        {
            error = "目标磁盘 " + di.Name + " 只剩 " + free.ToString("0.0", CultureInfo.InvariantCulture) + " GB，至少需要 20 GB。";
            return false;
        }
        dir = d;
        return true;
    }

    // 全自动安装：先按默认建议目录校验，不合规就换成「同一块盘 + 时间戳」的新目录再校验
    public static bool ResolveAutoInstallDir(out string dir, out bool inPlace, out string error)
    {
        dir = "";
        inPlace = false;
        error = "";
        string suggestion = "";
        bool suggestInPlace = false;
        try { suggestion = SuggestInstallDir(out suggestInPlace); }
        catch (Exception ex) { error = "无法推断默认安装位置：" + ex.Message; }

        string d = ""; bool ip = false; string err = "";
        if (suggestion.Length > 0 && ValidateInstallDir(suggestion, out d, out ip, out err))
        {
            dir = d;
            inPlace = ip;
            return true;
        }
        string firstError = error.Length > 0 ? error : err;

        // 兜底：同一块盘上换一个带时间戳的新目录（不覆盖已有目录，也不和已有安装混淆）
        DriveInfo di = null;
        if (suggestion.Length > 0) { try { di = DriveOf(suggestion); } catch { di = null; } }
        if (di != null && di.Name.Length > 0)
        {
            string alt = Path.Combine(di.Name, "AI-ACE-" + DateTime.Now.ToString("yyyyMMdd-HHmm"));
            err = "";
            if (ValidateInstallDir(alt, out d, out ip, out err))
            {
                dir = d;
                inPlace = ip;
                return true;
            }
            firstError = err;
        }

        error = firstError.Length > 0 ? firstError : "无法自动确定安装位置，请检查磁盘空间与权限。";
        return false;
    }

    // 全自动数据目录：先默认目录，不合规就退到「安装位置所在盘」的 AI-ACE-data 再校验
    public static bool ResolveAutoDataDir(string installDir, out string dir, out string error)
    {
        dir = "";
        error = "";
        string def = "";
        string d = ""; string err = "";
        try { def = DataDir.DefaultRoot(); }
        catch (Exception ex) { err = "无法推断默认数据目录：" + ex.Message; }

        if (def.Length > 0 && DataDir.IsValid(def, installDir, out d, out err))
        {
            dir = d;
            return true;
        }
        string firstError = err;

        DriveInfo di = null;
        try { di = DriveOf(installDir); } catch { di = null; }
        if (di != null && di.Name.Length > 0)
        {
            string alt = Path.Combine(di.Name, "AI-ACE-data");
            if (!string.Equals(alt.TrimEnd('\\'), def.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                err = "";
                if (DataDir.IsValid(alt, installDir, out d, out err))
                {
                    dir = d;
                    return true;
                }
                firstError = err;
            }
        }

        error = firstError.Length > 0 ? firstError : "无法自动确定数据目录，请检查磁盘空间与权限。";
        return false;
    }

    static bool IsUnder(string child, string parent)
    {
        if (string.IsNullOrEmpty(child) || string.IsNullOrEmpty(parent)) return false;
        string c = child.TrimEnd('\\') + "\\";
        string p = parent.TrimEnd('\\') + "\\";
        return c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasOldData(string target)
    {
        try { return File.Exists(Path.Combine(Path.Combine(target, "open-webui"), "webui.db")); }
        catch { return false; }
    }

    // 复制整棵树：robocopy /E（0..7 都是成功码，1 = 有文件被复制）
    public static bool CopyTree(string src, string dst, Action<string> log)
    {
        if (log == null) log = delegate(string s) { };
        if (string.IsNullOrEmpty(src) || !Directory.Exists(src))
        {
            log("复制失败：源目录不存在 " + src);
            return false;
        }
        if (IsUnder(dst, src))
        {
            log("复制失败：目标目录在源目录内部（会把自己拷进自己）：" + dst);
            return false;
        }
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
        string args = "\"" + src.TrimEnd('\\') + "\" \"" + dst.TrimEnd('\\') + "\" /E /R:1 /W:1 /MT:16 /NFL /NDL /NJH /NJS /NP";
        log("robocopy " + src + " → " + dst);
        string output;
        int code = RunProcess(robocopy, args, 6 * 3600 * 1000, out output, null);
        if (code < 0 || code >= 8)
        {
            log("复制失败（robocopy 退出码 " + code + "）：" + Tail(output, 400));
            return false;
        }
        return true;
    }

    // ════════════════════════════════════════════════════════════
    //  第 3 步：逐个 docker load（已存在的镜像按 docker images 跳过）
    // ════════════════════════════════════════════════════════════
    // tar 源永远取部署包根（tarRoot）；包根没有 docker-images 时回退到安装目录
    public static bool LoadImages(string tarRoot, string installRoot, Action<string> log, Action<double> progress, out string error)
    {
        error = "";
        if (log == null) log = delegate(string s) { };
        string fromPack = Path.Combine(tarRoot, "docker-images");
        string fromInstall = Path.Combine(installRoot, "docker-images");
        string dir = fromPack;
        if (!Directory.Exists(dir)) dir = fromInstall;
        if (!Directory.Exists(dir))
        {
            error = "找不到镜像目录，已查找： " + fromPack + " 与 " + fromInstall + "（部署包不完整，请重新拷贝整个 AI-ACE 文件夹）";
            return false;
        }
        log("镜像来源：" + dir);
        string[] tars = Directory.GetFiles(dir, "*.tar");
        Array.Sort(tars, StringComparer.OrdinalIgnoreCase);
        if (tars.Length == 0)
        {
            error = "docker-images 里没有 *.tar 镜像包。";
            return false;
        }
        log("镜像包共 " + tars.Length + " 个，开始逐个载入…");
        List<string> existing = Bootstrap.ListImages();
        int loaded = 0;
        int skipped = 0;
        for (int i = 0; i < tars.Length; i++)
        {
            string name = Path.GetFileName(tars[i]);
            if (progress != null) progress((double)i / (double)tars.Length);

            // 已经载过的镜像（docker images 里能查到 tar 的 RepoTags）直接跳过
            List<string> tags = TarTags.RepoTags(tars[i]);
            if (tags.Count > 0 && TagsPresent(existing, tags))
            {
                skipped++;
                log("已存在，跳过：" + name + "（" + string.Join("、", tags.ToArray()) + "）");
                continue;
            }

            log("正在载入 " + (i + 1) + "/" + tars.Length + "：" + name);
            string output;
            // 单条 docker load 超时 3600000ms（大镜像要好几分钟）
            bool ok = Bootstrap.RunDocker("load -i \"" + tars[i] + "\"", TarTimeoutMs, out output);
            if (!ok)
            {
                error = "载入失败：" + name + Environment.NewLine + Tail(output, 600);
                return false;
            }
            loaded++;
            existing = Bootstrap.ListImages();
            if (progress != null) progress((double)(i + 1) / (double)tars.Length);
        }
        log("镜像载入完成：新载入 " + loaded + " 个，跳过 " + skipped + " 个。");
        if (progress != null) progress(1.0);
        return true;
    }

    static bool TagsPresent(List<string> images, List<string> tags)
    {
        for (int i = 0; i < tags.Count; i++)
        {
            string t = tags[i];
            bool found = false;
            for (int j = 0; j < images.Count; j++)
            {
                if (string.Equals(images[j], t, StringComparison.OrdinalIgnoreCase)) { found = true; break; }
            }
            if (!found) return false;
        }
        return tags.Count > 0;
    }

    // ════════════════════════════════════════════════════════════
    //  第 4 步：保留旧数据（webui.db / -wal / -shm / uploads）
    //  目标已有 webui.db 时：备份 → 旧目录改名 → 展开 → 放回
    //  就地安装：只备份，不改名、不展开
    // ════════════════════════════════════════════════════════════
    //  dataDir     新的数据目录（旧数据最终都落到这里）
    //  prevDataDir 之前配置过的数据目录（重装 / 换盘时把里面的数据也搬过来）
    public static string PreserveOldData(string sourceRoot, string target, bool inPlace, string dataDir, string prevDataDir, out string backupDir, Action<string> log)
    {
        if (log == null) log = delegate(string s) { };
        if (dataDir == null) dataDir = "";
        if (prevDataDir == null) prevDataDir = "";
        backupDir = "";
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string oldDir = "";
        string parent = "";
        try { parent = Path.GetDirectoryName(target); } catch { }
        if (string.IsNullOrEmpty(parent)) parent = target;

        bool hasOld = HasOldData(target);
        if (hasOld)
        {
            backupDir = Path.Combine(parent, "ai-ace-保留数据-" + stamp);
            string keepDir = Path.Combine(backupDir, "open-webui");
            Directory.CreateDirectory(keepDir);
            string[] files = new string[] { "webui.db", "webui.db-wal", "webui.db-shm" };
            for (int i = 0; i < files.Length; i++)
            {
                string src = Path.Combine(Path.Combine(target, "open-webui"), files[i]);
                if (!File.Exists(src)) continue;
                File.Copy(src, Path.Combine(keepDir, files[i]), true);
                log("已备份 open-webui\\" + files[i] + " → " + keepDir);
            }
            string uploads = Path.Combine(Path.Combine(target, "open-webui"), "uploads");
            if (Directory.Exists(uploads))
            {
                log("正在备份 uploads 目录…");
                CopyTree(uploads, Path.Combine(keepDir, "uploads"), null);
            }
            log("旧数据已备份到：" + backupDir);
        }
        else
        {
            log("目标目录没有旧的 open-webui\\webui.db，按全新部署处理（不保留旧数据）。");
        }

        if (inPlace)
        {
            log("就地安装：不改名、不展开（程序所在目录就是安装目录）。");
        }
        else
        {
            if (hasOld && Directory.Exists(target))
            {
                oldDir = target + "-旧版-" + stamp;
                Directory.Move(target, oldDir);
                log("旧目录已改名为：" + oldDir);
            }
            else if (!hasOld)
            {
                // 全新安装：第 2 步已把部署包展开进 target，这里不能改名、也不需要再复制
                log("按全新部署处理：第 2 步已展开部署包，这里不再改名、不再复制。");
            }
            if (hasOld)
            {
                // 第 2 步没有展开（要等旧目录改名），这里补上
                log("正在展开部署包到 " + target + " …");
                if (!CopyTree(sourceRoot, target, log)) throw new IOException("展开部署包失败：" + target);
                log("已复制到：" + target);
            }
        }

        // 旧数据统一放到新的数据目录（容器只看数据目录，不再看安装目录里的 open-webui）
        string dataOpen = dataDir.Length > 0 ? Path.Combine(dataDir, "open-webui") : "";
        if (hasOld && backupDir.Length > 0 && dataOpen.Length > 0)
        {
            string keepDir = Path.Combine(backupDir, "open-webui");
            Directory.CreateDirectory(dataOpen);
            string[] files = new string[] { "webui.db", "webui.db-wal", "webui.db-shm" };
            for (int i = 0; i < files.Length; i++)
            {
                string src = Path.Combine(keepDir, files[i]);
                if (!File.Exists(src)) continue;
                File.Copy(src, Path.Combine(dataOpen, files[i]), true);
            }
            string up = Path.Combine(keepDir, "uploads");
            if (Directory.Exists(up)) CopyTree(up, Path.Combine(dataOpen, "uploads"), null);
            log("已把 webui.db 与 uploads 放到数据目录：" + dataOpen);
        }
        else if (hasOld && dataOpen.Length == 0)
        {
            log("警告：没有可用的数据目录，旧数据只留在备份里：" + backupDir);
        }

        // 之前的数据目录（重装 / 换盘）里还有 webui.db：也搬到新数据目录
        if (dataOpen.Length > 0 && prevDataDir.Length > 0 &&
            !string.Equals(prevDataDir.TrimEnd('\\'), dataDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) &&
            HasOldData(prevDataDir))
        {
            log("发现旧数据目录：" + prevDataDir + "，正在把里面的数据搬到新数据目录…");
            Directory.CreateDirectory(dataOpen);
            CopyTree(Path.Combine(prevDataDir, "open-webui"), dataOpen, log);
            log("已从旧数据目录搬到：" + dataOpen + "（旧文件仍在 " + prevDataDir + "，可自行删除）");
        }
        return oldDir;
    }

    // ════════════════════════════════════════════════════════════
    //  第 5 步：compose up + 健康检查 + 状态文件 + 快捷方式 + 浏览器
    // ════════════════════════════════════════════════════════════
    public static bool StartStack(string root, Action<string> log, Action<string> detail)
    {
        if (log == null) log = delegate(string s) { };
        log("启动容器：docker compose -f compose\\docker-compose.yml up -d --no-build");
        // EnsureStack = compose up -d --no-build + 等 /api/config 最多 300 秒
        bool ok = Bootstrap.EnsureStack(root, detail);
        if (ok) { log("健康检查通过：" + Bootstrap.BaseUrl); return true; }

        // 失败再探根路径 /
        log("接口健康检查没通过，再探根路径 / …");
        for (int i = 0; i <= RootProbeSeconds; i += 3)
        {
            if (Bootstrap.PingUrl(Bootstrap.BaseUrl + "/", 3000))
            {
                log("根路径已响应：" + Bootstrap.BaseUrl + "/");
                return true;
            }
            if (detail != null) detail("等待服务响应… " + i + " 秒");
            Thread.Sleep(3000);
        }
        log("健康检查未通过（首次启动要加载模型，可能还要再等几分钟）。");
        return false;
    }

    public static string AppVersion()
    {
        try
        {
            Version v = Assembly.GetExecutingAssembly().GetName().Version;
            return v.Major + "." + v.Minor + "." + v.Build;
        }
        catch { return "1.0.0"; }
    }

    public static void WriteInstallState(string installDir, string mode, string oldDir)
    {
        WriteInstallState(installDir, mode, oldDir, null);
    }

    public static void WriteInstallState(string installDir, string mode, string oldDir, string dataDir)
    {
        string dir = Path.Combine(installDir, "system");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "install-state.json");
        StringBuilder sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"version\": ").Append(Q(AppVersion())).Append(",\n");
        sb.Append("  \"installedAt\": ").Append(Q(DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"))).Append(",\n");
        sb.Append("  \"installDir\": ").Append(Q(installDir)).Append(",\n");
        sb.Append("  \"mode\": ").Append(Q(mode)).Append(",\n");
        sb.Append("  \"oldDir\": ").Append(Q(oldDir == null ? "" : oldDir)).Append(",\n");
        sb.Append("  \"dataDir\": ").Append(Q(dataDir == null ? "" : dataDir)).Append("\n");
        sb.Append("}\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    // ── 向导步骤日志落盘：<exe 同级>\system\install.log ──────────────
    // 写日志失败一律吞掉：排障文件不能反过来让安装失败
    static readonly object installLogLock = new object();
    public const long InstallLogMaxBytes = 1048576;

    public static string InstallLogPath()
    {
        return Path.Combine(Path.Combine(ExeDir(), "system"), "install.log");
    }

    public static void AppendLogFile(string line)
    {
        try
        {
            lock (installLogLock)
            {
                string file = InstallLogPath();
                string dir = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                try
                {
                    FileInfo fi = new FileInfo(file);
                    if (fi.Exists && fi.Length > InstallLogMaxBytes) File.Delete(file);
                }
                catch { }
                File.AppendAllText(file,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + (line == null ? "" : line) + "\r\n",
                    new UTF8Encoding(false));
            }
        }
        catch { }
    }

    public static bool CreateShortcuts(string installDir, Action<string> log)
    {
        if (log == null) log = delegate(string s) { };
        string exe = Path.Combine(installDir, "AI-ACE.exe");
        if (!File.Exists(exe))
        {
            log("找不到 " + exe + "，跳过快捷方式。");
            return false;
        }
        bool ok = true;
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!string.IsNullOrEmpty(desktop))
            {
                if (MakeShortcut(Path.Combine(desktop, "AI-ACE.lnk"), exe, installDir)) log("已创建桌面快捷方式：AI-ACE");
                else { ok = false; log("桌面快捷方式创建失败。"); }
            }
        }
        catch (Exception ex) { ok = false; log("桌面快捷方式失败：" + ex.Message); }
        try
        {
            string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            if (!string.IsNullOrEmpty(programs))
            {
                if (MakeShortcut(Path.Combine(programs, "AI-ACE.lnk"), exe, installDir)) log("已创建开始菜单快捷方式：AI-ACE");
                else { ok = false; log("开始菜单快捷方式创建失败。"); }
            }
        }
        catch (Exception ex) { ok = false; log("开始菜单快捷方式失败：" + ex.Message); }
        return ok;
    }

    // 用 WScript.Shell 建 .lnk（COM 迟绑定，不引第三方库）：指向 安装目录\AI-ACE.exe，无参数
    static bool MakeShortcut(string lnkPath, string targetExe, string workDir)
    {
        object shell = null;
        try
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return false;
            shell = Activator.CreateInstance(t);
            object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            Type lt = lnk.GetType();
            lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { targetExe });
            lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { workDir });
            lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { targetExe + ",0" });
            lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "AI-ACE 桌面客户端" });
            lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
            return true;
        }
        catch { return false; }
        finally
        {
            try { if (shell != null) System.Runtime.InteropServices.Marshal.ReleaseComObject(shell); }
            catch { }
        }
    }

    public static long DirSizeMB(string dir)
    {
        long sum = 0;
        try
        {
            string[] files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
            for (int i = 0; i < files.Length; i++)
            {
                try { sum += new FileInfo(files[i]).Length; }
                catch { }
            }
        }
        catch { }
        return sum / (1024 * 1024);
    }

    public static bool DeleteTree(string dir)
    {
        try { Directory.Delete(dir, true); return true; }
        catch { }
        try
        {
            string empty = Path.Combine(Path.GetTempPath(), "ai-ace-empty-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(empty);
            string robocopy = "robocopy";
            string output;
            RunProcess(robocopy, "\"" + empty + "\" \"" + dir + "\" /MIR /NFL /NDL /NJH /NJS /NP /R:1 /W:1", 1800000, out output, null);
            try { Directory.Delete(empty, true); } catch { }
            Directory.Delete(dir, true);
            return true;
        }
        catch { return false; }
    }

    // ── 进程 / 文本小工具 ──────────────────────────────────────

    static int RunProcess(string exe, string args, int timeoutMs, out string output, string workDir)
    {
        output = "";
        Process p = null;
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = exe;
            psi.Arguments = args;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            if (!string.IsNullOrEmpty(workDir)) psi.WorkingDirectory = workDir;
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
            if (!exited) { try { p.Kill(); } catch { } }
            if (exited) { try { p.WaitForExit(); } catch { } }
            lock (sb) { output = sb.ToString(); }
            if (!exited) return -1;
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            output = ex.Message;
            return -1;
        }
        finally
        {
            if (p != null) { try { p.Dispose(); } catch { } }
        }
    }

    static string Tail(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Trim();
        if (s.Length <= max) return s;
        return "…" + s.Substring(s.Length - max);
    }

    // Program.Main 调它：跑向导，返回 true 表示部署完成可以进主界面
    public static bool RunSetupWizard()
    {
        using (SetupWizardForm f = new SetupWizardForm())
        {
            Application.Run(f);
            LastInstallDir = f.InstallDir == null ? "" : f.InstallDir;
            return f.Completed;
        }
    }
}

// ════════════════════════════════════════════════════════════════════
//  只读 tar 头，取 manifest.json 里的 RepoTags —— 用来跳过已载入的镜像
//  （docker save 的 tar 第一块通常就是 manifest.json，读不到就当缺失）
// ════════════════════════════════════════════════════════════════════
public static class TarTags
{
    public static List<string> RepoTags(string tarPath)
    {
        List<string> tags = new List<string>();
        try
        {
            using (FileStream fs = new FileStream(tarPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
            {
                byte[] header = new byte[512];
                long len = fs.Length;
                long pos = 0;
                while (pos + 512 <= len)
                {
                    fs.Position = pos;
                    if (!ReadFull(fs, header, 512)) break;
                    bool zero = true;
                    for (int i = 0; i < 512; i++) { if (header[i] != 0) { zero = false; break; } }
                    if (zero) break;
                    string name = Str(header, 0, 100);
                    long size = Octal(header, 124, 12);
                    byte type = header[156];
                    if (name == "manifest.json" && (type == (byte)'0' || type == 0))
                    {
                        if (size > 0 && size <= 4 * 1024 * 1024)
                        {
                            byte[] buf = new byte[size];
                            fs.Position = pos + 512;
                            if (ReadFull(fs, buf, (int)size))
                                tags = ParseRepoTags(Encoding.UTF8.GetString(buf));
                        }
                        break;
                    }
                    pos += 512 + ((size + 511) / 512) * 512;
                }
            }
        }
        catch { }
        return tags;
    }

    static bool ReadFull(Stream s, byte[] buf, int count)
    {
        int got = 0;
        while (got < count)
        {
            int n = s.Read(buf, got, count - got);
            if (n <= 0) return false;
            got += n;
        }
        return true;
    }

    static string Str(byte[] b, int off, int max)
    {
        int end = off;
        while (end < off + max && b[end] != 0) end++;
        return Encoding.UTF8.GetString(b, off, end - off).Trim();
    }

    static long Octal(byte[] b, int off, int max)
    {
        string s = Str(b, off, max);
        if (s.Length == 0) return 0;
        long v = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] < '0' || s[i] > '7') break;
            v = v * 8 + (s[i] - '0');
        }
        return v;
    }

    static List<string> ParseRepoTags(string json)
    {
        List<string> list = new List<string>();
        int k = json.IndexOf("\"RepoTags\"", StringComparison.Ordinal);
        if (k < 0) return list;
        int a = json.IndexOf('[', k);
        if (a < 0) return list;
        int b = json.IndexOf(']', a);
        if (b < 0) return list;
        string inner = json.Substring(a + 1, b - a - 1);
        int i = 0;
        while (true)
        {
            int q1 = inner.IndexOf('"', i);
            if (q1 < 0) break;
            int q2 = inner.IndexOf('"', q1 + 1);
            if (q2 < 0) break;
            string s = inner.Substring(q1 + 1, q2 - q1 - 1).Trim();
            if (s.Length > 0) list.Add(s);
            i = q2 + 1;
        }
        return list;
    }
}

// ════════════════════════════════════════════════════════════════════
//  部署向导窗口：5 步顺序执行 + 进度条 + 日志区（同一窗口，不跳窗）
//  第 1 步 环境检查 → 第 2 步 安装位置 → 第 3 步 载入镜像
//  → 第 4 步 保留旧数据 → 第 5 步 启动与收尾（compose/健康/状态/快捷方式/浏览器）
// ════════════════════════════════════════════════════════════════════
public class SetupWizardForm : Form
{
    public bool Completed = false;
    public string InstallDir = "";
    public string DataRoot = "";
    bool installInPlace = false;

    static readonly Color CBack = Color.FromArgb(24, 26, 31);
    static readonly Color CText = Color.FromArgb(228, 231, 236);
    static readonly Color CMuted = Color.FromArgb(150, 158, 170);
    static readonly Color CAccent = Color.FromArgb(64, 132, 255);
    static readonly Color CLogBg = Color.FromArgb(16, 18, 22);

    readonly Label stepLabel;
    readonly Label detailLabel;
    readonly Label dirLabel;
    readonly Label dataLabel;
    readonly Label dirValue;
    readonly Label dataValue;
    readonly ProgressBar bar;
    readonly TextBox logBox;
    readonly Button btnRetry;
    readonly Button btnCancel;
    readonly Panel dirPanel;

    Thread worker;
    volatile bool running = false;
    volatile bool cancel = false;
    bool autoStarted = false;     // Shown 自动开始只用一次，防止重复投递
    string resolvedDir = "";      // 已算好的安装位置：重试部署直接复用，不再询问
    bool resolvedInPlace = false;
    string resolvedData = "";     // 已算好的数据目录

    public SetupWizardForm()
    {
        Text = "AI-ACE 一键部署";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        BackColor = CBack;
        ForeColor = CText;
        Font = new Font("Microsoft YaHei UI", 9f);
        ClientSize = new Size(760, 540);

        stepLabel = new Label();
        stepLabel.Text = "第 1 步 / 共 5 步：环境检查";
        stepLabel.Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold);
        stepLabel.ForeColor = CText;
        stepLabel.SetBounds(18, 14, 724, 26);
        Controls.Add(stepLabel);

        detailLabel = new Label();
        detailLabel.Text = "正在自动部署，不需要任何操作，请耐心等待。";
        detailLabel.ForeColor = CMuted;
        detailLabel.SetBounds(18, 42, 724, 18);
        Controls.Add(detailLabel);

        bar = new ProgressBar();
        bar.Minimum = 0;
        bar.Maximum = 100;
        bar.Style = ProgressBarStyle.Continuous;
        bar.SetBounds(18, 66, 724, 14);
        Controls.Add(bar);

        logBox = new TextBox();
        logBox.Multiline = true;
        logBox.ReadOnly = true;
        logBox.ScrollBars = ScrollBars.Vertical;
        logBox.BackColor = CLogBg;
        logBox.ForeColor = CText;
        logBox.BorderStyle = BorderStyle.FixedSingle;
        logBox.Font = new Font("Consolas", 9f);
        logBox.SetBounds(18, 90, 724, 200);
        Controls.Add(logBox);

        dirPanel = new Panel();
        dirPanel.SetBounds(18, 296, 724, 136);
        dirPanel.BackColor = CBack;
        Controls.Add(dirPanel);

        dirLabel = new Label();
        dirLabel.Text = "安装位置（全自动选择；不能是 C 盘、磁盘根目录或程序所在目录的子目录）：";
        dirLabel.ForeColor = CMuted;
        dirLabel.SetBounds(0, 0, 724, 18);
        dirPanel.Controls.Add(dirLabel);

        dirValue = new Label();
        dirValue.Text = "（正在自动选择…）";
        dirValue.ForeColor = CText;
        dirValue.AutoEllipsis = true;
        dirValue.SetBounds(0, 24, 724, 22);
        dirPanel.Controls.Add(dirValue);

        dataLabel = new Label();
        dataLabel.Text = "数据目录（全自动选择；不能是 C 盘、磁盘根目录或安装目录内部，至少 5 GB 可用）：";
        dataLabel.ForeColor = CMuted;
        dataLabel.SetBounds(0, 58, 724, 18);
        dirPanel.Controls.Add(dataLabel);

        dataValue = new Label();
        dataValue.Text = "（正在自动选择…）";
        dataValue.ForeColor = CText;
        dataValue.AutoEllipsis = true;
        dataValue.SetBounds(0, 82, 724, 22);
        dirPanel.Controls.Add(dataValue);

        // 只在失败后出现：重试部署（初始隐藏；窗口上没有任何必填输入）
        btnRetry = new Button();
        btnRetry.Text = "重试部署";
        btnRetry.SetBounds(520, 480, 110, 34);
        btnRetry.FlatStyle = FlatStyle.Flat;
        btnRetry.BackColor = CAccent;
        btnRetry.ForeColor = Color.White;
        btnRetry.Visible = false;
        btnRetry.Click += delegate { StartWork(); };
        Controls.Add(btnRetry);

        btnCancel = new Button();
        btnCancel.Text = "退出";
        btnCancel.SetBounds(642, 480, 100, 34);
        btnCancel.FlatStyle = FlatStyle.Flat;
        btnCancel.Click += delegate { Close(); };
        Controls.Add(btnCancel);

        FormClosing += delegate(object s, FormClosingEventArgs e)
        {
            if (running && !cancel)
            {
                if (MessageBox.Show("正在部署，确定要退出吗？（已复制的文件会保留）", "AI-ACE 一键部署",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                cancel = true;
            }
        };

        Append("AI-ACE 一键部署：共 5 步，全程约 10–30 分钟（取决于磁盘速度与镜像大小）。");
        Append("说明：不会安装 Docker Desktop，只检查并使用已有的 Docker。");
        Append("说明：安装位置与数据目录全自动决定，窗口出现后立即开始，无需任何点击。");
        Append("数据目录用于容器数据、文档、输出、知识库、数据库，不占 C 盘。");

        // 窗口显示前就把两个目录算好（只读展示），Shown 时直接开始部署
        ResolveDirs();

        // 自动开始入口：只触发一次，保护见 AutoStartOnce
        Shown += delegate { AutoStartOnce(); };
    }

    // ── UI 线程小工具 ──────────────────────────────────────────

    void Ui(Action a)
    {
        if (a == null) return;
        try
        {
            if (InvokeRequired) Invoke(a);
            else a();
        }
        catch { }
    }

    void Append(string msg)
    {
        try
        {
            logBox.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + "\r\n");
            logBox.SelectionStart = logBox.TextLength;
            logBox.ScrollToCaret();
        }
        catch { }
        Setup.AppendLogFile(msg);
    }

    void Log(string msg) { Ui(delegate { Append(msg); }); }

    void Detail(string msg) { Ui(delegate { detailLabel.Text = msg; }); }

    void Step(int n, string title)
    {
        Ui(delegate
        {
            stepLabel.Text = "第 " + n + " 步 / 共 5 步：" + title;
            Append("—— 第 " + n + " 步：" + title + " ——");
        });
    }

    void Prog(double percent)
    {
        int v = (int)Math.Round(percent);
        if (v < 0) v = 0;
        if (v > 100) v = 100;
        Ui(delegate { bar.Value = v; });
    }

    void ProgImages(double fraction)
    {
        Prog(40 + fraction * 30);
    }

    bool AskYesNo(string text, string caption)
    {
        bool yes = false;
        Ui(delegate
        {
            yes = MessageBox.Show(text, caption, MessageBoxButtons.YesNo,
                MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        });
        return yes;
    }

    // ── 全自动目录解析 / 自动开始 ─────────────────────────────

    // 算好安装位置与数据目录（只读展示）；重试部署沿用已算好的结果，不再询问
    void ResolveDirs()
    {
        if (resolvedDir.Length == 0)
        {
            string d; bool ip; string err;
            if (Setup.ResolveAutoInstallDir(out d, out ip, out err))
            {
                resolvedDir = d;
                resolvedInPlace = ip;
            }
            else Append("自动选择安装位置失败：" + err);
        }
        try { dirValue.Text = resolvedDir.Length > 0 ? resolvedDir + (resolvedInPlace ? "（就地安装，不复制）" : "（复制安装）") : "（自动选择失败，详见日志）"; }
        catch { }

        if (resolvedData.Length == 0 && resolvedDir.Length > 0)
        {
            string d; string err;
            if (Setup.ResolveAutoDataDir(resolvedDir, out d, out err)) resolvedData = d;
            else Append("自动选择数据目录失败：" + err);
        }
        try { dataValue.Text = resolvedData.Length > 0 ? resolvedData : "（自动选择失败，详见日志）"; }
        catch { }

        InstallDir = resolvedDir;
        DataRoot = resolvedData;
        installInPlace = resolvedInPlace;
    }

    // Shown 的唯一入口：无论事件来几次，只启动一次
    void AutoStartOnce()
    {
        if (autoStarted) return;
        autoStarted = true;
        StartWork();
    }

    // ── 启动 / 失败 ────────────────────────────────────────────

    void StartWork()
    {
        if (running) return;
        // 目录全部自动解析：重试部署直接复用已算好的结果，不再询问
        ResolveDirs();
        if (resolvedDir.Length == 0) { Fail("无法自动确定安装位置。"); return; }
        if (resolvedData.Length == 0) { Fail("无法自动确定数据目录。"); return; }
        InstallDir = resolvedDir;
        DataRoot = resolvedData;
        installInPlace = resolvedInPlace;
        Detail("正在自动部署，不需要任何操作，请耐心等待。");
        running = true;
        cancel = false;
        Completed = false;
        logBox.Clear();
        btnRetry.Visible = false;
        btnRetry.Enabled = false;
        worker = new Thread(Work);
        worker.IsBackground = true;
        worker.Start();
    }

    void Fail(string msg)
    {
        running = false;
        Ui(delegate
        {
            detailLabel.Text = "部署已停止。可点「重试部署」重跑，目录沿用已算好的结果。";
            btnRetry.Text = "重试部署";
            btnRetry.Visible = true;
            btnRetry.Enabled = true;
            if (msg == null || msg.Length == 0) msg = "部署没有完成，详见上方日志。";
            Append("部署失败：" + msg);
            MessageBox.Show(msg, "AI-ACE 一键部署", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        });
    }

    // ── 5 步主流程（后台线程）──────────────────────────────────

    void Work()
    {
        try
        {
            // 第 1 步：环境检查（Docker / 引擎 / 磁盘 / 显卡）
            Step(1, "环境检查");
            Prog(3);
            EnvReport env = Setup.CheckEnvironment(Log, Detail);
            if (!env.Ok) { Fail(env.Error); return; }
            if (cancel) return;
            Prog(18);

            // 第 2 步：安装位置（可移动盘禁止就地；不是完整包就复制）
            Step(2, "选择安装位置");
            // 安装位置已在 UI 线程的 StartWork 里校验并保存，这里只读字段，不碰任何控件
            string dir = InstallDir;
            bool inPlace = installInPlace;
            if (string.IsNullOrEmpty(dir)) { Fail("没有可用的安装位置，请重新填写后再试。"); return; }
            Log("安装位置：" + dir + (inPlace ? "（就地安装，不复制）" : "（复制安装）"));
            // 先记下「之前的数据目录」：重装 / 换盘时第 4 步要把里面的数据搬过来
            string prevData = DataDir.Current(dir);
            Log("之前的数据目录：" + (prevData.Length == 0 ? "（无）" : prevData));
            if (inPlace) Log("就地安装：程序所在目录就是安装目录。");
            else if (Setup.HasOldData(dir)) Log("目标已有旧 open-webui\\webui.db，将在第 4 步备份、改名旧目录后再展开部署包。");
            else
            {
                Detail("正在复制部署包（文件较多，请耐心等待）…");
                Log("正在复制部署包：" + env.Root + " → " + dir);
                if (!Setup.CopyTree(env.Root, dir, Log)) { Fail("复制部署包失败：请检查磁盘空间和权限，或换一个安装位置。"); return; }
                Log("已复制到：" + dir);
            }
            // 数据目录：建 5 个子目录（compose\.env 在第 5 步启动前写，保证先有目录再有容器）
            string dataRoot = DataRoot;
            if (string.IsNullOrEmpty(dataRoot)) { Fail("没有可用的数据目录，请重新填写后再试。"); return; }
            if (!DataDir.EnsureDirs(dataRoot)) { Fail("无法创建数据目录或其子目录：" + dataRoot); return; }
            Log("数据目录：" + dataRoot + "（已建 open-webui、documents、output、knowledge、database）");

            if (cancel) return;
            Prog(40);

            // 第 3 步：逐个 docker load（已存在的镜像跳过，单条 3600000ms 超时）
            Step(3, "载入镜像");
            string ierr;
            if (!Setup.LoadImages(env.Root, dir, Log, ProgImages, out ierr)) { Fail(ierr); return; }
            if (cancel) return;
            Prog(70);

            // 第 4 步：保留旧数据（webui.db / -wal / -shm / uploads）
            Step(4, "保留旧数据");
            string backupDir;
            string oldDir = Setup.PreserveOldData(env.Root, dir, inPlace, dataRoot, prevData, out backupDir, Log);
            if (cancel) return;
            Prog(80);

            // 第 5 步：compose up + 健康检查 + 状态文件 + 快捷方式（不打开浏览器）
            Step(5, "启动与收尾");
            // 启动容器之前必须先写 compose\.env（compose 侧用 ${AIACE_DATA:-..} 引用数据目录）
            string envErr;
            if (!DataDir.WriteEnv(dir, dataRoot, out envErr)) { Fail("写 compose\\.env 失败：" + envErr); return; }
            Log("已写 " + DataDir.EnvPath(dir) + "：" + DataDir.EnvKey + "=" + dataRoot.Replace('\\', '/'));
            bool healthy = Setup.StartStack(dir, Log, Detail);
            if (!healthy)
            {
                if (!AskYesNo("服务还没就绪。首次启动要加载模型，可能需要几分钟。\r\n\r\n要继续完成安装吗？（之后双击 AI-ACE.exe 也会自动把服务拉起来）", "AI-ACE 一键部署"))
                { Fail("已停止：服务没有就绪。"); return; }
                Log("按要求继续收尾；服务稍后由启动器拉起。");
            }
            Prog(92);

            string mode = inPlace ? "inplace" : "copied";
            Setup.WriteInstallState(dir, mode, oldDir, dataRoot);
            Log("已写入安装状态：" + Path.Combine(Path.Combine(dir, "system"), "install-state.json"));
            try
            {
                string logSrc = Setup.InstallLogPath();
                string logDstDir = Path.Combine(dir, "system");
                if (File.Exists(logSrc))
                {
                    Directory.CreateDirectory(logDstDir);
                    File.Copy(logSrc, Path.Combine(logDstDir, "install.log"), true);
                    Log("向导日志已复制到：" + Path.Combine(logDstDir, "install.log"));
                }
            }
            catch (Exception ex) { Log("向导日志复制失败（不影响安装）：" + ex.Message); }
            Setup.CreateShortcuts(dir, Log);
            Log("不打开浏览器：装完直接进入桌面端主界面。");

            // 旧目录删除：默认 No，选 Yes 才删
            if (oldDir != null && oldDir.Length > 0 && Directory.Exists(oldDir))
            {
                long mb = Setup.DirSizeMB(oldDir);
                bool del = AskYesNo("旧版本还在 " + oldDir + "（" + mb + " MB），对话记录和账号已复制到新目录。\r\n\r\n要删掉旧目录吗？",
                    "AI-ACE 安装完成");
                if (del)
                {
                    if (Setup.DeleteTree(oldDir)) Log("已删除旧目录：" + oldDir);
                    else Log("删除失败，旧版本保留在：" + oldDir);
                }
                else Log("已保留旧版本：" + oldDir);
            }

            Prog(100);
            Completed = true;
            running = false;
            Detail("部署完成，正在进入主界面…");
            Log("部署完成。");
            Ui(delegate { Close(); });
        }
        catch (Exception ex)
        {
            Fail("安装过程出错：" + ex.Message);
        }
    }
}
