using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Windows;
using System.Windows.Controls;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudiSync
{
    public partial class MainWindow : Window
    {
        private readonly List<MMDevice> _renderDevices = new();
        private WasapiCapture? _capture;
        private readonly List<(WasapiOut output, BufferedWaveProvider buffer)> _activeOutputs = new();
        private string? _originalDefaultDeviceId;

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

        private void LoadDevices()
        {
            DeviceList.Items.Clear();
            _renderDevices.Clear();

            var bluetoothNames = GetPairedBluetoothDeviceNames();
            var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

            foreach (var device in devices)
            {
                // Skip the VB-CABLE input itself — we don't want to route audio back into it
                if (device.FriendlyName.Contains("CABLE"))
                    continue;

                bool isBluetooth = bluetoothNames.Any(bt => device.FriendlyName.Contains(bt));
                string tag = isBluetooth ? "[BLUETOOTH] " : "[Other] ";

                _renderDevices.Add(device);
                DeviceList.Items.Add(new CheckBox
                {
                    Content = tag + device.FriendlyName,
                    Tag = device
                });
            }
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => LoadDevices();

        private MMDevice? FindCableOutputRecordingDevice()
        {
            var enumerator = new MMDeviceEnumerator();
            var captureDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            return captureDevices.FirstOrDefault(d => d.FriendlyName.Contains("CABLE Output"));
        }

        private void Start_Click(object sender, RoutedEventArgs e)
        {
            var selectedDevices = DeviceList.Items
                .Cast<CheckBox>()
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
                    var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
                    output.Init(buffer);
                    output.Play();
                    _activeOutputs.Add((output, buffer));
                }

                _capture.DataAvailable += (s, a) =>
                {
                    foreach (var (_, buffer) in _activeOutputs)
                        buffer.AddSamples(a.Buffer, 0, a.BytesRecorded);
                };

                _capture.StartRecording();

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
            _capture?.StopRecording();
            _capture?.Dispose();
            _capture = null;

            foreach (var (output, _) in _activeOutputs)
            {
                output.Stop();
                output.Dispose();
            }
            _activeOutputs.Clear();

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