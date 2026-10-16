using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TecnoInfoRead
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    public class MainForm : Form
    {
        static readonly Color Bg = Color.FromArgb(90, 90, 90);
        static readonly Color HeaderBg = Color.FromArgb(22, 26, 32);
        static readonly Color BoxBg = Color.FromArgb(60, 60, 60);

        TextBox txtSerial;
        Button btnCheck;
        TextBox txtOut;

        public MainForm()
        {
            Text = "TECNO | Infinix | itel Info Read";
            ClientSize = new Size(560, 600);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Bg;

            var header = new Panel { BackColor = HeaderBg, Location = new Point(8, 8), Size = new Size(544, 90) };
            var logo = new FlowLayoutPanel { AutoSize = true, BackColor = HeaderBg, WrapContents = false };
            var font = new Font("Arial", 24, FontStyle.Bold);
            AddLogo(logo, "TECNO", Color.FromArgb(10, 132, 255), font);
            AddLogo(logo, "|", Color.FromArgb(154, 154, 154), font);
            AddLogo(logo, "Infinix", Color.FromArgb(31, 191, 79), font);
            AddLogo(logo, "|", Color.FromArgb(154, 154, 154), font);
            AddLogo(logo, "itel", Color.FromArgb(255, 42, 42), font);
            header.Controls.Add(logo);
            header.Layout += (s, e) => logo.Location = new Point((header.Width - logo.Width) / 2, (header.Height - logo.Height) / 2);
            Controls.Add(header);

            Controls.Add(new Label
            {
                Text = "Connect Fastboot Mode", ForeColor = Color.Gainsboro, BackColor = Bg,
                Font = new Font("Segoe UI", 13, FontStyle.Bold), AutoSize = true, Location = new Point(30, 120)
            });

            txtSerial = new TextBox
            {
                Location = new Point(30, 158), Width = 340, BackColor = BoxBg, ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 13)
            };
            Controls.Add(txtSerial);

            btnCheck = new Button
            {
                Text = "Check Device", Location = new Point(382, 154), Size = new Size(148, 40),
                BackColor = Color.FromArgb(74, 74, 74), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 12, FontStyle.Bold)
            };
            btnCheck.FlatAppearance.BorderColor = Color.Black;
            btnCheck.Click += async (s, e) => await CheckDevice();
            Controls.Add(btnCheck);

            txtOut = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                Location = new Point(30, 215), Size = new Size(500, 360),
                BackColor = BoxBg, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 13)
            };
            Controls.Add(txtOut);
        }

        static void AddLogo(FlowLayoutPanel p, string text, Color c, Font f)
        {
            p.Controls.Add(new Label { Text = text, ForeColor = c, Font = f, AutoSize = true, Margin = new Padding(8, 0, 8, 0) });
        }

        async Task CheckDevice()
        {
            btnCheck.Enabled = false;
            txtOut.Text = "Waiting for device in Fastboot mode...";
            try
            {
                string serial = txtSerial.Text.Trim();
                string result = await Task.Run(() => ReadInfo(ref serial));
                txtSerial.Text = serial;
                txtOut.Text = result;
            }
            finally { btnCheck.Enabled = true; }
        }

        static string ReadInfo(ref string serial)
        {
            if (string.IsNullOrEmpty(serial))
            {
                string devs = Fastboot(null, "devices");
                var m = Regex.Match(devs, @"^(\S+)\s+fastboot", RegexOptions.Multiline);
                if (!m.Success) return "No device found in Fastboot mode.\r\n" + devs;
                serial = m.Groups[1].Value;
            }

            var d = Parse(Fastboot(serial, "getvar all"));
            foreach (var kv in Parse(Fastboot(serial, "oem get_info"))) d[kv.Key] = kv.Value;

            string sn = First(d, "serialno", "serial", "sn");
            if (sn == "Unknown") sn = serial;

            return "=== DEVICE INFORMATION ===\r\n\r\n" +
                   "Brand Name: " + First(d, "brand", "ro.product.brand", "manufacturer").ToUpper() + "\r\n" +
                   "Model Number: " + First(d, "model", "product", "ro.product.model") + "\r\n" +
                   "Model Name: " + First(d, "marketname", "market_name", "ro.product.marketname", "device_name") + "\r\n" +
                   "IMEI 1: " + First(d, "imei", "imei1", "imei_1") + "\r\n" +
                   "IMEI 2: " + First(d, "imei2", "imei_2") + "\r\n" +
                   "Serial Number: " + sn + "\r\n";
        }

        static string Fastboot(string serial, string args)
        {
            try
            {
                var psi = new ProcessStartInfo("fastboot", (serial != null ? "-s " + serial + " " : "") + args)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    var outT = p.StandardOutput.ReadToEndAsync();
                    var errT = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } }
                    return outT.Result + errT.Result;
                }
            }
            catch (Exception ex) { return "ERROR: " + ex.Message + " (fastboot.exe not found?)"; }
        }

        static Dictionary<string, string> Parse(string text)
        {
            var d = new Dictionary<string, string>();
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Replace("(bootloader)", "").Trim();
                var m = Regex.Match(line, @"^([\w\-\.]+)\s*:\s*(.*)$");
                if (m.Success) d[m.Groups[1].Value.ToLower()] = m.Groups[2].Value.Trim();
            }
            return d;
        }

        static string First(Dictionary<string, string> d, params string[] keys)
        {
            foreach (var k in keys)
                if (d.TryGetValue(k, out var v) && v.Length > 0) return v;
            return "Unknown";
        }
    }
}
