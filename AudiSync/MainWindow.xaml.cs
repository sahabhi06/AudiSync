using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Windows;
using System.Windows.Controls;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Threading.Tasks;
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

                var deviceBlock = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 5, 0, 5) };
                deviceBlock.Children.Add(checkBox);
                deviceBlock.Children.Add(delayRow);
                deviceBlock.Children.Add(volumeRow);
                deviceBlock.Children.Add(new Separator());

                DeviceList.Items.Add(deviceBlock);
                _deviceSliders[device] = slider;
                _volumeSliders[device] = volumeSlider;
                _deviceSliders[device] = slider;
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
                _capture = new WasapiCapture(cableOutput);
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
                    var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);

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
                    foreach (var (_, buffer) in _activeOutputs)
                        buffer.AddSamples(a.Buffer, 0, a.BytesRecorded);
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
            _capture?.StopRecording();
            _capture?.Dispose();
            _capture = null;

            foreach (var (output, _) in _activeOutputs)
            {
                output.Stop();
                output.Dispose();
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