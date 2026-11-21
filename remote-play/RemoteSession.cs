using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace RemotePlay;

internal sealed record AccessCode(byte[] Fingerprint, byte[] Secret)
{
    private const int HashBytes = 32;
    private const int SecretBytes = 24;

    internal static AccessCode Create(X509Certificate2 cert) =>
        new(cert.GetCertHash(HashAlgorithmName.SHA256), RandomNumberGenerator.GetBytes(SecretBytes));

    public override string ToString() => Convert.ToBase64String([.. Fingerprint, .. Secret]);

    internal static AccessCode Parse(string text)
    {
        byte[] data = Convert.FromBase64String(text);
        if (data.Length != HashBytes + SecretBytes)
            throw new FormatException("Código de acceso inválido.");
        return new AccessCode(data[..HashBytes], data[HashBytes..]);
    }
}

internal enum ConnectionStage { Connecting, SendingRequest, AwaitingApproval, StartingVideo, Streaming }

internal static class RemoteSession
{
    internal const string ReservedPortMessage = AppBrand.Name + " no utiliza los puertos 22 ni 663. Elige otro puerto.";
    private static ReadOnlySpan<byte> Protocol => "SRP1"u8;
    private const int PingInterval = 32;
    private const long VideoProfileChanged = 0x53525031;
    // Over the internet the guest's input can stall for 2-3 s right after the video starts, so 2 s dropped working guests.
    private static readonly TimeSpan InputSilenceLimit = TimeSpan.FromSeconds(25);
    private static readonly Task NoProfileChange = Task.Delay(Timeout.InfiniteTimeSpan);
    private static readonly SslApplicationProtocol DynamicVideoProtocol = new("remote-play/6");
    private static readonly SslApplicationProtocol SharedVideoProtocol = new("remote-play/5");
    private static readonly SslApplicationProtocol GuestProtocol = new("remote-play/4");
    private static readonly SslApplicationProtocol LatencyProtocol = new("remote-play/3");
    private static readonly SslApplicationProtocol LegacyProtocol = new("remote-play/1");
    private static readonly List<SslApplicationProtocol> ApplicationProtocols =
        [DynamicVideoProtocol, SharedVideoProtocol, GuestProtocol, LatencyProtocol, new("remote-play/2"), LegacyProtocol];

    private static bool SharesVideo(SslApplicationProtocol protocol) => protocol == DynamicVideoProtocol || protocol == SharedVideoProtocol;
    private static bool ManagesGuests(SslApplicationProtocol protocol) => SharesVideo(protocol) || protocol == GuestProtocol;
    private static bool UsesLowLatency(SslApplicationProtocol protocol) => ManagesGuests(protocol) || protocol == LatencyProtocol;

    internal static bool IsReservedPort(int port) => port is 22 or 663;

    private static void ValidatePort(int port)
    {
        if (IsReservedPort(port))
            throw new ArgumentException(ReservedPortMessage, nameof(port));
    }

    internal static X509Certificate2 CreateCertificate()
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest("CN=RemotePlay", key, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        using var generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(2));
        // Schannel needs a named key that it can open outside this process. Windows removes the
        // temporary user key when the imported certificate is disposed.
        byte[] pkcs12 = generated.Export(X509ContentType.Pkcs12);
        try { return X509CertificateLoader.LoadPkcs12(pkcs12, null, X509KeyStorageFlags.UserKeySet); }
        finally { CryptographicOperations.ZeroMemory(pkcs12); }
    }

    internal static async Task HostAsync(IPAddress address, int port, X509Certificate2 cert, AccessCode code,
        Action<string> status, Action<string> padStatus, CancellationToken stop, Action<string>? videoStatus = null,
        HostOptions options = default, Action<string>? extrasStatus = null, Action<GuestSession, bool>? guests = null,
        Func<string, string, CancellationToken, Task<bool>>? approve = null)
    {
        ValidatePort(port);
        CheckQuic();
        videoStatus ??= _ => { };
        extrasStatus ??= _ => { };
        var certificate = SslStreamCertificateContext.Create(cert, additionalCertificates: null, offline: true);
        await using var listener = await QuicListener.ListenAsync(new QuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(address, port),
            ListenBacklog = GuestSession.Limit * 2,
            ApplicationProtocols = ApplicationProtocols,
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
            {
                DefaultCloseErrorCode = 0,
                DefaultStreamErrorCode = 0,
                MaxInboundBidirectionalStreams = 2,
                MaxInboundUnidirectionalStreams = 0,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificateContext = certificate,
                    ApplicationProtocols = ApplicationProtocols,
                    EnabledSslProtocols = SslProtocols.Tls13
                }
            })
        }, stop);
        status($"Esperando cliente en {listener.LocalEndPoint}");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        using var slots = new SemaphoreSlim(GuestSession.Limit, GuestSession.Limit);
        var sharedVideo = new SharedVideoHub();
        LocalVideoLimit? localLimit = options.LimitLocal1440 || options.Live is not null ? new LocalVideoLimit() : null;
        var sessions = new List<Task>();
        int ordinal = 0;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                QuicConnection client;
                try { client = await listener.AcceptConnectionAsync(lifetime.Token); }
                catch (Exception ex) when (!lifetime.IsCancellationRequested && ex is IOException or AuthenticationException or SocketException)
                {
                    ConnectionLog.Write("Anfitrión", "Conexión segura entrante", ex);
                    Trace.Note($"Conexión entrante rechazada. {ex.Message}");
                    continue;
                }
                sessions.RemoveAll(session => session.IsCompleted);
                if (sessions.Count >= GuestSession.Limit * 2)
                    await client.DisposeAsync();
                else
                    sessions.Add(HandleGuestAsync(client, ++ordinal));
            }
        }
        finally
        {
            await lifetime.CancelAsync();
            await Task.WhenAll(sessions).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        async Task HandleGuestAsync(QuicConnection client, int id)
        {
            try
            {
                await using (client)
                using (var session = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                {
                    LocalVideoLimit? guestLimit = client.NegotiatedApplicationProtocol == DynamicVideoProtocol &&
                        client.RemoteEndPoint is IPEndPoint peer && IsLocalPeer(peer.Address, address) ? localLimit : null;
                    await ServeClientAsync(client, code, id, slots, padStatus, videoStatus, options, extrasStatus, guests,
                        session, sharedVideo, guestLimit,
                        client.RemoteEndPoint is IPEndPoint origin && IsLocalPeer(origin.Address, address) ? null : approve);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { /* Host shutdown. */ }
            catch (Exception ex)
            {
                ConnectionLog.Write("Anfitrión", $"Sesión de invitado {id}", ex);
                Trace.Note($"Sesión de invitado terminada. {ex.Message}");
                status($"Invitado desconectado · {ex.Message}");
            }
        }
    }

    private static bool IsLocalPeer(IPAddress peer, IPAddress bound)
    {
        if (peer.IsIPv4MappedToIPv6)
            peer = peer.MapToIPv4();
        return IPAddress.IsLoopback(peer) || peer.Equals(bound);
    }

    private static async Task ServeClientAsync(QuicConnection client, AccessCode code, int id, SemaphoreSlim slots,
        Action<string> padStatus, Action<string> videoStatus, HostOptions options, Action<string> extras,
        Action<GuestSession, bool>? guests, CancellationTokenSource session, SharedVideoHub sharedVideo, LocalVideoLimit? localLimit,
        Func<string, string, CancellationToken, Task<bool>>? approve = null)
    {
        CancellationToken stop = session.Token;
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(stop);
        handshake.CancelAfter(TimeSpan.FromSeconds(5));
        await using var input = await client.AcceptInboundStreamAsync(handshake.Token);
        bool modern = client.NegotiatedApplicationProtocol != LegacyProtocol;
        bool managed = ManagesGuests(client.NegotiatedApplicationProtocol);
        bool lowLatency = UsesLowLatency(client.NegotiatedApplicationProtocol);
        SharedVideoHub? shared = SharesVideo(client.NegotiatedApplicationProtocol) ? sharedVideo : null;
        var login = new byte[Protocol.Length + code.Secret.Length + (modern ? 1 : 0)];
        await input.ReadExactlyAsync(login, handshake.Token);
        byte flags = modern ? login[^1] : (byte)0;
        if ((flags & ~SessionFlags.Known) != 0 || !login.AsSpan(0, Protocol.Length).SequenceEqual(Protocol)
            || !CryptographicOperations.FixedTimeEquals(login.AsSpan(Protocol.Length, code.Secret.Length), code.Secret))
        {
            await input.WriteAsync(new byte[] { 0 }, completeWrites: true, handshake.Token);
            await input.WritesClosed.WaitAsync(handshake.Token);
            return;
        }
        VideoQuality quality = Qualities.Decode(Qualities.DefaultCode);
        if (modern && (flags & SessionFlags.Quality) != 0)
        {
            var choice = new byte[1];
            await input.ReadExactlyAsync(choice, handshake.Token);
            quality = Qualities.Decode(choice[0]);
        }
        if (options.Live is null)
            quality = Qualities.Limit(quality, options.BandwidthMbps, options.MaxFps);
        HostOptions initialOptions = options.Live?.Current.Options ?? options;
        string name = managed ? await ReadGuestNameAsync(input, handshake.Token) : "";
        if (approve is not null && (options.Live is null || initialOptions.RequireApproval))
        {
            // The host decides who comes in; the login timer is paused while it decides.
            handshake.CancelAfter(Timeout.InfiniteTimeSpan);
            string who = name.Length == 0 ? $"Invitado {id}" : name;
            bool allowed;
            using (var decision = CancellationTokenSource.CreateLinkedTokenSource(stop))
            {
                decision.CancelAfter(TimeSpan.FromSeconds(30));
                try { allowed = await approve(who, client.RemoteEndPoint.ToString() ?? "", decision.Token).WaitAsync(decision.Token); }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested) { allowed = false; }
            }
            handshake.CancelAfter(TimeSpan.FromSeconds(5));
            if (!allowed)
            {
                await input.WriteAsync(new byte[] { 3 }, completeWrites: true, handshake.Token);
                await input.WritesClosed.WaitAsync(handshake.Token);
                return;
            }
        }
        if (!await slots.WaitAsync(0, stop))
        {
            await input.WriteAsync(new byte[] { 2 }, completeWrites: true, handshake.Token);
            await input.WritesClosed.WaitAsync(handshake.Token);
            return;
        }
        initialOptions = options.Live?.Current.Options ?? options;
        var defaults = GuestDevices.Pad | (initialOptions.Input ? GuestDevices.Desktop : GuestDevices.None);
        var guest = new GuestSession(name.Length == 0 ? $"Invitado {id}" : name,
            client.RemoteEndPoint.ToString() ?? "", defaults, (flags & SessionFlags.Input) != 0, managed, session,
            supportsLiveVideo: client.NegotiatedApplicationProtocol == DynamicVideoProtocol, mousePriority: options.MousePriority);

        var features = new FeatureStatus(extras);
        var idle = new IdleTracker(initialOptions.IdleMinutes,
            options.Live is { } live ? () => live.Current.Options.IdleMinutes : null);
        using var side = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var sideTasks = new List<Task>();
        bool announced = false;
        bool joinedLimit = false;
        try
        {
            options.Live?.Register(guest);
            await input.WriteAsync(new byte[] { 1 }, completeWrites: !lowLatency, handshake.Token);
            if (localLimit is not null)
            {
                localLimit.Join();
                joinedLimit = true;
            }
            handshake.CancelAfter(Timeout.InfiniteTimeSpan);
            guests?.Invoke(guest, true);
            announced = true;
            if (modern && (flags & SessionFlags.Audio) != 0)
            {
                QuicStream audio = await client.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, side.Token);
                sideTasks.Add(Task.Run(async () =>
                {
                    await using (audio)
                        await RunSideAsync(() => options.Live is { } liveAudio
                            ? LiveAudioFeed.SendAsync(audio, liveAudio, text => features.Set("audio", text), side.Token)
                            : AudioFeed.SendAsync(audio, options.Audio,
                                text => features.Set("audio", text), side.Token, options.AudioDevice, options.AudioMode, options.AudioApplication),
                            text => features.Set("audio", text), side.Token);
                }, CancellationToken.None)); // The delegate owns and disposes the stream even on cancellation.
            }
            if (modern && (flags & SessionFlags.Input) != 0)
            {
                try
                {
                    using var accept = CancellationTokenSource.CreateLinkedTokenSource(stop);
                    accept.CancelAfter(TimeSpan.FromSeconds(5));
                    QuicStream remote = await client.AcceptInboundStreamAsync(accept.Token);
                    sideTasks.Add(Task.Run(async () =>
                    {
                        await using (remote)
                            await RunSideAsync(() => InputFeed.HostAsync(remote, guest, managed,
                                text => features.Set("input", text), side.Token, idle),
                                text => features.Set("input", text), side.Token);
                    }, CancellationToken.None)); // The delegate owns and disposes the stream even on cancellation.
                }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested)
                {
                    features.Set("input", "Mouse y teclado: el invitado no abrió el canal");
                }
            }

            await RunDuplexAsync(token => SendVideoAsync(client, modern, modern && (flags & SessionFlags.H264) != 0,
                videoStatus, quality, options, lowLatency, shared, localLimit, token),
                token => ReceivePadAsync(input, padStatus, token, idle, lowLatency, managed, guest), stop);
        }
        finally
        {
            options.Live?.Unregister(guest);
            if (joinedLimit)
                localLimit!.Leave();
            await side.CancelAsync();
            await Task.WhenAll(sideTasks).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            features.Clear();
            await guest.Finish().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (announced)
                guests?.Invoke(guest, false);
            int previous = slots.Release();
            if ((options.Live?.Current.Options.LockOnDisconnect ?? options.LockOnDisconnect) && announced &&
                previous == GuestSession.Limit - 1 && !stop.IsCancellationRequested)
                HostActions.Lock();
        }
    }

    private static async Task SendVideoAsync(QuicConnection client, bool modern, bool wantsH264,
        Action<string> status, VideoQuality quality, HostOptions options, bool lowLatency,
        SharedVideoHub? shared, LocalVideoLimit? localLimit, CancellationToken stop)
    {
        do
        {
            LiveHostSettings.State? settings = options.Live?.Current;
            HostOptions current = settings?.Options ?? options;
            var local = localLimit?.Snapshot();
            VideoQuality effective = EffectiveVideo(quality, current, wantsH264, local is { Limited: true });
            await using var video = await client.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, stop);
            using var segment = CancellationTokenSource.CreateLinkedTokenSource(stop);
            Task send = VideoFeed.SendAsync(video, modern, wantsH264, status, segment.Token, effective,
                current.Encoder, current.Display, lowLatency, shared);
            bool switching = false;
            try
            {
                while (!send.IsCompleted && client.NegotiatedApplicationProtocol == DynamicVideoProtocol &&
                    (settings is not null || local is not null))
                {
                    Task changedVideo = settings?.VideoChanged ?? NoProfileChange;
                    Task changedLocal = local?.Changed ?? NoProfileChange;
                    if (await Task.WhenAny(send, changedVideo, changedLocal) == send || stop.IsCancellationRequested)
                        break;
                    settings = options.Live?.Current;
                    HostOptions updated = settings?.Options ?? options;
                    local = localLimit?.Snapshot();
                    VideoQuality next = EffectiveVideo(quality, updated, wantsH264, local is { Limited: true });
                    if (next != effective || updated.Encoder != current.Encoder || updated.Display != current.Display)
                    {
                        video.Abort(QuicAbortDirection.Write, VideoProfileChanged);
                        switching = true;
                        break;
                    }
                }
                if (!switching)
                {
                    await send;
                    return;
                }
            }
            finally
            {
                await segment.CancelAsync();
                await send.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        } while (!stop.IsCancellationRequested);
    }

    private static VideoQuality EffectiveVideo(VideoQuality requested, HostOptions options, bool h264, bool limited)
    {
        VideoQuality quality = Qualities.Limit(requested, options.BandwidthMbps, options.MaxFps);
        return h264 && options.LimitLocal1440 && limited && quality.Height > 1080 && quality.Fps > 60
            ? quality with { Fps = 60 } : quality;
    }

    private static async Task<string> ReadGuestNameAsync(Stream stream, CancellationToken stop)
    {
        var length = new byte[1];
        await stream.ReadExactlyAsync(length, stop);
        if (length[0] > GuestSession.NameBytes)
            throw new IOException("Nombre de invitado demasiado largo.");
        var encoded = new byte[length[0]];
        await stream.ReadExactlyAsync(encoded, stop);
        return GuestSession.CleanName(Encoding.UTF8.GetString(encoded));
    }

    // Audio and input are extras: a failure there is reported but never ends the session.
    private static async Task RunSideAsync(Func<Task> work, Action<string> report, CancellationToken stop)
    {
        try { await work(); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { /* Normal session shutdown. */ }
        catch (Exception ex) { report(ex.Message); }
    }

    internal static async Task ConnectAsync(IPAddress host, int port, AccessCode code, Action connected,
        Action<VideoFrame> onFrame, Func<PadState?> pollPad, CancellationToken stop, Action<string>? videoStatus = null,
        ClientOptions options = default, Action<string>? extrasStatus = null, Action<ConnectionStage>? progress = null)
    {
        int stage = (int)ConnectionStage.Connecting;
        void Report(ConnectionStage current)
        {
            Volatile.Write(ref stage, (int)current);
            progress?.Invoke(current);
        }
        try
        {
            await ConnectCoreAsync(host, port, code, connected, onFrame, pollPad, stop, videoStatus,
                options, extrasStatus, Report);
        }
        catch (Exception ex) when (!stop.IsCancellationRequested && ex is OperationCanceledException or
            QuicException { QuicError: QuicError.ConnectionTimeout or QuicError.ConnectionIdle })
        {
            ConnectionLog.Write("Cliente", ((ConnectionStage)Volatile.Read(ref stage)).ToString(), ex);
            string message = (ConnectionStage)Volatile.Read(ref stage) switch
            {
                ConnectionStage.Connecting => "El anfitrión no respondió al conectar por UDP. Comprueba la IP y el puerto.",
                ConnectionStage.SendingRequest => "Se agotó el tiempo al enviar la solicitud de acceso al anfitrión.",
                ConnectionStage.AwaitingApproval => "No llegó la respuesta de acceso del anfitrión mientras se esperaba la aprobación.",
                ConnectionStage.StartingVideo => "El acceso fue autorizado, pero no llegó el video del anfitrión a tiempo.",
                _ => "La transmisión dejó de responder después de conectar."
            };
            throw new TimeoutException(message, ex);
        }
        catch (QuicException ex) when (!stop.IsCancellationRequested && ex.QuicError == QuicError.StreamAborted)
        {
            ConnectionLog.Write("Cliente", ((ConnectionStage)Volatile.Read(ref stage)).ToString(), ex);
            throw new IOException($"El anfitrión cerró un canal de la sesión (código {ex.ApplicationErrorCode}). Revisa el motivo en el anfitrión.", ex);
        }
    }

    private static async Task ConnectCoreAsync(IPAddress host, int port, AccessCode code, Action connected,
        Action<VideoFrame> onFrame, Func<PadState?> pollPad, CancellationToken stop, Action<string>? videoStatus,
        ClientOptions options, Action<string>? extrasStatus, Action<ConnectionStage> progress)
    {
        ValidatePort(port);
        CheckQuic();
        progress(ConnectionStage.Connecting);
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(stop);
        handshake.CancelAfter(TimeSpan.FromSeconds(8));
        await using var client = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
        {
            RemoteEndPoint = new IPEndPoint(host, port),
            DefaultCloseErrorCode = 0,
            DefaultStreamErrorCode = 0,
            MaxInboundBidirectionalStreams = 0,
            MaxInboundUnidirectionalStreams = 2,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                TargetHost = "RemotePlay",
                ApplicationProtocols = ApplicationProtocols,
                EnabledSslProtocols = SslProtocols.Tls13,
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is not null && CryptographicOperations.FixedTimeEquals(
                        certificate.GetCertHash(HashAlgorithmName.SHA256), code.Fingerprint)
            }
        }, handshake.Token);
        progress(ConnectionStage.SendingRequest);
        await using var input = await client.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, handshake.Token);
        bool modern = client.NegotiatedApplicationProtocol != LegacyProtocol;
        bool managed = ManagesGuests(client.NegotiatedApplicationProtocol);
        bool lowLatency = UsesLowLatency(client.NegotiatedApplicationProtocol);
        bool wantsAudio = modern && options.Audio;
        bool wantsQuality = modern && VideoFeed.HasFFmpeg && options.Quality != Qualities.DefaultCode &&
            options.Quality is >= 0 and < 256;
        bool wantsInput = modern && options.Input is not null && (managed || options.SendDesktopInput);
        byte flags = (byte)((VideoFeed.HasFFmpeg ? SessionFlags.H264 : 0) |
            (wantsAudio ? SessionFlags.Audio : 0) | (wantsInput ? SessionFlags.Input : 0) |
            (wantsQuality ? SessionFlags.Quality : 0));
        byte[] login = modern ? [.. Protocol, .. code.Secret, flags] : [.. Protocol, .. code.Secret];
        if (wantsQuality)
            login = [.. login, (byte)options.Quality];
        if (managed)
        {
            byte[] name = GuestSession.EncodeName(options.GuestName);
            login = [.. login, (byte)name.Length, .. name];
        }
        await input.WriteAsync(login, handshake.Token);
        var reply = new byte[1];
        handshake.CancelAfter(TimeSpan.FromSeconds(45)); // The host may be asking its owner to admit this guest.
        progress(ConnectionStage.AwaitingApproval);
        await input.ReadExactlyAsync(reply, handshake.Token);
        if (reply[0] == 3)
            throw new IOException("El anfitrión no admitió tu conexión.");
        if (reply[0] == 2)
            throw new IOException("La sesión del anfitrión alcanzó el límite de invitados.");
        if (reply[0] != 1)
            throw new AuthenticationException("El anfitrión rechazó el código de acceso.");
        handshake.CancelAfter(Timeout.InfiniteTimeSpan);
        progress(ConnectionStage.StartingVideo);

        var features = new FeatureStatus(extrasStatus ?? (_ => { }));
        using var side = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var sideTasks = new List<Task>();
        try
        {
            if (lowLatency)
            {
                Action<double, bool, GuestDevices?> report = options.NetworkStatus ?? ((_, _, _) => { });
                sideTasks.Add(RunSideAsync(() => ReadLatencyAsync(input, report, managed, side.Token),
                    _ => report(double.NaN, false, null), side.Token));
            }
            if (wantsInput && options.Input is { } queue)
            {
                QuicStream remote = await client.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, stop);
                sideTasks.Add(Task.Run(async () =>
                {
                    await using (remote)
                        await RunSideAsync(() => InputFeed.ClientAsync(remote, queue.Reader,
                            text => features.Set("input", text), side.Token, options.InputState, managed),
                            text => features.Set("input", text), side.Token);
                }, CancellationToken.None)); // The delegate owns and disposes the stream even on cancellation.
            }

            bool highFrameRate = wantsQuality && Qualities.Decode(options.Quality).Fps > 60;
            await RunDuplexAsync(token => ReceiveVideoAsync(client, modern, wantsAudio, options.Decoder,
                    highFrameRate, () =>
                    {
                        progress(ConnectionStage.Streaming);
                        connected();
                    }, onFrame, videoStatus ?? (_ => { }), features, sideTasks, token),
                token => SendPadAsync(input, pollPad, token, lowLatency, managed), stop);
        }
        finally
        {
            await side.CancelAsync();
            await Task.WhenAll(sideTasks).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            features.Clear();
        }
    }

    private static async Task ReceiveVideoAsync(QuicConnection client, bool modern, bool audio, VideoDecoder decoder,
        bool highFrameRate, Action connected,
        Action<VideoFrame> onFrame, Action<string> videoStatus, FeatureStatus features, List<Task> sideTasks,
        CancellationToken stop)
    {
        do
        {
            try
            {
                await ReceiveVideoStreamAsync(client, modern, audio, decoder, highFrameRate, connected, onFrame,
                    videoStatus, features, sideTasks, stop);
                return;
            }
            catch (QuicException ex) when (client.NegotiatedApplicationProtocol == DynamicVideoProtocol &&
                ex.QuicError == QuicError.StreamAborted && ex.ApplicationErrorCode == VideoProfileChanged && !stop.IsCancellationRequested)
            {
                Trace.Note("Se reemplazó el canal de video por un cambio de FPS.");
            }
        } while (!stop.IsCancellationRequested);
    }

    private static async Task ReceiveVideoStreamAsync(QuicConnection client, bool modern, bool audio, VideoDecoder decoder,
        bool highFrameRate, Action connected, Action<VideoFrame> onFrame, Action<string> videoStatus,
        FeatureStatus features, List<Task> sideTasks, CancellationToken stop)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(stop);
        startup.CancelAfter(TimeSpan.FromSeconds(audio ? 20 : 8));
        QuicStream? video = null;
        byte? first = null;
        try
        {
            while (video is null)
            {
                QuicStream next = await client.AcceptInboundStreamAsync(startup.Token);
                if (!audio)
                {
                    video = next;
                    break;
                }

                var tag = new byte[1];
                try { await next.ReadExactlyAsync(tag, startup.Token); }
                catch
                {
                    await next.DisposeAsync();
                    throw;
                }

                if (tag[0] == AudioFeed.Tag)
                {
                    sideTasks.Add(Task.Run(async () =>
                    {
                        await using (next)
                            await RunSideAsync(() => AudioFeed.ReceiveAsync(next,
                                text => features.Set("audio", text), stop),
                                text => features.Set("audio", text), stop);
                    }, CancellationToken.None)); // The delegate owns and disposes the stream even on cancellation.
                }
                else
                {
                    video = next;
                    first = tag[0];
                }
            }

            startup.CancelAfter(Timeout.InfiniteTimeSpan);
            connected();
            bool lowLatency = UsesLowLatency(client.NegotiatedApplicationProtocol);
            bool shared = SharesVideo(client.NegotiatedApplicationProtocol);
            await VideoFeed.ReceiveAsync(video, modern, videoStatus, onFrame, stop, first, decoder, highFrameRate, lowLatency, shared);
        }
        finally
        {
            if (video is not null)
                await video.DisposeAsync();
        }
    }

    private static void CheckQuic()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            throw new PlatformNotSupportedException("Este prototipo requiere Windows 11 con soporte para QUIC.");
    }

    // Closing either stream ends the session and releases the virtual controller.
    private static async Task RunDuplexAsync(Func<CancellationToken, Task> readOrWrite,
        Func<CancellationToken, Task> otherDirection, CancellationToken stop)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop);
        Task first = readOrWrite(linked.Token);
        Task second = otherDirection(linked.Token);
        Task completed = await Task.WhenAny(first, second);
        try
        {
            await completed;
        }
        finally
        {
            await linked.CancelAsync();
            await Task.WhenAll(first, second).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static async Task SendPadAsync(Stream stream, Func<PadState?> poll, CancellationToken stop, bool lowLatency, bool managed)
    {
        using var timer = new InputPollTimer(managed);
        var packet = new byte[PadState.PacketSize + (lowLatency ? sizeof(long) : 0)];
        int counter = 0;
        do
        {
            PadState.Write(packet, poll());
            if (lowLatency)
            {
                // A timestamp is echoed on the same authenticated stream independently of the controller state.
                long stamp = counter == 0 ? Stopwatch.GetTimestamp() : 0;
                BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(PadState.PacketSize), stamp);
                counter = (counter + 1) % PingInterval;
            }
            await stream.WriteAsync(packet, stop);
        } while (await timer.WaitAsync(stop));
    }

    private static async Task ReadLatencyAsync(Stream stream, Action<double, bool, GuestDevices?> report, bool managed,
        CancellationToken stop)
    {
        var ping = new byte[sizeof(long) + 1];
        while (!stop.IsCancellationRequested)
        {
            await stream.ReadExactlyAsync(ping, stop);
            long sent = BinaryPrimitives.ReadInt64LittleEndian(ping);
            long received = Stopwatch.GetTimestamp();
            if (sent > 0 && sent <= received && Stopwatch.GetElapsedTime(sent, received).TotalSeconds <= 5)
            {
                var devices = (GuestDevices)ping[sizeof(long)];
                report(Stopwatch.GetElapsedTime(sent, received).TotalMilliseconds, devices.HasFlag(GuestDevices.Pad),
                    managed ? devices & GuestDevices.Desktop : null);
            }
        }
    }

    private static async Task ReceivePadAsync(Stream stream, Action<string> status, CancellationToken stop,
        IdleTracker idle, bool lowLatency, bool managed, GuestSession guest)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var packet = new byte[PadState.PacketSize + (lowLatency ? sizeof(long) : 0)];
        byte[]? ping = lowLatency ? new byte[sizeof(long) + 1] : null;
        VirtualPad? output = null;
        PadState? previous = null;
        bool driverFailed = false;
        var silence = Stopwatch.StartNew();
        long received = 0;
        lock (guest.Gate)
            guest.Revoked += Revoke;
        try
        {
            while (true)
            {
                deadline.CancelAfter(InputSilenceLimit);
                await stream.ReadExactlyAsync(packet, deadline.Token);
                TimeSpan gap = silence.Elapsed;
                silence.Restart();
                received++;
                if (gap > TimeSpan.FromMilliseconds(400))
                    Trace.Note($"Entrada del invitado: hueco de {gap.TotalMilliseconds:0} ms (paquete {received}).");
                if (idle.Expired)
                    throw new IOException("El invitado estuvo inactivo demasiado tiempo.");
                PadState? state = PadState.Read(packet);
                bool echo = ping is not null && BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(PadState.PacketSize)) != 0;
                lock (guest.Gate)
                {
                    UpdatePad(guest.Permissions.HasFlag(GuestDevices.Pad) ? state : null);
                    if (echo && ping is not null)
                    {
                        packet.AsSpan(PadState.PacketSize).CopyTo(ping);
                        GuestDevices active = output is not null ? GuestDevices.Pad : GuestDevices.None;
                        if (managed && guest.InputConnected)
                            active |= guest.EffectiveDesktopPermissions;
                        ping[sizeof(long)] = (byte)active;
                    }
                }
                if (echo && ping is not null)
                    await stream.WriteAsync(ping, deadline.Token);
            }
        }
        catch (OperationCanceledException ex) when (!stop.IsCancellationRequested)
        {
            Trace.Note($"Entrada del invitado: sin datos tras {received} paquetes; silencio de {silence.Elapsed.TotalMilliseconds:0} ms.");
            throw new TimeoutException($"No llegaron datos de entrada del invitado durante {InputSilenceLimit.TotalSeconds:0} segundos.", ex);
        }
        finally
        {
            lock (guest.Gate)
            {
                guest.Revoked -= Revoke;
                ReleasePad();
            }
        }

        void Revoke(GuestDevices revoked)
        {
            if (!revoked.HasFlag(GuestDevices.Pad))
                return;
            previous = null;
            driverFailed = false;
            ReleasePad();
        }

        void ReleasePad()
        {
            VirtualPad? detached = output;
            output = null;
            try { detached?.Dispose(); }
            catch (Exception ex) { Trace.Note($"Error al liberar el mando: {ex.Message}"); }
        }

        void UpdatePad(PadState? state)
        {
            if (state == previous)
                return;
            previous = state;
            idle.Touch();
            if (state is null)
            {
                ReleasePad();
                driverFailed = false;
                status("Sin mando remoto");
            }
            else if (!driverFailed)
            {
                try
                {
                    if (output is null)
                    {
                        output = new VirtualPad();
                        status("Mando Xbox 360 virtual conectado");
                    }
                    output.Update(state.Value);
                }
                catch (Exception ex)
                {
                    driverFailed = true;
                    status($"Error de ViGEmBus. {ex.Message}");
                    ReleasePad();
                }
            }
        }
    }
}
