using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace ClassBox.Plugins.ClassTalk.Net;

/// <summary>
/// 本地 cmd 命令执行器（供管理员远程下发命令时在目标机器上执行）。
/// 用 chcp 65001 强制 UTF-8 输出，避免中文乱码；带超时与输出长度限制。
/// </summary>
public static class CommandRunner
{
    public const int TimeoutMs = 20000;
    public const int MaxOutputChars = 8000;

    public static string Run(string command)
    {
        command = (command ?? "").Trim();
        if (command.Length == 0) return "";

        try
        {
            var psi = new ProcessStartInfo("cmd.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("chcp 65001>nul & " + command);

            using var p = Process.Start(psi);
            if (p == null) return "[执行失败] 无法启动 cmd.exe";

            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();

            if (!p.WaitForExit(TimeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return $"[超时] 命令超过 {TimeoutMs / 1000} 秒未结束，已强制终止";
            }
            p.WaitForExit();

            string stdout = outTask.GetAwaiter().GetResult();
            string stderr = errTask.GetAwaiter().GetResult();
            var sb = new StringBuilder();
            if (stdout.Length > 0) sb.Append(stdout);
            if (stderr.Length > 0)
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append(stderr);
            }
            string result = sb.ToString().TrimEnd();
            if (result.Length == 0) result = $"（命令已执行，无输出）退出码 {p.ExitCode}";
            else if (p.ExitCode != 0) result += $"\n[退出码 {p.ExitCode}]";

            if (result.Length > MaxOutputChars)
                result = result.Substring(0, MaxOutputChars) + $"\n…（输出过长，已截断，共 {result.Length} 字符）";
            return result;
        }
        catch (Exception ex)
        {
            return "[执行失败] " + ex.Message;
        }
    }

    public static Task<string> RunAsync(string command) => Task.Run(() => Run(command));
}
