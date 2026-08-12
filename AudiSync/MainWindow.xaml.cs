using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NAudio.Wave.SampleProviders;

namespace AudiSync
{
    

    public partial class MainWindow : Window
    {
        private readonly List<MMDevice> _renderDevices = new();
        private WasapiCapture? _capture;
        private readonly List<(WasapiOut output, BufferedWaveProvider buffer)> _activeOutputs = new();
        private string? _originalDefaultDeviceId;
        private readonly Dictionary<MMDevice, Slider> _deviceSliders = new();
        private readonly Dictionary<MMDevice, VolumeSampleProvider> _deviceVolumeProviders = new();
        private readonly Dictionary<MMDevice, Slider> _volumeSliders = new();
        private readonly Dictionary<MMDevice, BufferedWaveProvider> _deviceBuffers = new();
        private bool _isSyncing = false;
        private bool _isCalibrating = false;

        public MainWindow()
        {
            InitializeComponent();
            LoadDevices();
        }

        private List<string> GetPairedBluetoothDeviceNames()
        {
            var names = new List<string>();
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name FROM Win32_PnPEntity WHERE PNPClass = 'Bluetooth'");
                foreach (var device in searcher.Get())
                {
                    var name = device["Name"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(name))
                        names.Add(name);
                }
            }
            catch { }
            return names;
        }

        private void HandleDeviceFailure(MMDevice device, WasapiOut output, Exception? ex)
        {
            // PlaybackStopped fires on a background thread — must marshal back to UI thread
            Dispatcher.Invoke(() =>
            {
                // Remove this device from active playback so the DataAvailable loop stops touching it
                _activeOutputs.RemoveAll(pair => pair.output == output);
                _deviceBuffers.Remove(device);
                _deviceVolumeProviders.Remove(device);

                // Uncheck it in the UI so it's clear this device dropped out
                foreach (StackPanel block in DeviceList.Items)
                {
                    var checkBox = (CheckBox)block.Children[0];
                    if (checkBox.Tag is MMDevice d && d.ID == device.ID)
                        checkBox.IsChecked = false;
                }

                string reason = ex != null ? ex.Message : "disconnected";
                StatusText.Text = $"Status: '{device.FriendlyName}' dropped out ({reason}). Still syncing to {_activeOutputs.Count} device(s).";

                try { output.Dispose(); } catch { }

                // If every device has dropped, fully stop
                if (_activeOutputs.Count == 0)
                {
                    Stop_Click(this, new RoutedEventArgs());
                    StatusText.Text = "Status: All devices disconnected — sync stopped";
                }
            });
        }

        private List<MMDevice> GetSelectedDevices()
        {
            return DeviceList.Items
                .Cast<StackPanel>()
                .Select(block => (CheckBox)block.Children[0])
                .Where(cb => cb.IsChecked == true)
                .Select(cb => (MMDevice)cb.Tag)
                .ToList();
        }

        private async void Calibrate_Click(object sender, RoutedEventArgs e)
        {
            var selectedDevices = GetSelectedDevices();

            if (selectedDevices.Count < 2)
            {
                StatusText.Text = "Status: Select at least 2 devices to calibrate";
                return;
            }

            if (FindNonBluetoothMicrophone() == null)
            {
                StatusText.Text = "Status: ERROR - No built-in/non-Bluetooth microphone detected. Calibration needs one to avoid disrupting your Bluetooth audio.";
                return;
            }

            _isCalibrating = true;
            CalibrateBtn.IsEnabled = false;
            StartBtn.IsEnabled = false;

            // If we're actively syncing, remember and mute current volumes so the test tone can be heard cleanly
            var savedVolumes = new Dictionary<MMDevice, float>();
            bool wasSyncing = _isSyncing;

            if (wasSyncing)
            {
                StatusText.Text = "Status: Muting playback briefly for calibration...";
                foreach (var device in selectedDevices)
                {
                    if (_deviceVolumeProviders.TryGetValue(device, out var vp))
                    {
                        savedVolumes[device] = vp.Volume;
                        vp.Volume = 0f;
                    }
                }
                await Task.Delay(300); // let the mute settle before measuring
            }

            try
            {
                var measurements = new Dictionary<MMDevice, double>();

                for (int i = 0; i < selectedDevices.Count; i++)
                {
                    var device = selectedDevices[i];
                    StatusText.Text = $"Status: Calibrating '{device.FriendlyName}' ({i + 1}/{selectedDevices.Count}) — please stay quiet";
                    double latency = await MeasureDeviceLatencyAsync(device);
                    measurements[device] = latency;
                    await Task.Delay(500);
                }

                double minLatency = measurements.Values.Min();

                foreach (var device in selectedDevices)
                {
                    int delayMs = (int)Math.Round(measurements[device] - minLatency);
                    delayMs -= delayMs % 5;
                    _deviceSliders[device].Value = Math.Max(0, delayMs);
                }

                StatusText.Text = wasSyncing
                    ? "Status: Calibration complete — resuming playback with corrected sync"
                    : "Status: Calibration complete — review delays below, then Start Sync";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Status: ERROR during calibration - " + ex.Message;
            }
            finally
            {
                // Restore original volumes if we muted for a live calibration
                foreach (var kvp in savedVolumes)
                {
                    if (_deviceVolumeProviders.TryGetValue(kvp.Key, out var vp))
                        vp.Volume = kvp.Value;
                }

                _isCalibrating = false;
                CalibrateBtn.IsEnabled = true;
                StartBtn.IsEnabled = !wasSyncing; // keep Start disabled if we're still actively syncing
            }
        }

        private MMDevice? FindNonBluetoothMicrophone()
        {
            var bluetoothNames = GetPairedBluetoothDeviceNames();
            var enumerator = new MMDeviceEnumerator();
            var micDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);

            // Prefer a mic that is NOT associated with any paired Bluetooth device
            return micDevices.FirstOrDefault(mic =>
                !bluetoothNames.Any(bt => mic.FriendlyName.Contains(bt)));
        }

        private async Task<double> MeasureDeviceLatencyAsync(MMDevice device)
        {
            var micDevice = FindNonBluetoothMicrophone();
            if (micDevice == null)
            {
                StatusText.Text = "Status: ERROR - No non-Bluetooth microphone found for calibration";
                return 0;
            }

            var samples = new List<float>();
            var capture = new WasapiCapture(micDevice);
            var sw = System.Diagnostics.Stopwatch.StartNew();

            capture.DataAvailable += (s, a) =>
            {
                int bytesPerSample = capture.WaveFormat.BitsPerSample / 8;
                int channels = capture.WaveFormat.Channels;
                int sampleCount = a.BytesRecorded / bytesPerSample / channels;

                for (int i = 0; i < sampleCount; i++)
                {
                    int offset = i * bytesPerSample * channels;
                    float sample = capture.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat
                        ? BitConverter.ToSingle(a.Buffer, offset)
                        : BitConverter.ToInt16(a.Buffer, offset) / 32768f;
                    samples.Add(Math.Abs(sample));
                }
            };

            capture.StartRecording();
            await Task.Delay(300); // capture a moment of baseline "silence" first

            double baseline = samples.Count > 0 ? samples.Average() : 0.001;
            double threshold = Math.Max(baseline * 4, 0.02);

            double clickSentMs = sw.Elapsed.TotalMilliseconds;
            int actualSampleRate = capture.WaveFormat.SampleRate;
            var tone = new SignalGenerator(capture.WaveFormat.SampleRate, 1)
            {
                Type = SignalGeneratorType.Sin,
                Frequency = 2500,
                Gain = 0.8
            };
            var toneProvider = tone.Take(TimeSpan.FromMilliseconds(150));

            var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
            output.Init(toneProvider.ToWaveProvider());
            output.Play();

            await Task.Delay(1500); // window to let the tone play + travel + be captured

            output.Stop();
            output.Dispose();
            capture.StopRecording();
            capture.Dispose();

            double msPerSample = 1000.0 / actualSampleRate;

            int startIndex = (int)(clickSentMs / msPerSample);
            for (int i = Math.Max(0, startIndex); i < samples.Count; i++)
            {
                if (samples[i] > threshold)
                {
                    double onsetMs = i * msPerSample;
                    return onsetMs - clickSentMs;
                }
            }

            // Fallback: no clear onset detected, assume 0 extra latency
            return 0;
        }
        private void AdjustDeviceDelayLive(MMDevice device, int deltaMs)
        {
            if (deltaMs == 0 || _capture == null) return;
            if (!_deviceBuffers.TryGetValue(device, out var buffer)) return;

            int bytesPerMs = _capture.WaveFormat.AverageBytesPerSecond / 1000;
            int byteCount = Math.Abs(deltaMs) * bytesPerMs;
            byteCount -= byteCount % _capture.WaveFormat.BlockAlign; // keep sample-aligned
            if (byteCount <= 0) return;

            if (deltaMs > 0)
            {
                // Increasing delay: insert silence, pushing this device's audio later
                var silence = new byte[byteCount];
                buffer.AddSamples(silence, 0, byteCount);
            }
            else
            {
                // Decreasing delay: discard some queued audio to catch this device up
                var throwaway = new byte[byteCount];
                buffer.Read(throwaway, 0, byteCount);
            }
        }

        private void LoadDevices()
        {
            DeviceList.Items.Clear();
            _renderDevices.Clear();
            _deviceSliders.Clear();
            _volumeSliders.Clear();

            var bluetoothNames = GetPairedBluetoothDeviceNames();
            var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

            foreach (var device in devices)
            {
                if (device.FriendlyName.Contains("CABLE"))
                    continue;

                bool isBluetooth = bluetoothNames.Any(bt => device.FriendlyName.Contains(bt));
                string tag = isBluetooth ? "[BLUETOOTH] " : "[Other] ";

                _renderDevices.Add(device);

                var checkBox = new CheckBox
                {
                    Content = tag + device.FriendlyName,
                    Tag = device,
                    Width = 400,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var slider = new Slider
                {
                    Minimum = 0,
                    Maximum = 300,
                    Width = 120,
                    TickFrequency = 5,
                    IsSnapToTickEnabled = true,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(5, 0, 5, 0)
                };

                var delayLabel = new TextBlock { Text = "0 ms", Width = 50, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };

                var minusButton = new Button { Content = "−", Width = 28, Height = 24, VerticalAlignment = VerticalAlignment.Center };
                minusButton.Click += (s, e) => { if (slider.Value >= slider.TickFrequency) slider.Value -= slider.TickFrequency; };

                var plusButton = new Button { Content = "+", Width = 28, Height = 24, VerticalAlignment = VerticalAlignment.Center };
                plusButton.Click += (s, e) => { if (slider.Value <= slider.Maximum - slider.TickFrequency) slider.Value += slider.TickFrequency; };

                double previousDelay = 0;
                slider.ValueChanged += (s, e) =>
                {
                    delayLabel.Text = $"{(int)slider.Value} ms";
                    double delta = slider.Value - previousDelay;
                    previousDelay = slider.Value;

                    if (_isSyncing)
                        AdjustDeviceDelayLive(device, (int)delta);
                };

                var delayRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 2, 0, 2) };
                delayRow.Children.Add(new TextBlock { Text = "Delay:", Width = 50, VerticalAlignment = VerticalAlignment.Center });
                delayRow.Children.Add(minusButton);
                delayRow.Children.Add(slider);
                delayRow.Children.Add(plusButton);
                delayRow.Children.Add(delayLabel);

                // --- Volume controls ---
                var volumeSlider = new Slider
                {
                    Minimum = 0,
                    Maximum = 100,
                    Value = 100,
                    Width = 120,
                    TickFrequency = 5,
                    IsSnapToTickEnabled = true,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(5, 0, 5, 0)
                };

                var volumeLabel = new TextBlock { Text = "100 %", Width = 50, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };

                var volMinusButton = new Button { Content = "−", Width = 28, Height = 24, VerticalAlignment = VerticalAlignment.Center };
                volMinusButton.Click += (s, e) => { if (volumeSlider.Value >= volumeSlider.TickFrequency) volumeSlider.Value -= volumeSlider.TickFrequency; };

                var volPlusButton = new Button { Content = "+", Width = 28, Height = 24, VerticalAlignment = VerticalAlignment.Center };
                volPlusButton.Click += (s, e) => { if (volumeSlider.Value <= volumeSlider.Maximum - volumeSlider.TickFrequency) volumeSlider.Value += volumeSlider.TickFrequency; };

                volumeSlider.ValueChanged += (s, e) =>
                {
                    volumeLabel.Text = $"{(int)volumeSlider.Value} %";
                    // Live-update volume if this device is currently playing
                    if (_deviceVolumeProviders.TryGetValue(device, out var vp))
                        vp.Volume = (float)(volumeSlider.Value / 100.0);
                };

                var volumeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 2, 0, 8) };
                volumeRow.Children.Add(new TextBlock { Text = "Volume:", Width = 50, VerticalAlignment = VerticalAlignment.Center });
                volumeRow.Children.Add(volMinusButton);
                volumeRow.Children.Add(volumeSlider);
                volumeRow.Children.Add(volPlusButton);
                volumeRow.Children.Add(volumeLabel);

                var deviceBlock = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 8, 0, 8) };
                deviceBlock.Children.Add(checkBox);
                deviceBlock.Children.Add(delayRow);
                deviceBlock.Children.Add(volumeRow);
                deviceBlock.Children.Add(new Separator());

                DeviceList.Items.Add(deviceBlock);
                _deviceSliders[device] = slider;
                _volumeSliders[device] = volumeSlider;
            }
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => LoadDevices();

        private MMDevice? FindCableOutputRecordingDevice()
        {
            var enumerator = new MMDeviceEnumerator();
            var captureDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            return captureDevices.FirstOrDefault(d => d.FriendlyName.Contains("CABLE Output"));
        }

        private async void Start_Click(object sender, RoutedEventArgs e)
        {
            if (_isCalibrating)
            {
                StatusText.Text = "Status: Please wait for calibration to finish first";
                return;
            }
            var selectedDevices = DeviceList.Items
            .Cast<StackPanel>()
            .Select(block => (CheckBox)block.Children[0])
            .Where(cb => cb.IsChecked == true)
            .Select(cb => (MMDevice)cb.Tag)
            .ToList();

            if (selectedDevices.Count == 0)
            {
                StatusText.Text = "Status: Select at least one device first";
                return;
            }

            var cableOutput = FindCableOutputRecordingDevice();
            if (cableOutput == null)
            {
                StatusText.Text = "Status: ERROR - 'CABLE Output' recording device not found. Is VB-CABLE installed?";
                return;
            }
            var enumerator = new MMDeviceEnumerator();

            // Remember current default device so we can restore it later
            _originalDefaultDeviceId = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;

            // Find CABLE Input among NAudio's own device list (no second library needed)
            var cableInputDevice = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .FirstOrDefault(d => d.FriendlyName.Contains("CABLE Input"));

            if (cableInputDevice == null)
            {
                StatusText.Text = "Status: ERROR - Could not find 'CABLE Input' playback device";
                return;
            }

            DefaultDeviceHelper.SetDefaultDevice(cableInputDevice.ID);

            try
            {
                _capture = new WasapiCapture(cableOutput, true, 20); // event-driven, ~20ms latency instead of default
                _capture.WaveFormat = cableOutput.AudioClient.MixFormat;

                _activeOutputs.Clear();
                foreach (var device in selectedDevices)
                {
                    var buffer = new BufferedWaveProvider(_capture.WaveFormat)
                    {
                        DiscardOnBufferOverflow = true,
                        BufferDuration = TimeSpan.FromSeconds(2)
                    };
                    _deviceBuffers[device] = buffer;
                    var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 40);
                    output.PlaybackStopped += (s, e) =>
                    {
                        // Only treat this as a failure if it stopped with an error (not a normal user-initiated Stop)
                        if (e.Exception != null)
                            HandleDeviceFailure(device, output, e.Exception);
                    };

                    float initialVolume = (float)(_volumeSliders[device].Value / 100.0);
                    var sampleProvider = buffer.ToSampleProvider();
                    var volumeProvider = new VolumeSampleProvider(sampleProvider) { Volume = initialVolume };
                    _deviceVolumeProviders[device] = volumeProvider;
                    output.Init(volumeProvider.ToWaveProvider());
                    _activeOutputs.Add((output, buffer));

                    int delayMs = (int)_deviceSliders[device].Value;
                    if (delayMs == 0)
                    {
                        output.Play();
                    }
                    else
                    {
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(delayMs);
                            output.Play();
                        });
                    }
                }

                _capture.DataAvailable += (s, a) =>
                {
                    // Iterate a snapshot copy — _activeOutputs can be modified concurrently by HandleDeviceFailure
                    foreach (var (output, buffer) in _activeOutputs.ToList())
                    {
                        try
                        {
                            buffer.AddSamples(a.Buffer, 0, a.BytesRecorded);
                        }
                        catch
                        {
                            // This device's buffer failed — let PlaybackStopped handle cleanup, just skip it here
                        }
                    }
                };

                _capture.StartRecording();
                _isSyncing = true;
                
                StatusText.Text = $"Status: Syncing to {selectedDevices.Count} device(s)";
                StartBtn.IsEnabled = false;
                StopBtn.IsEnabled = true;
                RefreshBtn.IsEnabled = false;
            }
            catch (Exception ex)
            {
                StatusText.Text = "Status: ERROR - " + ex.Message;
            }
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            _isSyncing = false;
            try { _capture?.StopRecording(); } catch { }
            try { _capture?.Dispose(); } catch { }
            _capture = null;

            foreach (var (output, _) in _activeOutputs)
            {
                try { output.Stop(); } catch { }
                try { output.Dispose(); } catch { }
            }
            _activeOutputs.Clear();
            _deviceBuffers.Clear();
            _deviceVolumeProviders.Clear();

            StatusText.Text = "Status: Idle";
            StartBtn.IsEnabled = true;
            StopBtn.IsEnabled = false;
            RefreshBtn.IsEnabled = true;
            // Restore whatever the user's original speaker/headphone was
            if (_originalDefaultDeviceId != null)
                DefaultDeviceHelper.SetDefaultDevice(_originalDefaultDeviceId);
        }
    }
}