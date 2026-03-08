using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SR160PowerConfig
{
    public class MainForm : Form
    {
        private Button btnConnect;
        private Button btnDisconnect;
        private Button btnRefresh;
        private Button btnSave;
        private Button btnClearList;
        private Label lblStatus;
        private Label lblCurrentPower;
        private Label lblPowerValue;
        private Label lblTagCount;
        private TrackBar trackPower;
        private NumericUpDown numPower;
        private TextBox txtEpcInput;
        private ListView lvTags;
        private bool isConnected;
        private bool suppressSync;
        private Dictionary<string, int> tagCounts;

        public MainForm()
        {
            tagCounts = new Dictionary<string, int>();
            InitializeComponents();
        }

        private void InitializeComponents()
        {
            Text = "CHIPMO \u2014 SR160 Power Config";
            ClientSize = new Size(500, 640);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9);

            // ─── Header ───
            var pnlHeader = new Panel
            {
                Location = new Point(0, 0),
                Size = new Size(500, 55),
                BackColor = Color.FromArgb(33, 37, 41)
            };

            var lblCompany = new Label
            {
                Text = "CHIPMO",
                Location = new Point(15, 5),
                AutoSize = true,
                Font = new Font("Segoe UI", 16, FontStyle.Bold),
                ForeColor = Color.White
            };

            var lblDevice = new Label
            {
                Text = "Chainway SR160 UHF RFID \u2014 TX Power тохируулагч",
                Location = new Point(15, 32),
                AutoSize = true,
                Font = new Font("Segoe UI", 9),
                ForeColor = Color.FromArgb(173, 181, 189)
            };

            pnlHeader.Controls.AddRange(new Control[] { lblCompany, lblDevice });

            // ─── USB холболт ───
            var grpConn = new GroupBox
            {
                Text = "USB холболт",
                Location = new Point(12, 63),
                Size = new Size(476, 75)
            };

            lblStatus = new Label
            {
                Text = "\u25CF Салсан",
                ForeColor = Color.Red,
                Location = new Point(12, 24),
                AutoSize = true,
                Font = new Font("Segoe UI", 10, FontStyle.Bold)
            };

            btnConnect = new Button
            {
                Text = "Холбогдох",
                Location = new Point(12, 46),
                Size = new Size(100, 26)
            };
            btnConnect.Click += BtnConnect_Click;

            btnDisconnect = new Button
            {
                Text = "Салгах",
                Location = new Point(118, 46),
                Size = new Size(80, 26),
                Enabled = false
            };
            btnDisconnect.Click += BtnDisconnect_Click;

            grpConn.Controls.AddRange(new Control[] { lblStatus, btnConnect, btnDisconnect });

            // ─── Антенна #1 ───
            var grpAnt = new GroupBox
            {
                Text = "Антенна #1",
                Location = new Point(12, 145),
                Size = new Size(476, 65)
            };

            lblCurrentPower = new Label
            {
                Text = "Одоогийн хүч:  \u2014",
                Location = new Point(12, 26),
                AutoSize = true,
                Font = new Font("Segoe UI", 11)
            };

            btnRefresh = new Button
            {
                Text = "Шинэчлэх",
                Location = new Point(380, 24),
                Size = new Size(82, 26),
                Enabled = false
            };
            btnRefresh.Click += delegate { ReadCurrentPower(); };

            grpAnt.Controls.AddRange(new Control[] { lblCurrentPower, btnRefresh });

            // ─── Хүч тохируулах ───
            var grpPower = new GroupBox
            {
                Text = "Хүч тохируулах (5 \u2013 30 dBm)",
                Location = new Point(12, 217),
                Size = new Size(476, 130)
            };

            var lblMin = new Label
            {
                Text = "5",
                Location = new Point(10, 28),
                AutoSize = true,
                Font = new Font("Segoe UI", 8)
            };

            var lblMax = new Label
            {
                Text = "30",
                Location = new Point(430, 28),
                AutoSize = true,
                Font = new Font("Segoe UI", 8)
            };

            trackPower = new TrackBar
            {
                Minimum = 5,
                Maximum = 30,
                Value = 20,
                TickFrequency = 1,
                Location = new Point(24, 40),
                Size = new Size(420, 45),
                Enabled = false
            };
            trackPower.ValueChanged += TrackPower_ValueChanged;

            lblPowerValue = new Label
            {
                Text = "\u2014 dBm",
                Location = new Point(200, 82),
                AutoSize = true,
                Font = new Font("Segoe UI", 16, FontStyle.Bold),
                ForeColor = Color.DarkBlue
            };

            numPower = new NumericUpDown
            {
                Minimum = 5,
                Maximum = 30,
                Value = 20,
                Location = new Point(12, 98),
                Size = new Size(60, 25),
                Enabled = false
            };
            numPower.ValueChanged += NumPower_ValueChanged;

            var lblDbm = new Label
            {
                Text = "dBm",
                Location = new Point(75, 101),
                AutoSize = true
            };

            btnSave = new Button
            {
                Text = "Хадгалах",
                Location = new Point(350, 94),
                Size = new Size(110, 30),
                Enabled = false,
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                BackColor = Color.FromArgb(76, 175, 80),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnSave.FlatAppearance.BorderSize = 0;
            btnSave.Click += BtnSave_Click;

            grpPower.Controls.AddRange(new Control[] {
                lblMin, lblMax, trackPower, lblPowerValue, numPower, lblDbm, btnSave
            });

            // ─── EPC уншигч (HID keyboard input) ───
            var grpEpc = new GroupBox
            {
                Text = "EPC уншигч (SR160 энд шивнэ)",
                Location = new Point(12, 353),
                Size = new Size(476, 280)
            };

            var lblInput = new Label
            {
                Text = "EPC оролт:",
                Location = new Point(12, 23),
                AutoSize = true
            };

            txtEpcInput = new TextBox
            {
                Location = new Point(80, 20),
                Size = new Size(280, 25),
                Font = new Font("Consolas", 10),
                CharacterCasing = CharacterCasing.Upper
            };
            txtEpcInput.KeyDown += TxtEpcInput_KeyDown;

            btnClearList = new Button
            {
                Text = "Цэвэрлэх",
                Location = new Point(380, 18),
                Size = new Size(82, 28)
            };
            btnClearList.Click += delegate
            {
                lvTags.Items.Clear();
                tagCounts.Clear();
                lblTagCount.Text = "Нийт: 0 таг";
                txtEpcInput.Focus();
            };

            lblTagCount = new Label
            {
                Text = "Нийт: 0 таг",
                Location = new Point(12, 50),
                AutoSize = true,
                Font = new Font("Segoe UI", 10, FontStyle.Bold)
            };

            lvTags = new ListView
            {
                Location = new Point(12, 72),
                Size = new Size(450, 195),
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                Font = new Font("Consolas", 9)
            };
            lvTags.Columns.Add("#", 40);
            lvTags.Columns.Add("EPC код", 320);
            lvTags.Columns.Add("Тоо", 60);

            grpEpc.Controls.AddRange(new Control[] {
                lblInput, txtEpcInput, btnClearList, lblTagCount, lvTags
            });

            Controls.AddRange(new Control[] { pnlHeader, grpConn, grpAnt, grpPower, grpEpc });
        }

        private void TxtEpcInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Return)
            {
                e.SuppressKeyPress = true;
                string epc = txtEpcInput.Text.Trim().ToUpper();
                if (epc.Length == 0) return;

                AddEpcToList(epc);
                txtEpcInput.Clear();
                txtEpcInput.Focus();
            }
        }

        private void AddEpcToList(string epc)
        {
            if (tagCounts.ContainsKey(epc))
            {
                tagCounts[epc]++;
                for (int i = 0; i < lvTags.Items.Count; i++)
                {
                    if (lvTags.Items[i].SubItems[1].Text == epc)
                    {
                        lvTags.Items[i].SubItems[2].Text = tagCounts[epc].ToString();
                        break;
                    }
                }
            }
            else
            {
                tagCounts[epc] = 1;
                ListViewItem item = new ListViewItem((lvTags.Items.Count + 1).ToString());
                item.SubItems.Add(epc);
                item.SubItems.Add("1");
                lvTags.Items.Add(item);
                lvTags.EnsureVisible(lvTags.Items.Count - 1);
            }

            lblTagCount.Text = "Нийт: " + tagCounts.Count + " таг";
        }

        private void SetConnectedState(bool connected)
        {
            isConnected = connected;
            lblStatus.Text = connected ? "\u25CF Холбогдсон" : "\u25CF Салсан";
            lblStatus.ForeColor = connected ? Color.Green : Color.Red;
            btnConnect.Enabled = !connected;
            btnDisconnect.Enabled = connected;
            btnRefresh.Enabled = connected;
            trackPower.Enabled = connected;
            numPower.Enabled = connected;
            btnSave.Enabled = connected;

            if (!connected)
            {
                lblCurrentPower.Text = "Одоогийн хүч:  \u2014";
                lblPowerValue.Text = "\u2014 dBm";
            }
        }

        private void BtnConnect_Click(object sender, EventArgs e)
        {
            try
            {
                int ret = UHFAPI.UsbOpen();
                if (ret == 0)
                {
                    SetConnectedState(true);
                    ReadCurrentPower();
                }
                else
                {
                    MessageBox.Show(
                        "Төхөөрөмжтэй холбогдож чадсангүй.\nАлдааны код: " + ret,
                        "Алдаа", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Алдаа: " + ex.Message, "Алдаа",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BtnDisconnect_Click(object sender, EventArgs e)
        {
            try { UHFAPI.UsbClose(); } catch { }
            SetConnectedState(false);
        }

        private void ReadCurrentPower()
        {
            byte power = 0;
            int ret = UHFAPI.UHFGetPower(ref power);
            if (ret == 0)
            {
                int val = Math.Max(5, Math.Min(30, (int)power));
                lblCurrentPower.Text = "Одоогийн хүч:  " + power + " dBm";

                suppressSync = true;
                trackPower.Value = val;
                numPower.Value = val;
                suppressSync = false;

                lblPowerValue.Text = power + " dBm";
            }
            else
            {
                lblCurrentPower.Text = "Одоогийн хүч:  уншиж чадсангүй (код: " + ret + ")";
            }
        }

        private void TrackPower_ValueChanged(object sender, EventArgs e)
        {
            if (suppressSync) return;
            suppressSync = true;
            numPower.Value = trackPower.Value;
            suppressSync = false;
            lblPowerValue.Text = trackPower.Value + " dBm";
        }

        private void NumPower_ValueChanged(object sender, EventArgs e)
        {
            if (suppressSync) return;
            suppressSync = true;
            trackPower.Value = (int)numPower.Value;
            suppressSync = false;
            lblPowerValue.Text = numPower.Value + " dBm";
        }

        private void BtnSave_Click(object sender, EventArgs e)
        {
            byte power = (byte)numPower.Value;
            int ret = UHFAPI.UHFSetPower(1, power);
            if (ret == 0)
            {
                ReadCurrentPower();
                MessageBox.Show(
                    "Хүчийг " + power + " dBm болгож хадгаллаа.",
                    "Амжилттай", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(
                    "Хүч тохируулж чадсангүй.\nАлдааны код: " + ret,
                    "Алдаа", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (isConnected)
            {
                try { UHFAPI.UsbClose(); } catch { }
            }
            base.OnFormClosing(e);
        }
    }
}
