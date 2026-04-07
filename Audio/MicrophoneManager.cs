using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using MultiplayerChat.Config;
using SiraUtil.Logging;
using UnityEngine;
using Zenject;

namespace MultiplayerChat.Audio;

public class MicrophoneManager : MonoBehaviour, IInitializable, IDisposable
{
    [Inject] private readonly SiraLog _log = null!;
    [Inject] private readonly PluginConfig _config = null!;

    public const int TargetFrequency = 48000;
    public const int SamplesPerFragment = 512;

    public string? SelectedDeviceName { get; private set; }
    public bool HaveSelectedDevice { get; private set; }

    private int _minFreq;
    private int _maxFreq;
    private AudioClip? _captureClip;

    private int _micBufferPos;
    private readonly float[] _fragmentBuffer;
    private float[]? _micBuffer;

    public bool IsCapturing { get; private set; }

    public event Action<float[], int>? FragmentReadyEvent;
    public event Action? CaptureEndEvent;

    private object _mic_access_lock = new();
    private bool _mic_is_toggling = false;

    public MicrophoneManager()
    {
        _micBufferPos = 0;
        _fragmentBuffer = new float[SamplesPerFragment];
        _micBuffer = null; // will be allocated when sampling frequency is set
    }

    public void Initialize()
    {
        TryAutoSelectDevice();
    }

    public void Dispose()
    {
        if (_captureClip != null)
        {
            Destroy(_captureClip);
            _captureClip = null;
        }
    }

    #region Loop

    public void Update()
    {
        //This small check here stops the main thread from hanging if the _mic_access_lock is currently in use.
        //Lock exists so that Mic doesnt cause hard crash if accessed while already being accessed.
        //Start and stop are in Task.Runs to prevent main thread from hanging while mic is being started.
        if (_mic_is_toggling || !IsCapturing || _captureClip is null || _micBuffer is null)
            return;

        lock (_mic_access_lock)
        {
            if (!Microphone.IsRecording(SelectedDeviceName))
                return;

            // Get mic position (in samples)
            var micPosCurrent = Microphone.GetPosition(SelectedDeviceName);

            if (micPosCurrent < 0 || _micBufferPos == micPosCurrent)
                return;

            // Get raw wave samples from the mic capture into our buffer
            if (!_captureClip.GetData(_micBuffer, 0))
                return;

            // We'll invoke OnAudioReady each time we can fill the fragment buffer
            while (GetLoopDataLength(_micBuffer.Length, _micBufferPos, micPosCurrent) > SamplesPerFragment)
            {
                var remain = _micBuffer.Length - _micBufferPos;

                if (remain < SamplesPerFragment)
                {
                    Array.Copy(_micBuffer, _micBufferPos, _fragmentBuffer, 0, remain);
                    Array.Copy(_micBuffer, 0, _fragmentBuffer, remain, SamplesPerFragment - remain);
                }
                else
                {
                    Array.Copy(_micBuffer, _micBufferPos, _fragmentBuffer, 0, SamplesPerFragment);
                }
                if (FragmentReadyEvent != null && _captureClip != null)
                    FragmentReadyEvent?.Invoke(_fragmentBuffer, _captureClip.frequency);

                _micBufferPos += SamplesPerFragment;

                if (_micBufferPos > _micBuffer.Length)
                {
                    _micBufferPos -= _micBuffer.Length;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetLoopDataLength(int bufferLength, int head, int tail)
    {
        if (head < tail)
        {
            return tail - head;
        }
        else
        {
            return bufferLength - head + tail;
        }
    }

    #endregion

    #region Device selection

    public bool AnyDevicesAvailable => Microphone.devices.Length > 0;

    public string[] AvailableDeviceNames => Microphone.devices;

    public bool TryAutoSelectDevice()
    {
        // Try configured device
        if (_config.MicrophoneDevice is not null && TrySelectDevice(_config.MicrophoneDevice))
            return true;

        // Try default device
        if (TrySelectDevice(null)) 
            return true;

        _log.Error("No valid recording devices available, will not be able to speak");
        return false;
    }

    public bool TrySelectDevice(string? deviceName)
    {
        if (deviceName == "Default")
            deviceName = null;
        
        if (IsCapturing)
            InternalStopCapture(); //Has to call the internal version so that this is done synchronously. The old mic will not get disabled correctly if the SelectedDeviceName is changed before the task can run.

        if (deviceName == "None")
        {
            SelectedDeviceName = null;
            HaveSelectedDevice = false;
            _minFreq = 0;
            _maxFreq = 0;
            _log.Warn("Recording device selection removed, will not be able to speak");
            return true;
        }

        if (deviceName != null && !AvailableDeviceNames.Contains(deviceName))
        {
            _log.Error($"Requested device is not available: {deviceName}");
            return false;
        }

        SelectedDeviceName = deviceName;
        HaveSelectedDevice = true;
        
        Microphone.GetDeviceCaps(deviceName, out _minFreq, out _maxFreq);
        
        _log.Info($"Selected recording device: {deviceName ?? "(Default)"} (frequency={GetRecordingFrequency()})");
        return true;
    }

    public int GetRecordingFrequency()
    {
        if (_minFreq == 0 && _maxFreq == 0)
            // If these values are zero, any frequency should be supported
            return TargetFrequency;

        return Mathf.Clamp(TargetFrequency, _minFreq, _maxFreq);
    }

    #endregion

    #region Capture

    public void StartCapture()
    {
        Task.Run(InternalStartCapture);
    }

    public void StopCapture()
    {
        Task.Run(InternalStopCapture);
    }

    private void InternalStartCapture()
    {
        InternalStopCapture();

        lock (_mic_access_lock)
        {
            _mic_is_toggling = true;
            if (!HaveSelectedDevice)
            {
                _mic_is_toggling = false;
                throw new InvalidOperationException("Cannot start capture without a selected device");
            }

            var recordingFreq = GetRecordingFrequency();

            try
            {
                _captureClip = Microphone.Start(SelectedDeviceName, true, 1, recordingFreq);
            }
            finally { }
            _micBufferPos = 0;

            if (_micBuffer == null || _micBuffer.Length != recordingFreq)
                _micBuffer = new float[recordingFreq];

            IsCapturing = true;
            _mic_is_toggling = false;
        }
    }


    private void InternalStopCapture()
    {
        lock (_mic_access_lock)
        {
            _mic_is_toggling = true;
            if (!IsCapturing)
            {
                _mic_is_toggling = false;
                return;
            }

            if (Microphone.IsRecording(SelectedDeviceName))
                Microphone.End(SelectedDeviceName);

            IsCapturing = false;

            if (_captureClip != null)
            {
                Destroy(_captureClip);
                _captureClip = null;
            }

            _micBufferPos = 0;

            Array.Clear(_fragmentBuffer, 0, _fragmentBuffer.Length);

            if (_micBuffer != null)
                Array.Clear(_micBuffer, 0, _micBuffer.Length);

            _mic_is_toggling = false;
        }
        CaptureEndEvent?.Invoke();
    }

    #endregion
}