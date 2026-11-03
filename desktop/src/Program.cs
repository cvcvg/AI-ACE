using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

static class Program
{
    const string MutexName = "Global\\AI-ACE-Native-Client-v2";
    const string ShowEventName = "Global\\AI-ACE-Native-Show-v2";

    [STAThread]
    static void Main(string[] args)
    {
        // 诊断开关（无窗口，供验收）：AI-ACE.exe --probe <输出文件> → 写 UTF-8 JSON，退出码 0
        if (args != null && args.Length > 0 && string.Equals(args[0], "--probe", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 2) { Environment.ExitCode = 2; return; }
            Environment.ExitCode = Setup.RunProbe(args[1]);
            return;
        }

        bool created = false;
        Mutex mutex = new Mutex(true, MutexName, out created);
        if (!created)
        {
            try { EventWaitHandle ev = EventWaitHandle.OpenExisting(ShowEventName); ev.Set(); }
            catch { }
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 路由：没有 system\install-state.json = 首次运行 → 部署向导；有 = 已部署 → 原启动器
        if (!Setup.IsDeployed())
        {
            if (!Setup.RunSetupWizard()) { GC.KeepAlive(mutex); return; }
            // 向导成功后直接进主界面（不再走 splash）；复制安装时把主界面指到新目录
            string target = Setup.LastInstallDir;
            if (target != null && target.Length > 0 &&
                !string.Equals(target.TrimEnd('\\'), Setup.ExeDir(), StringComparison.OrdinalIgnoreCase))
                Environment.SetEnvironmentVariable("AI_ACE_ROOT", target);
        }
        else
        {
            using (SplashForm splash = new SplashForm())
            {
                Application.Run(splash);
                if (splash.Result == null || !splash.Result.Ok) { GC.KeepAlive(mutex); return; }
                Bootstrap.DemoMode = splash.Result.Demo;
            }
        }

        EventWaitHandle show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        MainForm form = new MainForm();
        RegisteredWaitHandle reg = ThreadPool.RegisterWaitForSingleObject(show,
            delegate(object state, bool timedOut)
            {
                MainForm f = state as MainForm;
                if (f == null) return;
                try { f.BeginInvoke(new Action(f.RestoreFromTray)); }
                catch { }
            }, form, Timeout.Infinite, false);
        Application.Run(form);
        GC.KeepAlive(reg);
        GC.KeepAlive(mutex);
    }
}
