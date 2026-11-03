using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;

// 「联网也不外泄」启动自检：只读核对三件事——端口只对本机监听、DOCKER-USER 已挂 AIACE_EGRESS、回滚脚本在位。
// 不安装、不修改任何规则；任何一步失败只降级为「无法确认」，绝不抛异常、绝不阻断启动。
public class NetGuardResult
{
    public string Title = "";
    public string Summary = "";
    public string Detail = "";
}

public static class NetGuard
{
    // 与 docker-compose 的端口映射保持一致
    static readonly int[] WatchPorts = new int[] { 3000, 9000, 8010, 8011, 8081 };

    const string Chain = "AIACE_EGRESS";
    const string Distro = "docker-desktop";
    const string ScriptName = "联网隔离.ps1";
    const int WslTimeoutMs = 20000;   // 单次 wsl 调用硬上限 20 秒，整体远低于 60 秒预算

    const int Ok = 0;
    const int Warn = 1;
    const int Unknown = 2;

    public static NetGuardResult SecurityCheck()
    {
        try
        {
            return CheckCore();
        }
        catch (Exception ex)
        {
            NetGuardResult r = new NetGuardResult();
            r.Title = "AI-ACE 安全自检 · 无法确认";
            r.Summary = "结论：无法确认（自检本身出错：" + OneLine(ex.Message) + "）";
            r.Detail = "[UNKNOWN] 安全自检未能完成，启动继续。" + Environment.NewLine + ex.Message;
            return r;
        }
    }

    static NetGuardResult CheckCore()
    {
        List<string> lines = new List<string>();
        string[] state = new string[3];
        int[] level = new int[3];

        // 1) 端口暴露面
        try
        {
            List<string> exposed = ExposedPorts();
            if (exposed.Count == 0)
            {
                level[0] = Ok; state[0] = "正常";
                lines.Add("[OK]      端口暴露面：3000/9000/8010/8011/8081 未对外监听（未监听，或只监听 127.0.0.1 / ::1）");
            }
            else
            {
                level[0] = Warn; state[0] = "有风险";
                lines.Add("[WARN]    端口暴露面：以下监听地址不是 127.0.0.1 / ::1 → " + Join(exposed));
            }
        }
        catch (Exception ex)
        {
            level[0] = Unknown; state[0] = "无法确认";
            lines.Add("[UNKNOWN] 端口暴露面：无法枚举本机监听端口（" + OneLine(ex.Message) + "）");
        }

        // 2) 拦截规则
        try
        {
            string reason;
            int st = CheckRuleChain(out reason);
            level[1] = st;
            if (st == Ok)
            {
                state[1] = "正常";
                lines.Add("[OK]      拦截规则：DOCKER-USER 链中已挂 " + Chain + "（先 RETURN 内网，再 LOG，最后 DROP）");
            }
            else if (st == Warn)
            {
                state[1] = "有风险";
                lines.Add("[WARN]    拦截规则：" + reason);
            }
            else
            {
                state[1] = "无法确认";
                lines.Add("[UNKNOWN] 拦截规则：无法确认（" + reason + "）—— Docker 引擎/WSL 未就绪不算失败");
            }
        }
        catch (Exception ex)
        {
            level[1] = Unknown; state[1] = "无法确认";
            lines.Add("[UNKNOWN] 拦截规则：无法确认（" + OneLine(ex.Message) + "）");
        }

        // 3) 回滚脚本是否在位
        try
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            string p1 = Path.Combine(exeDir, "desktop", "安全", ScriptName);
            string p2 = Path.Combine(exeDir, "安全", ScriptName);
            if (File.Exists(p1))
            {
                level[2] = Ok; state[2] = "正常";
                lines.Add("[OK]      回滚脚本：" + p1);
            }
            else if (File.Exists(p2))
            {
                level[2] = Ok; state[2] = "正常";
                lines.Add("[OK]      回滚脚本：" + p2);
            }
            else
            {
                level[2] = Warn; state[2] = "有风险";
                lines.Add("[WARN]    回滚脚本：未找到 " + ScriptName + "，已查找：" + p1 + " ；" + p2);
            }
        }
        catch (Exception ex)
        {
            level[2] = Unknown; state[2] = "无法确认";
            lines.Add("[UNKNOWN] 回滚脚本：无法确认（" + OneLine(ex.Message) + "）");
        }

        int okCount = 0, warnCount = 0, unknownCount = 0;
        for (int i = 0; i < level.Length; i++)
        {
            if (level[i] == Warn) warnCount++;
            else if (level[i] == Unknown) unknownCount++;
            else okCount++;
        }
        string verdict = warnCount > 0 ? "有风险" : (unknownCount > 0 ? "无法确认" : "全绿");

        StringBuilder d = new StringBuilder();
        d.AppendLine("端口暴露面：" + state[0]);
        d.AppendLine("拦截规则：" + state[1]);
        d.AppendLine("回滚脚本：" + state[2]);
        d.AppendLine();
        for (int i = 0; i < lines.Count; i++) d.AppendLine(lines[i]);
        d.AppendLine();
        d.Append("脚本用法：无参=只审计不修改；-Apply 装规则；-Report 出报告；-Remove 回滚。");

        NetGuardResult r = new NetGuardResult();
        r.Title = "AI-ACE 安全自检 · " + verdict;
        r.Summary = "结论：" + verdict + "（正常 " + okCount + " 项 / 有风险 " + warnCount + " 项 / 无法确认 " + unknownCount + " 项）";
        r.Detail = d.ToString();
        return r;
    }

    // 返回所有「监听地址不是回环」的受关注端口，形如 0.0.0.0:3000
    static List<string> ExposedPorts()
    {
        List<string> bad = new List<string>();
        IPEndPoint[] eps = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        for (int i = 0; i < eps.Length; i++)
        {
            IPEndPoint ep = eps[i];
            if (ep == null) continue;
            bool watch = false;
            for (int k = 0; k < WatchPorts.Length; k++)
            {
                if (WatchPorts[k] == ep.Port) { watch = true; break; }
            }
            if (!watch) continue;
            if (IPAddress.IsLoopback(ep.Address)) continue;
            string s = ep.Address + ":" + ep.Port;
            if (!bad.Contains(s)) bad.Add(s);
        }
        return bad;
    }

    // Ok = DOCKER-USER 有跳转且 AIACE_EGRESS 链内有 DROP；Warn = 只齐一半；Unknown = wsl/引擎拿不到结果
    static int CheckRuleChain(out string reason)
    {
        reason = "";
        int code; string err;
        string user = RunCapture(WslPath(), "-d " + Distro + " -u root iptables -S DOCKER-USER", WslTimeoutMs, out code, out err);
        string fail = OneLine(err);
        if (code != 0)
        {
            reason = code == int.MinValue
                ? "DOCKER-USER 查询没跑起来：" + (fail.Length > 0 ? fail : "wsl 未能启动")
                : "wsl/iptables 未返回可用结果，退出码 " + code + (fail.Length > 0 ? "：" + fail : "");
            return Unknown;
        }
        if (user.IndexOf("DOCKER-USER", StringComparison.OrdinalIgnoreCase) < 0)
        {
            reason = "输出里没有 DOCKER-USER 链（Docker 未就绪）";
            return Unknown;
        }
        if (!HasJump(user, Chain))
        {
            reason = "DOCKER-USER 里没有 -j " + Chain + " 跳转，出网没有被拦截 —— 请运行 安全\\联网隔离.ps1 -Apply";
            return Warn;
        }
        int ccode; string cerr;
        string chain = RunCapture(WslPath(), "-d " + Distro + " -u root iptables -S " + Chain, WslTimeoutMs, out ccode, out cerr);
        if (ccode != 0)
        {
            string ce = OneLine(cerr);
            reason = "DOCKER-USER 已跳转到 " + Chain + "，但 " + (ccode == int.MinValue ? "读取该链没跑起来" : "读取该链失败，退出码 " + ccode)
                + (ce.Length > 0 ? "：" + ce : "") + " —— 看不到 DROP，请重跑 安全\\联网隔离.ps1 -Apply";
            return ccode == int.MinValue ? Unknown : Warn;
        }
        if (!HasJump(chain, "DROP"))
        {
            reason = "链在但没有 DROP，等于没拦 —— 请重跑 安全\\联网隔离.ps1 -Apply";
            return Warn;
        }
        return Ok;
    }

    // 输出里有没有「-j 目标」这种跳转（-N 定义行不算，链名要求整词匹配）
    static bool HasJump(string text, string target)
    {
        if (string.IsNullOrEmpty(text)) return false;
        string[] rows = text.Split('\n');
        string needle = " -j " + target + " ";
        for (int i = 0; i < rows.Length; i++)
        {
            string row = " " + rows[i].Trim() + " ";
            if (!row.StartsWith(" -N ") && row.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    static string WslPath()
    {
        try
        {
            string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
            if (File.Exists(p)) return p;
        }
        catch { }
        return "wsl.exe";
    }

    // 同步跑一个无窗口子进程；超时或起不来时 exitCode = int.MinValue
    static string RunCapture(string exe, string args, int timeoutMs, out int exitCode, out string errText)
    {
        exitCode = int.MinValue;
        errText = "";
        StringBuilder stdout = new StringBuilder();
        StringBuilder stderr = new StringBuilder();
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (Process p = new Process())
            {
                p.StartInfo = psi;
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs a)
                {
                    if (a.Data != null) { lock (stdout) stdout.AppendLine(a.Data); }
                };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs a)
                {
                    if (a.Data != null) { lock (stderr) stderr.AppendLine(a.Data); }
                };
                if (!p.Start())
                {
                    errText = "进程未能启动";
                    return "";
                }
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    try { p.WaitForExit(2000); } catch { }
                    errText = "调用超时（" + (timeoutMs / 1000) + " 秒）";
                    return "";
                }
                p.WaitForExit();
                exitCode = p.ExitCode;
            }
        }
        catch (Exception ex)
        {
            errText = ex.Message;
            return "";
        }
        string o, e2;
        lock (stdout) { o = stdout.ToString(); }
        lock (stderr) { e2 = stderr.ToString(); }
        errText = e2.Trim();
        return o;
    }

    static string Join(List<string> items)
    {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0) sb.Append("、");
            sb.Append(items[i]);
        }
        return sb.ToString();
    }

    static string OneLine(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
    }
}
