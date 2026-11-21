using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace RemotePlay;

internal sealed record NetworkPriorityState(bool Configured, bool? Effective);

internal static class NetworkPriority
{
    private const string HelperArgument = "--network-priority";
    private const int VideoDscp = 34;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly string PowerShell = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");

    // Values are JSON data on stdin. No path, policy name or user text is inserted into PowerShell code.
    private const string PolicyScript = """
        $ErrorActionPreference='Stop'
        $ProgressPreference='SilentlyContinue'
        $request=[Console]::In.ReadToEnd() | ConvertFrom-Json
        $owned=@(Get-NetQosPolicy | Where-Object { $_.Name -like ($request.Prefix+'*') })
        foreach($rule in $owned) {
            if($rule.AppPathName -ine $request.Executable) { throw 'La politica no pertenece a este ejecutable.' }
        }
        if($request.Action -ne 'query') {
            foreach($rule in $owned) { $rule | Remove-NetQosPolicy -Confirm:$false }
            if($request.Action -eq 'enable') {
                New-NetQosPolicy -Name $request.Name -AppPathNameMatchCondition $request.Executable `
                    -IPProtocolMatchCondition UDP -IPSrcPortMatchCondition ([UInt16]$request.Port) `
                    -NetworkProfile All -DSCPAction ([SByte]$request.Dscp) | Out-Null
            }
        }
        $stored=@(Get-NetQosPolicy | Where-Object {
            $_.Name -eq $request.Name -and $_.AppPathName -ieq $request.Executable -and
            ([string]$_.IPProtocol) -in @('UDP','17') -and $_.IPSrcPortStart -eq $request.Port -and
            $_.IPSrcPortEnd -eq $request.Port -and $_.DSCPValue -eq $request.Dscp
        })
        $effective=$null
        $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
        try {
            $admin=([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        } finally { $identity.Dispose() }
        if($admin) {
            $deadline=[DateTime]::UtcNow.AddSeconds(5)
            do {
                $active=@(Get-NetQosPolicy -PolicyStore ActiveStore | Where-Object {
                    $_.Name -eq $request.Name -and $_.AppPathName -ieq $request.Executable -and
                    ([string]$_.IPProtocol) -in @('UDP','17') -and $_.IPSrcPortStart -eq $request.Port -and
                    $_.IPSrcPortEnd -eq $request.Port -and $_.DSCPValue -eq $request.Dscp
                })
                $effective=($active.Count -gt 0)
                if($request.Action -eq 'query' -or $effective -eq ($request.Action -eq 'enable')) { break }
                Start-Sleep -Milliseconds 200
            } while([DateTime]::UtcNow -lt $deadline)
        }
        [pscustomobject]@{Configured=($stored.Count -gt 0);Effective=$effective} | ConvertTo-Json -Compress
        """;

    internal static bool IsHelper(string[] args) => args.Length > 0 && args[0] == HelperArgument;

    internal static int RunHelper(string[] args)
    {
        if (args.Length != 3 || args[1] is not ("enable" or "disable") ||
            !int.TryParse(args[2], out int port) || !ValidPort(port))
            return 2;
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            return 3;
        try
        {
            NetworkPriorityState state = RunPolicyAsync(args[1], port, CancellationToken.None).GetAwaiter().GetResult();
            bool expected = args[1] == "enable";
            if (state.Configured != expected || state.Effective != expected)
                ConnectionLog.Write("Anfitrión", "Comprobar prioridad de red",
                    new IOException($"QoS guardada={state.Configured}; aplicada={state.Effective}; solicitado={expected}."));
            return state.Configured == expected && state.Effective == expected ? 0 : 4;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or
            OperationCanceledException or JsonException)
        {
            ConnectionLog.Write("Anfitrión", "Configurar prioridad de red", ex);
            return 4;
        }
    }

    internal static Task<NetworkPriorityState> GetStateAsync(int port, CancellationToken stop) =>
        RunPolicyAsync("query", port, stop);

    internal static async Task ConfigureAsync(bool enable, int port)
    {
        if (!ValidPort(port)) throw new ArgumentOutOfRangeException(nameof(port));
        string executable = Environment.ProcessPath ?? throw new IOException("No se encontró el ejecutable de " + AppBrand.Name + ".");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = AppContext.BaseDirectory
        };
        start.ArgumentList.Add(HelperArgument);
        start.ArgumentList.Add(enable ? "enable" : "disable");
        start.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using Process helper = await Task.Run(() => Process.Start(start)) ??
            throw new IOException("Windows no pudo iniciar la configuración de prioridad de red.");
        await helper.WaitForExitAsync();
        if (helper.ExitCode != 0)
            throw new IOException("Windows no pudo configurar la prioridad de red. La transmisión puede continuar.");
    }

    private static bool ValidPort(int port) => port is >= 1 and <= 65535 && !RemoteSession.IsReservedPort(port);

    private static async Task<NetworkPriorityState> RunPolicyAsync(string action, int port, CancellationToken stop)
    {
        if (!ValidPort(port)) throw new ArgumentOutOfRangeException(nameof(port));
        string executable = Path.GetFullPath(Environment.ProcessPath ?? throw new IOException("No se encontró " + AppBrand.Name + "."));
        string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(executable.ToUpperInvariant())))[..12];
        string prefix = $"RemotePlayCino-QoS-{identity}-";
        string request = JsonSerializer.Serialize(new
        {
            Action = action, Executable = executable, Prefix = prefix, Name = prefix + port,
            Port = port, Dscp = VideoDscp
        });
        var start = new ProcessStartInfo(PowerShell)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(PolicyScript);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using Process process = Process.Start(start) ?? throw new IOException("No se pudo consultar QoS en Windows.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.StandardInput.WriteAsync(request.AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0)
                throw new IOException("No se pudo consultar o aplicar QoS. " + (await errors).Trim());
            return JsonSerializer.Deserialize<NetworkPriorityState>(await output, Json) ??
                throw new IOException("Windows devolvió un estado QoS vacío.");
        }
        finally
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { /* The process exited between the check and termination. */ }
            await ((Task)Task.WhenAll(output, errors)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }
}
