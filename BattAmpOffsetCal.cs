// Mission Planner plugin: calibrate the ArduPilot analog battery current offset (BATT_AMP_OFFSET).
//
// ArduPilot's analog monitor (BATT_MONITOR = 4, AP_BattMonitor_Analog) computes
//     reported_A = (pin_V - BATT_AMP_OFFSET) * BATT_AMP_PERVLT
// so the offset that makes the reported current equal the current measured with a meter is
//     new_offset_V = old_offset_V + (reported_A - actual_A) / BATT_AMP_PERVLT
// Example: 0.054 + (-1.6 - 0.3) / 180 = 0.04344 V, which moves the reading from -1.6 A to 0.3 A.
//
// Install either this .cs file (Mission Planner compiles it at start-up) or the built DLL
// into the Mission Planner "plugins" folder - not both. Open it by right-clicking the HUD on
// the DATA (Flight Data) screen. Written against the Mission Planner 1.3.83 plugin API.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BattAmpOffsetCal
{
    public class BattAmpOffsetCalPlugin : MissionPlanner.Plugin.Plugin
    {
        private ToolStripMenuItem menuItem;

        public override string Name { get { return "Battery Current Offset Calibration"; } }
        public override string Version { get { return "1.0"; } }
        public override string Author { get { return "wartun"; } }

        public override bool Init()
        {
            return true;
        }

        public override bool Loaded()
        {
            menuItem = new ToolStripMenuItem("Battery Current Offset Calibration...");
            menuItem.Click += (sender, e) =>
            {
                using (var form = new CalibrationForm(Host))
                    form.ShowDialog(Host.MainForm);
            };
            Host.FDMenuHud.Items.Add(menuItem);
            return true;
        }

        public override bool Exit()
        {
            if (menuItem != null)
                Host.FDMenuHud.Items.Remove(menuItem);
            return true;
        }
    }

    /// <summary>Pure calculation and input parsing, kept separate from the UI and MAVLink code.</summary>
    public static class OffsetMath
    {
        public static double NewOffset(double oldOffsetV, double reportedA, double actualA, double ampsPerVolt)
        {
            return oldOffsetV + (reportedA - actualA) / ampsPerVolt;
        }

        public static bool IsValidAmpsPerVolt(double ampsPerVolt)
        {
            return !double.IsNaN(ampsPerVolt) && !double.IsInfinity(ampsPerVolt) && ampsPerVolt > 0;
        }

        /// <summary>Parses "0.3", "0,3", "-1,6", "0.3 A". Both comma and period are decimal separators.</summary>
        public static bool TryParseAmps(string text, out double amps)
        {
            amps = 0;
            if (text == null)
                return false;

            var s = text.Trim().Replace(',', '.').Replace('−', '-'); // U+2212 minus sign
            if (s.EndsWith("A", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(0, s.Length - 1).TrimEnd();

            return double.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                       CultureInfo.InvariantCulture, out amps)
                   && !double.IsNaN(amps) && !double.IsInfinity(amps);
        }
    }

    /// <summary>An expected failure; the message is shown to the user as is.</summary>
    public class CalibrationException : Exception
    {
        public CalibrationException(string message) : base(message)
        {
        }
    }

    /// <summary>Values read together from the vehicle, so the offset and the current belong to each other.</summary>
    public class Snapshot
    {
        public byte SysId;
        public byte CompId;
        public double Offset;
        public double AmpsPerVolt;
        public double Current;
        public int Samples;
    }

    public class CalibrationForm : Form
    {
        private const string MonitorParam = "BATT_MONITOR";
        private const string OffsetParam = "BATT_AMP_OFFSET";
        private const string PerVoltParam = "BATT_AMP_PERVLT";
        private const int AnalogVoltageAndCurrent = 4;
        private const int SampleWindowMs = 2000;
        private const int TelemetryTimeoutMs = 5000;
        private const int SettleMs = 500;
        private const double VerifyToleranceV = 1e-6;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private readonly MissionPlanner.Plugin.PluginHost host;
        private readonly Label currentLabel = ValueLabel();
        private readonly Label offsetLabel = ValueLabel();
        private readonly Label perVoltLabel = ValueLabel();
        private readonly Label statusLabel = new Label { AutoSize = true, MaximumSize = new Size(460, 0) };
        private readonly TextBox actualBox = new TextBox { Width = 120 };
        private readonly Button refreshButton = new Button { Text = "Refresh", AutoSize = true };
        private readonly Button applyButton = new Button { Text = "Calculate...", AutoSize = true };
        private readonly Button closeButton = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        private bool busy;

        public CalibrationForm(MissionPlanner.Plugin.PluginHost host)
        {
            this.host = host;

            Text = "Battery Current Offset Calibration";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(10);

            var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
            AddRow(grid, "Reported battery current:", currentLabel);
            AddRow(grid, OffsetParam + ":", offsetLabel);
            AddRow(grid, PerVoltParam + ":", perVoltLabel);
            AddRow(grid, "Actual current from meter (A):", actualBox);

            var buttons = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Right };
            buttons.Controls.AddRange(new Control[] { refreshButton, applyButton, closeButton });
            grid.Controls.Add(buttons);
            grid.SetColumnSpan(buttons, 2);
            grid.Controls.Add(statusLabel);
            grid.SetColumnSpan(statusLabel, 2);
            Controls.Add(grid);

            AcceptButton = applyButton;
            CancelButton = closeButton;
            refreshButton.Click += async (s, e) => await RefreshAsync();
            applyButton.Click += async (s, e) => await CalibrateAsync();
            FormClosing += (s, e) => e.Cancel = busy;
            Shown += async (s, e) => await RefreshAsync();

            MissionPlanner.Utilities.ThemeManager.ApplyThemeTo(this);
        }

        private static Label ValueLabel()
        {
            return new Label { AutoSize = true, Text = "-", Anchor = AnchorStyles.Left };
        }

        private static void AddRow(TableLayoutPanel grid, string caption, Control value)
        {
            grid.Controls.Add(new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left });
            grid.Controls.Add(value);
        }

        private void SetBusy(bool value, string status)
        {
            busy = value;
            refreshButton.Enabled = applyButton.Enabled = closeButton.Enabled = actualBox.Enabled = !value;
            UseWaitCursor = value;
            statusLabel.Text = status;
        }

        private void Display(Snapshot s)
        {
            currentLabel.Text = string.Format(Inv, "{0:0.000} A  (mean of {1} fresh samples)", s.Current, s.Samples);
            offsetLabel.Text = string.Format(Inv, "{0:0.00000} V", s.Offset);
            perVoltLabel.Text = string.Format(Inv, "{0:0.###} A/V", s.AmpsPerVolt);
        }

        private void ShowError(string message)
        {
            statusLabel.Text = message;
            MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private static string Describe(Exception ex)
        {
            return ex is CalibrationException ? ex.Message : ex.GetType().Name + ": " + ex.Message;
        }

        private async Task RefreshAsync()
        {
            SetBusy(true, "Reading parameters and battery current from the vehicle...");
            try
            {
                var snapshot = await Task.Run(() => ReadSnapshot());
                Display(snapshot);
                SetBusy(false, "Measure the current with your meter, enter it above and press Calculate.");
                actualBox.Focus();
            }
            catch (Exception ex)
            {
                SetBusy(false, "");
                ShowError("Could not read from the vehicle. Nothing was changed.\n\n" + Describe(ex));
            }
        }

        private async Task CalibrateAsync()
        {
            double actual;
            if (!OffsetMath.TryParseAmps(actualBox.Text, out actual))
            {
                ShowError("Enter the current measured with the meter in amperes, e.g. 0.3 or 0,3.");
                actualBox.Focus();
                return;
            }

            SetBusy(true, "Reading fresh values from the vehicle...");
            Snapshot before;
            double newOffset;
            try
            {
                // re-read everything so the offset and the reported current used below belong together
                before = await Task.Run(() => ReadSnapshot());
                Display(before);
                newOffset = OffsetMath.NewOffset(before.Offset, before.Current, actual, before.AmpsPerVolt);
                if (double.IsNaN(newOffset) || double.IsInfinity(newOffset))
                    throw new CalibrationException("The calculated offset is not a valid number.");
            }
            catch (Exception ex)
            {
                SetBusy(false, "");
                ShowError("Could not calculate a new offset. Nothing was changed.\n\n" + Describe(ex));
                return;
            }

            var proposal = string.Format(Inv,
                "Reported current (mean of {0} fresh samples): {1:0.000} A\n" +
                "Actual current (meter): {2:0.000} A\n" +
                PerVoltParam + ": {3:0.###} A/V\n\n" +
                "new offset = {4:0.00000} + ({1:0.000} - {2:0.000}) / {3:0.###} = {5:0.00000} V\n\n" +
                "Write " + OffsetParam + " = {5:0.00000} V (currently {4:0.00000} V) to vehicle sysid {6}?\n" +
                "The reported current should change by {7:+0.00;-0.00} A. " + PerVoltParam + " is not changed.",
                before.Samples, before.Current, actual, before.AmpsPerVolt, before.Offset, newOffset,
                before.SysId, actual - before.Current);

            statusLabel.Text = "Proposed " + OffsetParam + ": " + newOffset.ToString("0.00000", Inv) + " V";
            if (MessageBox.Show(this, proposal, "Confirm " + OffsetParam, MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                SetBusy(false, "Cancelled. Nothing was changed.");
                return;
            }

            SetBusy(true, "Writing " + OffsetParam + " and reading it back...");
            try
            {
                var after = await Task.Run(() => WriteVerifyAndReread(before, newOffset));
                Display(after);
                var result = string.Format(Inv,
                    OffsetParam + " written and verified: {0:0.00000} V -> {1:0.00000} V (read back from the vehicle).\n" +
                    "Reported current now: {2:0.000} A (mean of {3} fresh samples). Your meter: {4:0.000} A.",
                    before.Offset, after.Offset, after.Current, after.Samples, actual);
                SetBusy(false, result);
                MessageBox.Show(this, result, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                SetBusy(false, "");
                ShowError(Describe(ex) + "\n\nPress Refresh to see the value now on the vehicle.");
            }
        }

        // ---- vehicle access; runs on a background thread ----

        private Snapshot ReadSnapshot()
        {
            var port = host.comPort;
            if (port == null || port.BaseStream == null || !port.BaseStream.IsOpen)
                throw new CalibrationException("Mission Planner is not connected to a vehicle.");

            var mav = port.MAV;
            var s = new Snapshot { SysId = mav.sysid, CompId = mav.compid };

            var monitor = ReadParam(port, s, MonitorParam);
            if (monitor != AnalogVoltageAndCurrent)
                throw new CalibrationException(string.Format(Inv,
                    MonitorParam + " is {0}. This calibration only applies to the analog voltage and current monitor ("
                    + MonitorParam + " = {1}).", monitor, AnalogVoltageAndCurrent));

            s.AmpsPerVolt = ReadParam(port, s, PerVoltParam);
            if (!OffsetMath.IsValidAmpsPerVolt(s.AmpsPerVolt))
                throw new CalibrationException(string.Format(Inv,
                    PerVoltParam + " is {0}. It must be a positive number of amps per volt.", s.AmpsPerVolt));

            s.Offset = ReadParam(port, s, OffsetParam);
            s.Current = ReadCurrent(port, s.SysId, s.CompId, 0, out s.Samples);
            return s;
        }

        private Snapshot WriteVerifyAndReread(Snapshot before, double newOffset)
        {
            var port = host.comPort;
            if (port == null || port.BaseStream == null || !port.BaseStream.IsOpen)
                throw new CalibrationException("Mission Planner is no longer connected. Nothing was changed.");

            bool accepted;
            try
            {
                // sends PARAM_SET for the existing parameter and waits for the vehicle's PARAM_VALUE reply
                accepted = port.setParam(before.SysId, before.CompId, OffsetParam, newOffset);
            }
            catch (Exception ex)
            {
                throw new CalibrationException("Writing " + OffsetParam + " failed: " + ex.Message);
            }

            if (!accepted)
                throw new CalibrationException(OffsetParam + " is not in Mission Planner's parameter list, so it was not written.");

            var after = new Snapshot
            {
                SysId = before.SysId,
                CompId = before.CompId,
                AmpsPerVolt = before.AmpsPerVolt,
                Offset = ReadParam(port, before, OffsetParam) // fresh PARAM_REQUEST_READ
            };

            if (Math.Abs(after.Offset - (float)newOffset) > VerifyToleranceV)
                throw new CalibrationException(string.Format(Inv,
                    "Verification failed: the vehicle reports " + OffsetParam + " = {0:0.00000} V, expected {1:0.00000} V.",
                    after.Offset, newOffset));

            after.Current = ReadCurrent(port, after.SysId, after.CompId, SettleMs, out after.Samples);
            return after;
        }

        private static double ReadParam(MissionPlanner.MAVLinkInterface port, Snapshot target, string name)
        {
            if (!port.MAVlist[target.SysId, target.CompId].param.ContainsKey(name))
                throw new CalibrationException(name + " is missing from the parameter list. Check that the battery monitor " +
                                               "is configured, then refresh the parameters (CONFIG > Full Parameter List > Refresh Params).");

            float value;
            try
            {
                value = port.GetParam(target.SysId, target.CompId, name);
            }
            catch (Exception ex)
            {
                throw new CalibrationException("Could not read " + name + " from the vehicle: " + ex.Message);
            }

            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new CalibrationException(name + " returned an invalid value.");
            return value;
        }

        /// <summary>
        /// Averages the battery 1 current from SYS_STATUS and BATTERY_STATUS (id 0) messages that arrive
        /// after this call, i.e. fresh telemetry rather than Mission Planner's cached value.
        /// </summary>
        private static double ReadCurrent(MissionPlanner.MAVLinkInterface port, byte sysid, byte compid, int settleMs, out int count)
        {
            if (settleMs > 0)
                Thread.Sleep(settleMs);

            var samples = new List<double>();
            var unknown = 0;
            Action<short> add = centiamps =>
            {
                lock (samples)
                {
                    if (centiamps == -1) // ArduPilot sends -1 when it has no current measurement
                        unknown++;
                    else
                        samples.Add(centiamps / 100.0);
                }
            };

            var subSys = port.SubscribeToPacketType(MAVLink.MAVLINK_MSG_ID.SYS_STATUS, m =>
            {
                add(m.ToStructure<MAVLink.mavlink_sys_status_t>().current_battery);
                return true;
            }, sysid, compid);
            var subBat = port.SubscribeToPacketType(MAVLink.MAVLINK_MSG_ID.BATTERY_STATUS, m =>
            {
                var bat = m.ToStructure<MAVLink.mavlink_battery_status_t>();
                if (bat.id == 0)
                    add(bat.current_battery);
                return true;
            }, sysid, compid);

            try
            {
                var timer = Stopwatch.StartNew();
                while (timer.ElapsedMilliseconds < TelemetryTimeoutMs)
                {
                    lock (samples)
                        if (samples.Count > 0 && timer.ElapsedMilliseconds >= SampleWindowMs)
                            break;
                    Thread.Sleep(50);
                }
            }
            finally
            {
                port.UnSubscribeToPacketType(subSys);
                port.UnSubscribeToPacketType(subBat);
            }

            lock (samples)
            {
                count = samples.Count;
                if (count == 0 && unknown > 0)
                    throw new CalibrationException("The vehicle reports that battery current is not available.");
                if (count == 0)
                    throw new CalibrationException(string.Format(Inv,
                        "No battery telemetry (SYS_STATUS / BATTERY_STATUS) received from the vehicle in {0} s.",
                        TelemetryTimeoutMs / 1000));

                var sum = 0.0;
                foreach (var a in samples)
                    sum += a;
                return sum / count;
            }
        }
    }
}
