using System.Net;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Threading.Channels;
using NAudio.CoreAudioApi;

namespace RemotePlay;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (NetworkPriority.IsHelper(args))
        {
            Environment.ExitCode = NetworkPriority.RunHelper(args);
            return;
        }
        ApplicationConfiguration.Initialize();
        bool checkPortable = args is ["--portable-check"];
        try
        {
            PortableFiles.Prepare();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Environment.ExitCode = 1;
            if (checkPortable) Console.Error.WriteLine(ex.Message);
            else MessageBox.Show("No se pudieron preparar los archivos de RemotePlayCino. " +
                "Guarda el EXE en una carpeta donde puedas escribir y vuelve a abrirlo.\n\n" + ex.Message,
                AppBrand.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (checkPortable) return;
        Application.Run(new RemotePlayForm());
    }
}

internal sealed partial class RemotePlayForm : Form
{
    private const int DefaultPort = 48123;
    private readonly ComboBox hostAddress = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 245 };
    private readonly NumericUpDown hostPort = PortField();
    private readonly TextBox hostCode = new() { ReadOnly = true, UseSystemPasswordChar = true };
    private readonly Button copy = new() { Text = "Copiar", AutoSize = true, Enabled = false };
    private readonly Button revealCode = new() { Text = "Mostrar", Enabled = false };
    private readonly Label copyFeedback = new() { AutoSize = true, Visible = false };
    private readonly Button hostButton = new() { Text = "Compartir pantalla", AutoSize = true };
    private readonly Label hostStatus = new() { Text = "Pantalla sin compartir", AutoSize = true };
    private readonly Label hostPad = new() { Text = "Sin mando remoto", AutoSize = true };
    private readonly TextBox clientAddress = new() { PlaceholderText = "IP del anfitrión", Width = 150 };
    private readonly TextBox clientName = new() { PlaceholderText = "Invitado", MaxLength = 32 };
    private readonly NumericUpDown clientPort = PortField();
    private readonly TextBox clientCode = new() { PlaceholderText = "Pega el código", UseSystemPasswordChar = true };
    private readonly Button clientButton = new() { Text = "Conectar", AutoSize = true };
    private readonly ComboBox clientPad = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 115, DropDownWidth = 480 };
    private readonly Label clientStatus = new() { Text = "Desconectado", AutoSize = true };
    private readonly Label padReadout = new() { AutoSize = true };
    private readonly Label hostVideo = new() { AutoSize = true };
    private readonly Label clientVideo = new() { AutoSize = true, Text = "Video sin señal" };
    private readonly CheckBox hostAudio = new() { Text = "Compartir audio con invitados" };
    private readonly CheckBox hostInput = new() { Text = "Permitir mouse y teclado del invitado" };
    private readonly Label hostExtras = new() { AutoSize = true };
    private readonly CheckBox clientAudio = new() { Text = "Escuchar el audio del anfitrión" };
    private readonly CheckBox clientInput = new() { Text = "Enviar mi mouse y teclado" };
    private readonly Label clientExtras = new() { AutoSize = true };
    private readonly ComboBox clientResolution = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 115 };
    private readonly ComboBox clientFps = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 115 };
    private readonly ComboBox clientDecoder = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 115 };
    private readonly ComboBox hostBandwidth = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 115 };
    private readonly ComboBox hostMaxFps = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 115 };
    private readonly ComboBox hostDisplay = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 115 };
    private readonly ComboBox hostAudioDevice = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 115 };
    private readonly NumericUpDown hostIdle = new() { Minimum = 0, Maximum = 240, Value = 0, Width = 80 };
    private readonly CheckBox hostLock = new() { Text = "Bloquear el equipo al desconectarse el invitado" };
    private readonly CheckBox hostApprove = new() { Text = "Pedir aprobación a cada invitado (no aplica a este mismo PC)", Checked = true };
    private readonly CheckBox hostLocal1440 = new() { Text = "Limitar 1440p a 60 FPS con 4 invitados en este PC" };
    private readonly List<string?> audioDeviceIds = [];
    private readonly ComboBox hostEncoder = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 115 };
    private Channel<byte[]>? inputQueue;
    private readonly VideoSurface video = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer updateUi = new() { Interval = 125 };
    private CancellationTokenSource? hostStop;
    private CancellationTokenSource? clientStop;
    private const int MaxReconnects = 5;
    private int reconnectAttempts;
    private bool reconnectPending;
    private Task hostTask = Task.CompletedTask;
    private Task clientTask = Task.CompletedTask;
    private VideoFrame? pendingFrame;
    private int renderQueued;
    private int framesPresented;
    private string clientCodec = "";
    private sealed record NetworkSample(double Milliseconds, bool PadActive, long ReceivedAt);
    private NetworkSample? networkSample;
    private GuestDevices hostDesktopPermissions;
    private bool liveDesktopPermissions;
    private readonly Stopwatch fpsInterval = Stopwatch.StartNew();
    private bool closing;
    private bool readyToClose;

    internal RemotePlayForm(string? preferencesPath = null)
    {
        settingsPath = preferencesPath ?? AppSettings.DefaultPath;
        Text = $"{AppBrand.Name} · {Application.ProductVersion.Split('+')[0]}";
        AppBrand.ApplyIcon(this);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 11);
        ClientSize = new Size(1160, 710);
        MinimumSize = new Size(900, 540);
        StartPosition = FormStartPosition.CenterScreen;

        BuildLayout();
        StartUpdateCheck();
        ConfigurePublicAddress();
        video.InputSink = packet => Volatile.Read(ref inputQueue)?.Writer.TryWrite(packet);

        clientResolution.Items.AddRange(Qualities.Resolutions.Select(item => (object)item.Name).ToArray());
        clientResolution.SelectedIndex = 0;
        clientFps.Items.AddRange(Qualities.FpsOptions.Select(item => (object)$"{item} FPS").ToArray());
        clientFps.SelectedIndex = 1;
        clientDecoder.Items.AddRange(["Software", "Hardware (D3D11)", "Hardware (NVIDIA CUDA)"]);
        clientDecoder.SelectedIndex = 0;
        hostBandwidth.Items.AddRange(Qualities.BandwidthOptions.Select(item => (object)$"{item} Mbps").ToArray());
        hostBandwidth.SelectedIndex = 3;
        hostMaxFps.Items.AddRange(Qualities.FpsOptions.Select(item => (object)$"{item} FPS").ToArray());
        hostMaxFps.SelectedIndex = Qualities.FpsOptions.Length - 1;
        hostEncoder.Items.AddRange(["Automático", "NVIDIA NVENC", "CPU (x264)"]);
        hostEncoder.SelectedIndex = 0;
        FillDisplayAndAudioLists();
        InitializeAudioSelection();
        hostButton.Click += (_, _) =>
        {
            if (hostStop is not null)
                hostStop.Cancel();
            else
                hostTask = StartHostAsync();
        };
        clientButton.Click += async (_, _) =>
        {
            if (reconnectPending)
            {
                reconnectPending = false;
                clientStatus.Text = "Desconectado";
                clientButton.Text = "Conectar";
            }
            else if (clientStop is { } stop)
            {
                reconnectAttempts = MaxReconnects;
                await StopClientAsync(stop);
            }
            else
            {
                reconnectAttempts = 0;
                clientTask = StartClientAsync();
            }
        };
        copy.Click += (_, _) => CopyAccessCode();
        clientInput.CheckedChanged += (_, _) => RefreshDesktopInput();
        revealCode.Click += (_, _) =>
        {
            hostCode.UseSystemPasswordChar = !hostCode.UseSystemPasswordChar;
            revealCode.Text = hostCode.UseSystemPasswordChar ? "Mostrar" : "Ocultar";
        };
        hostAddress.DropDown += (_, _) =>
        {
            if (hostStop is null)
                RefreshAddresses();
        };
        RefreshAddresses();
        AppSettings? settings = AppSettings.Load(settingsPath);
        if (settings is not null)
            RestoreSettings(settings);
        InitializeControllers(settings);
        ConfigurePriorities();
        InitializeLiveSettings();
        InitializeClientSettings();
        updateUi.Tick += (_, _) => RefreshStatus();
        updateUi.Start();
    }

    private void FillDisplayAndAudioLists()
    {
        Screen[] screens = VideoFeed.ScreenList();
        for (int index = 0; index < screens.Length; index++)
        {
            displayIds.Add(screens[index].DeviceName);
            hostDisplay.Items.Add($"Pantalla {index + 1}{(screens[index].Primary ? " (principal)" : "")} · " +
                $"{screens[index].Bounds.Width}×{screens[index].Bounds.Height}");
        }
        if (screens.Length > 0)
            hostDisplay.SelectedIndex = 0;

        hostAudioDevice.Items.Add("Predeterminado del sistema");
        audioDeviceIds.Add(null);
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    hostAudioDevice.Items.Add(device.FriendlyName);
                    audioDeviceIds.Add(device.ID);
                }
            }
        }
        catch (COMException ex) { Trace.Note($"No se pudieron enumerar las salidas de audio: {ex.Message}"); }
        hostAudioDevice.SelectedIndex = 0;
    }

    private static NumericUpDown PortField() =>
        new() { Minimum = 1, Maximum = 65535, Value = DefaultPort, Width = 80 };

    private void RefreshAddresses()
    {
        var previous = (hostAddress.SelectedItem as BindAddress)?.Address;
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .Select(adapter => (adapter.Name, Properties: adapter.GetIPProperties()))
            .OrderByDescending(adapter => adapter.Properties.GatewayAddresses
                .Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork))
            .SelectMany(adapter => adapter.Properties.UnicastAddresses
                .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(address.Address))
                .Select(address => new BindAddress(address.Address, adapter.Name)))
            .DistinctBy(entry => entry.Address)
            .Append(new BindAddress(IPAddress.Loopback, "Este equipo"))
            .ToArray();
        hostAddress.Items.Clear();
        hostAddress.Items.AddRange(addresses);
        hostAddress.SelectedIndex = Math.Max(0, Array.FindIndex(addresses, item => item.Address.Equals(previous)));
    }

    private sealed record BindAddress(IPAddress Address, string Adapter)
    {
        public override string ToString() => $"{Address} · {Adapter}";
    }

    private void CopyAccessCode()
    {
        try
        {
            Clipboard.SetDataObject(hostCode.Text, copy: true, retryTimes: 5, retryDelay: 60);
            copyFeedback.Text = "Código copiado";
        }
        catch (ExternalException)
        {
            copyFeedback.Text = "Portapapeles ocupado. Reintenta o pulsa Mostrar.";
        }
        copyFeedback.Visible = true;
    }

    private void CopyHostAddress()
    {
        if (hostAddress.SelectedItem is not BindAddress bind)
            return;
        try
        {
            Clipboard.SetDataObject(bind.Address.ToString(), copy: true, retryTimes: 5, retryDelay: 60);
            copyFeedback.Text = bind.Address.Equals(IPAddress.Loopback) ? "Copiada · solo este equipo" : "IP copiada";
        }
        catch (ExternalException) { copyFeedback.Text = "Portapapeles ocupado. Reintenta."; }
        copyFeedback.Visible = true;
    }

    private async Task StartHostAsync()
    {
        if (hostAddress.SelectedItem is not BindAddress bind)
            return;
        int port = (int)hostPort.Value;
        if (RemoteSession.IsReservedPort(port))
        {
            hostStatus.Text = RemoteSession.ReservedPortMessage;
            return;
        }
        if (!ValidateAudioSelection()) return;
        using var stop = new CancellationTokenSource();
        hostStop = stop;
        hostMousePriority = new HostMousePriority();
        UpdateLocalPriority();
        liveHostSettings = new LiveHostSettings(ReadHostOptions());
        HostOptions options = liveHostSettings.Current.Options;
        Func<string, string, CancellationToken, Task<bool>> approve = AskApprovalAsync;
        hostAddress.Enabled = false;
        hostPort.ReadOnly = true;
        hostPort.Increment = 0;
        RefreshAudioControls();
        hostButton.Text = "Dejar de compartir";
        hostStatus.Text = "Preparando sesión…";
        try
        {
            using var mouseMonitor = new HostMouseMonitor(hostMousePriority, point =>
                clientStop is not null && video.Visible && clientInput.Checked &&
                video.IsHandleCreated && video.RectangleToScreen(video.ClientRectangle).Contains(point));
            using var cert = await Task.Run(RemoteSession.CreateCertificate, stop.Token);
            AccessCode code = AccessCode.Create(cert);
            hostCode.Text = code.ToString();
            copy.Enabled = revealCode.Enabled = true;
            await Task.Run(() => RemoteSession.HostAsync(bind.Address, port, cert, code,
                status => Post(() => hostStatus.Text = status), status => Post(() => hostPad.Text = status),
                stop.Token, status => Post(() => hostVideo.Text = status), options,
                text => Post(() => hostExtras.Text = text), (guest, connected) => Post(() =>
                {
                    if (ReferenceEquals(hostStop, stop))
                        SetGuest(guest, connected);
                }), approve), stop.Token);
        }
        catch (Exception) when (stop.IsCancellationRequested)
        {
            hostStatus.Text = "Pantalla sin compartir";
        }
        catch (Exception ex)
        {
            hostStatus.Text = ex.Message;
        }
        finally
        {
            hostStop = null;
            hostMousePriority = null;
            liveHostSettings = null;
            applyHostChanges.Enabled = false;
            liveSettingsStatus.Text = "Durante una sesión puedes editar estos ajustes y aplicarlos sin cambiar el código.";
            UpdateLocalPriority();
            ClearGuests();
            hostCode.Clear();
            copy.Enabled = revealCode.Enabled = false;
            hostCode.UseSystemPasswordChar = true;
            revealCode.Text = "Mostrar";
            copyFeedback.Visible = false;
            hostAddress.Enabled = hostPort.Enabled = hostAudio.Enabled = hostInput.Enabled = true;
            hostPort.ReadOnly = false;
            hostPort.Increment = 1;
            hostBandwidth.Enabled = hostMaxFps.Enabled = hostEncoder.Enabled = true;
            hostDisplay.Enabled = hostAudioDevice.Enabled = hostIdle.Enabled = hostLock.Enabled = hostLocal1440.Enabled = hostApprove.Enabled = true;
            RefreshAudioControls();
            hostButton.Text = "Compartir pantalla";
            hostPad.Text = "Sin mando remoto";
            hostVideo.Text = "";
            hostExtras.Text = "";
        }
    }

    private async Task StartClientAsync()
    {
        if (clientStop is not null)
            return;
        int port = (int)clientPort.Value;
        if (RemoteSession.IsReservedPort(port))
        {
            clientStatus.Text = RemoteSession.ReservedPortMessage;
            return;
        }
        string target = clientAddress.Text.Trim();
        IPAddress.TryParse(target, out var address);
        if (target.Length is 0 or > 253 ||
            (address is not null && address.AddressFamily != AddressFamily.InterNetwork))
        {
            clientStatus.Text = "Ingresa la IPv4 o el nombre del anfitrión";
            return;
        }
        AccessCode code;
        try { code = AccessCode.Parse(clientCode.Text); }
        catch (FormatException)
        {
            clientStatus.Text = "Código de acceso inválido";
            return;
        }

        using var stop = new CancellationTokenSource();
        clientStop = stop;
        var input = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(512)
            { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        var options = new ClientOptions(clientAudio.Checked, input,
            Qualities.Encode(clientResolution.SelectedIndex, clientFps.SelectedIndex), (VideoDecoder)clientDecoder.SelectedIndex,
            (milliseconds, padActive, devices) =>
            {
                Volatile.Write(ref networkSample,
                    double.IsFinite(milliseconds) ? new NetworkSample(milliseconds, padActive, Stopwatch.GetTimestamp()) : null);
                Post(() =>
                {
                    if (!ReferenceEquals(clientStop, stop))
                        return;
                    if (devices is { } granted)
                    {
                        liveDesktopPermissions = true;
                        hostDesktopPermissions = granted;
                        clientInput.Enabled = !closing;
                    }
                    else if (liveDesktopPermissions)
                        hostDesktopPermissions = GuestDevices.None;
                    RefreshDesktopInput();
                });
            },
            allowed => Post(() =>
            {
                if (ReferenceEquals(clientStop, stop))
                    video.CaptureInput = allowed;
            }), GuestSession.CleanName(clientName.Text), clientInput.Checked);
        Volatile.Write(ref inputQueue, input);
        bool lostConnection = false;
        long connectedAt = 0;
        video.CaptureInput = false;
        activeClientProfile = ReadClientProfile();
        clientConnected = false;
        SetClientConnectionFields(readOnly: true);
        clientInput.Enabled = false;
        MarkClientChanges();
        clientButton.Text = "Desconectar";
        clientStatus.Text = VideoFeed.HasFFmpeg ? "Conectando…" : "Conectando… (falta la carpeta ffmpeg)";
        video.SetPlaceholder("Conectando…");
        try
        {
            if (address is null)
            {
                clientStatus.Text = "Buscando el anfitrión…";
                address = (await Dns.GetHostAddressesAsync(target, stop.Token))
                    .FirstOrDefault(found => found.AddressFamily == AddressFamily.InterNetwork);
                if (address is null)
                    throw new IOException("El anfitrión no tiene una dirección IPv4.");
            }
            await Task.Run(() => RemoteSession.ConnectAsync(address, port, code,
                () => Post(() =>
                {
                    if (!ReferenceEquals(clientStop, stop)) return;
                    clientConnected = true;
                    connectedAt = Environment.TickCount64;
                    MarkClientChanges();
                    clientStatus.Text = "Conectado";
                    OpenViewer();
                }),
                QueueFrame, () => controllers?.Poll(), stop.Token,
                codec => Post(() => clientCodec = codec), options,
                text => Post(() => clientExtras.Text = text),
                stage => Post(() =>
                {
                    if (!ReferenceEquals(clientStop, stop) || stage == ConnectionStage.Streaming)
                        return;
                    clientStatus.Text = stage switch
                    {
                        ConnectionStage.Connecting => "Contactando con el anfitrión por UDP…",
                        ConnectionStage.SendingRequest => "Enviando solicitud de acceso…",
                        ConnectionStage.AwaitingApproval => "Esperando que el anfitrión pulse Admitir (30 segundos)…",
                        _ => "Acceso autorizado. Esperando video…"
                    };
                })), stop.Token);
        }
        catch (Exception) when (stop.IsCancellationRequested)
        {
            clientStatus.Text = "Desconectado";
        }
        catch (AuthenticationException)
        {
            clientStatus.Text = "El código no coincide con el anfitrión";
        }
        catch (EndOfStreamException)
        {
            clientStatus.Text = "El anfitrión cerró la sesión";
        }
        catch (OperationCanceledException)
        {
            clientStatus.Text = "Se agotó el tiempo de espera";
            lostConnection = clientConnected;
        }
        catch (Exception ex)
        {
            clientStatus.Text = ex.Message;
            lostConnection = clientConnected;
        }
        finally
        {
            clientStop = null;
            clientConnected = false;
            liveDesktopPermissions = false;
            hostDesktopPermissions = GuestDevices.None;
            CloseViewer();
            Interlocked.Exchange(ref pendingFrame, null)?.Dispose();
            video.SetFrame(null);
            video.SetPlaceholder("Conecta con el anfitrión para ver su pantalla");
            clientCodec = "";
            Volatile.Write(ref networkSample, null);
            framesPresented = 0;
            clientVideo.Text = "Video sin señal";
            video.CaptureInput = false;
            Volatile.Write(ref inputQueue, null);
            input.Writer.TryComplete();
            SetClientConnectionFields(readOnly: false);
            clientAudio.Enabled = clientInput.Enabled = true;
            clientResolution.Enabled = clientFps.Enabled = clientDecoder.Enabled = true;
            clientExtras.Text = "";
            clientButton.Text = "Conectar";
            MarkClientChanges();
        }

        // A drop in the middle of a session is retried by itself; a stable session earns back the attempts.
        if (connectedAt != 0 && Environment.TickCount64 - connectedAt > 60_000)
            reconnectAttempts = 0;
        if (lostConnection && !closing && reconnectAttempts < MaxReconnects)
        {
            reconnectAttempts++;
            ConnectionLog.Note($"Cliente · conexión perdida; reconexión automática {reconnectAttempts}/{MaxReconnects}.");
            reconnectPending = true;
            clientButton.Text = "Cancelar";
            clientStatus.Text = $"Conexión perdida. Reconectando ({reconnectAttempts}/{MaxReconnects})…";
            await Task.Delay(2000);
            if (reconnectPending && !closing)
            {
                reconnectPending = false;
                clientTask = StartClientAsync();
            }
        }
    }

    private void RefreshDesktopInput()
    {
        if (!liveDesktopPermissions)
            return;
        if (!clientInput.Checked && video.CaptureInput)
            Volatile.Read(ref inputQueue)?.Writer.TryWrite(InputFeed.Packet(InputFeed.ReleaseAll));
        video.DesktopDevices = clientInput.Checked ? hostDesktopPermissions : GuestDevices.None;
    }

    private async Task StopClientAsync(CancellationTokenSource stop)
    {
        if (stop.IsCancellationRequested)
            return;
        clientStatus.Text = "Desconectando…";
        try { await stop.CancelAsync(); }
        catch (AggregateException ex) { Trace.Note($"Error al desconectar el cliente: {ex.Message}"); }
    }

    // Asks the host owner to admit or deny a guest who already knows the access code.
    private async Task<bool> AskApprovalAsync(string name, string endpoint, CancellationToken token)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = token.Register(() => result.TrySetResult(false));
        Form? dialog = null;
        try
        {
            Post(() =>
            {
                if (result.Task.IsCompleted || closing || IsDisposed)
                {
                    result.TrySetResult(false);
                    return;
                }
                dialog = CreateApprovalDialog(name, endpoint);
                dialog.FormClosed += (_, _) => result.TrySetResult(dialog.DialogResult == DialogResult.Yes);
                dialog.Show(this);
            });
            return await result.Task && !token.IsCancellationRequested;
        }
        finally
        {
            Post(() =>
            {
                if (dialog is not null && !dialog.IsDisposed)
                    dialog.Close();
            });
        }
    }

    private void Post(Action update)
    {
        if (IsHandleCreated && !IsDisposed)
            BeginInvoke(update);
    }

    private void QueueFrame(VideoFrame frame)
    {
        if (Interlocked.Exchange(ref pendingFrame, frame) is { } replaced)
        {
            replaced.Dispose();
            if (Trace.Enabled) Trace.Log("client_frame_dropped", 1);
        }
        ScheduleFrame();
    }

    private void ScheduleFrame()
    {
        if (Interlocked.Exchange(ref renderQueued, 1) == 0)
            Post(PresentLatestFrame);
    }

    private void PresentLatestFrame()
    {
        if (Interlocked.Exchange(ref pendingFrame, null) is { } next)
        {
            video.SetFrame(next);
            video.Update();
        }
        Volatile.Write(ref renderQueued, 0);
        if (Volatile.Read(ref pendingFrame) is not null)
            ScheduleFrame();
    }

    private void RefreshStatus()
    {
        mousePriorityStatus.Text = hostMousePriority?.Active == true
            ? "Tu mouse está al mando. El mouse de los invitados queda en pausa hasta un segundo después de soltarlo."
            : "Tu mouse tiene prioridad. Los invitados pueden moverlo mientras tú no lo uses.";
        if (fpsInterval.Elapsed.TotalSeconds >= 1)
        {
            if (video.FrameSize is { } size)
            {
                clientVideo.Text = $"{clientCodec}  ·  {size.Width} × {size.Height}  ·  {framesPresented / fpsInterval.Elapsed.TotalSeconds:0} FPS";
                NetworkSample? sample = Volatile.Read(ref networkSample);
                if (sample is not null && Stopwatch.GetElapsedTime(sample.ReceivedAt) > TimeSpan.FromSeconds(3))
                    sample = null;
                if (sample is not null)
                    clientVideo.Text += $"  ·  Red {sample.Milliseconds:0.0} ms";
                if (clientCodec == "JPEG" && !VideoFeed.HasFFmpeg)
                    clientVideo.Text += "  ·  Falta la carpeta ffmpeg junto al programa";
                bool? padActive = clientPad.SelectedIndex == 0 ? false : sample?.PadActive;
                viewer?.SetStatus(clientVideo.Text, video.DesktopDevices, padActive);
            }
            framesPresented = 0;
            fpsInterval.Restart();
        }
        RefreshControllerReadout();
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (!readyToClose && (controllers is not null || !hostTask.IsCompleted || !clientTask.IsCompleted || !clientApplyTask.IsCompleted || !networkPriorityTask.IsCompleted))
        {
            e.Cancel = true;
            if (!closing)
            {
                closing = true;
                using Form notice = ShowClosingNotice();
                Enabled = false;
                await Task.Yield();
                Task stopHost = hostStop?.CancelAsync() ?? Task.CompletedTask;
                Task stopClient = clientStop?.CancelAsync() ?? Task.CompletedTask;
                Task stopControllers = controllers?.StopAsync() ?? Task.CompletedTask;
                await Task.WhenAll(stopHost, stopClient, stopControllers, hostTask, clientTask, clientApplyTask, networkPriorityTask)
                    .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
                readyToClose = true;
                notice.Close();
                Close();
            }
            return;
        }
        updateUi.Dispose();
        Interlocked.Exchange(ref pendingFrame, null)?.Dispose();
        base.OnFormClosing(e);
    }
}
