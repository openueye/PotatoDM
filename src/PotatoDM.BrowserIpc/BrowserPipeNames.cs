using System.Diagnostics;

namespace PotatoDM.BrowserIpc;

/// <summary>Per-session pipe names so parallel Windows sessions never collide.</summary>
public static class BrowserPipeNames
{
    public const string PipeNameOverrideVariable = "POTATODM_PIPE_NAME";

    public static string ForCurrentSession()
    {
        var overridden = Environment.GetEnvironmentVariable(PipeNameOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return overridden.Trim();
        }

        return ForSessionId(Process.GetCurrentProcess().SessionId);
    }

    public static string ForSessionId(int sessionId) => $"PotatoDM.Browser.v1.{sessionId}";
}
