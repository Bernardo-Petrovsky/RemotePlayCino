using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace RemotePlay;

// Native Windows process loopback. The selected process and its children form the audio boundary.
internal sealed class ProcessAudioCapture : IWaveIn
{
    private readonly AudioClient client;
    private readonly Process target;
    private readonly AutoResetEvent samples = new(false);
    private readonly CancellationTokenSource stop = new();
    private Task recording = Task.CompletedTask;
    private bool started;
    private bool disposed;
    private readonly WaveFormat format = new(48000, 16, 2);

    private ProcessAudioCapture(AudioClient client, Process target)
    {
        this.client = client;
        this.target = target;
        try
        {
            client.Initialize(AudioClientShareMode.Shared,
                AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm,
                0, 0, format, Guid.Empty);
            SetSampleEvent(client, samples.SafeWaitHandle);
        }
        catch
        {
            samples.Dispose();
            stop.Dispose();
            throw;
        }
    }

    internal static bool Supported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348);

    internal static async Task<ProcessAudioCapture> CreateAsync(AudioApplication application, bool exclude, CancellationToken token)
    {
        if (!Supported) throw new PlatformNotSupportedException("El filtro de audio requiere Windows 11 o una versión compatible de Windows.");
        token.ThrowIfCancellationRequested();
        Process target = application.Open();
        AudioClient? client = null;
        try
        {
            client = await ActivateAsync(target.Id, exclude, token);
            if (target.HasExited) throw new IOException("La aplicación de audio se cerró al iniciar la captura.");
            return new ProcessAudioCapture(client, target);
        }
        catch
        {
            client?.Dispose();
            target.Dispose();
            throw;
        }
    }

    public WaveFormat WaveFormat
    {
        get => format;
        set => throw new NotSupportedException("El filtro transmite PCM estéreo a 48 kHz y 16 bits.");
    }
    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public void StartRecording()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (started) throw new InvalidOperationException("La captura ya se inició.");
        started = true;
        recording = Task.Factory.StartNew(Capture, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private void Capture()
    {
        Exception? error = null;
        try
        {
            AudioCaptureClient capture = client.AudioCaptureClient;
            client.Start();
            WaitHandle[] waits = [stop.Token.WaitHandle, samples];
            while (!stop.IsCancellationRequested)
            {
                if (WaitHandle.WaitAny(waits, 250) == 0) break;
                if (target.HasExited) throw new IOException("La aplicación de audio se cerró. El audio filtrado se detuvo.");
                while (!stop.IsCancellationRequested && capture.GetNextPacketSize() > 0)
                {
                    IntPtr data = capture.GetBuffer(out int frames, out AudioClientBufferFlags flags);
                    byte[] pcm;
                    try
                    {
                        pcm = new byte[checked(frames * format.BlockAlign)];
                        if ((flags & AudioClientBufferFlags.Silent) == 0)
                            Marshal.Copy(data, pcm, 0, pcm.Length);
                    }
                    finally { capture.ReleaseBuffer(frames); }
                    DataAvailable?.Invoke(this, new WaveInEventArgs(pcm, pcm.Length));
                }
            }
        }
        catch (Exception ex) when (ex is COMException or IOException or InvalidOperationException or OverflowException)
        { error = ex; }
        finally
        {
            try { client.Stop(); }
            catch (COMException ex) { error ??= ex; }
            RecordingStopped?.Invoke(this, new StoppedEventArgs(error));
        }
    }

    public void StopRecording() => stop.Cancel();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        stop.Cancel();
        recording.GetAwaiter().GetResult();
        client.Dispose();
        target.Dispose();
        samples.Dispose();
        stop.Dispose();
    }

    private static async Task<AudioClient> ActivateAsync(int processId, bool exclude, CancellationToken token)
    {
        var callback = new ActivationCallback(processId, exclude);
        IActivationOperation? operation = null;
        try
        {
            operation = BeginActivation(callback);
            try { return await callback.Result.Task.WaitAsync(TimeSpan.FromSeconds(5), token); }
            catch
            {
                // Activation cannot be canceled natively. Dispose a client returned after cancellation.
                _ = callback.Result.Task.ContinueWith(task =>
                {
                    if (task.IsCompletedSuccessfully) task.Result.Dispose();
                    else _ = task.Exception;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw;
            }
        }
        catch
        {
            if (operation is null) callback.Parameters.Dispose();
            throw;
        }
        finally
        {
            if (operation is not null) Marshal.ReleaseComObject(operation);
            GC.KeepAlive(callback);
        }
    }

    [SuppressMessage("Major Code Smell", "S3869", Justification = "NAudio accepts an IntPtr. AddRef protects the call; this capture owns the event until after AudioClient.Dispose.")]
    private static void SetSampleEvent(AudioClient audio, SafeWaitHandle handle)
    {
        bool referenced = false;
        try
        {
            handle.DangerousAddRef(ref referenced);
            audio.SetEventHandle(handle.DangerousGetHandle());
        }
        finally { if (referenced) handle.DangerousRelease(); }
    }

    [SuppressMessage("Major Code Smell", "S3869", Justification = "The native PROPVARIANT BLOB requires a pointer. AddRef protects this call and the completion callback owns the allocation until activation completes.")]
    private static IActivationOperation BeginActivation(ActivationCallback callback)
    {
        bool referenced = false;
        try
        {
            callback.Parameters.DangerousAddRef(ref referenced);
            var variant = new ActivationVariant { Type = 65, Size = 12, Data = callback.Parameters.DangerousGetHandle() };
            Guid iid = typeof(IAudioClient).GUID;
            Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync("VAD\\Process_Loopback", in iid, in variant, callback, out IActivationOperation operation));
            return operation;
        }
        finally { if (referenced) callback.Parameters.DangerousRelease(); }
    }

    private sealed class ActivationMemory : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal ActivationMemory(int pid, bool exclude) : base(true)
        {
            SetHandle(Marshal.AllocHGlobal(12));
            Marshal.WriteInt32(handle, 0, 1); // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            Marshal.WriteInt32(handle, 4, pid);
            Marshal.WriteInt32(handle, 8, exclude ? 1 : 0);
        }
        protected override bool ReleaseHandle() { Marshal.FreeHGlobal(handle); return true; }
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct ActivationVariant
    {
        [FieldOffset(0)] internal ushort Type;
        [FieldOffset(8)] internal int Size;
        [FieldOffset(16)] internal IntPtr Data;
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class ActivationCallback(int pid, bool exclude) : IActivationCallback, IAgileObject
    {
        internal ActivationMemory Parameters { get; } = new(pid, exclude);
        internal TaskCompletionSource<AudioClient> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ActivateCompleted(IActivationOperation operation)
        {
            object? native = null;
            try
            {
                Marshal.ThrowExceptionForHR(operation.GetActivateResult(out int hr, out native));
                Marshal.ThrowExceptionForHR(hr);
                var audio = new AudioClient((IAudioClient)native);
                native = null;
                if (!Result.TrySetResult(audio)) audio.Dispose();
            }
            catch (Exception ex)
            { Result.TrySetException(ex); } // Never let an exception cross the native COM callback boundary.
            finally
            {
                if (native is not null && Marshal.IsComObject(native)) Marshal.ReleaseComObject(native);
                Parameters.Dispose();
            }
            return 0;
        }
    }

    [DllImport("Mmdevapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ActivateAudioInterfaceAsync(string path, in Guid iid, in ActivationVariant parameters,
        IActivationCallback callback, out IActivationOperation operation);
}
