using System;
using System.Windows.Forms;
using Grimoire.Networking;
using Grimoire.Game;
using System.Threading.Tasks;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using Grimoire.Game.Data;
using System.Collections.Generic;
using System.Linq;
using Grimoire.Tools;
using DarkUI.Forms;
using Newtonsoft.Json;

namespace ExamplePacketPlugin
{
    public partial class Main : DarkForm
    {
        public static Main Instance { get; } = new Main();
        public string getBottingTime => string.Format("{0:hh\\:mm\\:ss}", stopwatch.Elapsed);
        public string getMapInfo => $"{Player.Map}-{Flash.Call<int>("RoomNumber", new string[0])}";
        public int getThisPlayerID => Flash.Call<int>("UserID", new string[0]);
        public int thisCharID => Flash.Call<int>("CharID", new object[0]); // visual charID

        int lastUIWidth;
        string mapName = null;
        Stopwatch stopwatch = new Stopwatch();
        Process currProcess = Process.GetCurrentProcess();
        PerformanceCounter currCpuCounter = null;
        Dictionary<int, InventoryItem> getSessionDropsById = new Dictionary<int, InventoryItem>();
        int duplicateWarningLogCounter = 1;

        public Main()
        {
            InitializeComponent();
        }

        private void Main_Load(object sender, EventArgs e)
        {
            Task.Run(() => this.currCpuCounter = ProcessCpuCounter.GetPerfCounterForProcessId(currProcess.Id));
            lastUIWidth = this.Width;
        }

        private void Main_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        }

        private void cbEnable_CheckedChanged(object sender, EventArgs e)
        {
            if(cbEnable.Checked)
            {
                Proxy.Instance.ReceivedFromServer += PacketHandler;
                doMonitoring();
            }
            else
            {
                Proxy.Instance.ReceivedFromServer -= PacketHandler;
                getSessionDropsById.Clear();
            }
        }

        public void PacketHandler(Grimoire.Networking.Message message)
        {
            try
            {
                if (World.IsMapLoading) return;

                string msgStr = message?.ToString();
                if (msgStr == null) return;

                Type msgType = message.GetType();

                if (msgType.Equals(typeof(JsonMessage)))
                {
                    // {"t":"xt","b":{"r":-1,"o":{"cmd":"moveToArea","areaName":"battleon-2" ...
                    if (message.Command == "moveToArea")
                    {
                        // update map name + number
                        this.mapName = ((JsonMessage)message).DataObject["areaName"]?.Value<string>();
                        if (cbLogWarning.Checked) addWarningLog($"[{getBottingTime}] {this.mapName}");
                        if (cbLogDrops.Checked) addDropsLog($"[{getBottingTime}] {this.mapName}");
                    }

                    // {"t":"xt","b":{"r":-1,"o":{"cmd":"ccqr","bSuccess":0,"QuestID":3533,"sName":"Air Realm Purification"}}}
                    else if (message.Command == "ccqr" && cbLogWarning.Checked)
                    {
                        // log for quest complete failed
                        if (((JsonMessage)message).DataObject["bSuccess"]?.Value<int>() == 0)
                        {
                            string questName = ((JsonMessage)message).DataObject["sName"]?.Value<string>();
                            string warningMsg = $"Quest Complete Failed! ({questName})";
                            addWarningLog(warningMsg);
                        }
                    }

                    // {"t":"xt","b":{"r":-1,"o":{"userID":"73048820","monPad":1,"cmd":"playerDeath","entType":"m","did":"73033030","kid":"undefined"}}}
                    else if (message.Command == "playerDeath" && cbLogWarning.Checked)
                    {
                        if (((JsonMessage)message).DataObject["did"]?.Value<int>() == thisCharID)
                        {
                            string monsterList = null;
                            List<Monster> currMonster = World.AvailableMonsters.OrderByDescending(m => m.MaxHealth).ToList();
                            for (int i = 0; i <= currMonster.Count - 1; i++)
                            {
                                monsterList += currMonster[i].Name;
                                if (i != currMonster.Count - 1)
                                    monsterList += ", ";
                            }
                            addWarningLog($"Player Died! at {Player.Map}; {Player.Cell}/{Player.Pad}; Monster: {monsterList ?? "None"}");
                        }
                    }

                    // {"t":"xt","b":{"r":-1,"o":{"cmd":"dropItem","items":{"598":{"ItemID":"598","iQty":1}}}}}
                    else if (message.Command == "dropItem" && cbLogDrops.Checked)
                    {
                        // log for dropItem
                        createDropLog(message);
                    }

                    // Success: {"t":"xt","b":{"r":-1,"o":{"ItemID":598,"cmd":"getDrop","bSuccess":1,"bBank":false,"CharItemID":1.825454993E9,"iQty":1}}}
                    // Failed: {"t":"xt","b":{"r":-1,"o":{"ItemID":598,"cmd":"getDrop","bSuccess":0}}}
                    else if (message.Command == "getDrop" && cbLogDrops.Checked)
                    {
                        // log for getDrop
                        createGetDropLog(message);
                    }
                }
                else if (msgType.Equals(typeof(XtMessage)))
                {
                    // %xt%warning%-1%Item already exists in your bank!%
                    if (message.Command == "warning" && cbLogWarning.Checked)
                    {
                        // log for warning text from server
                        string warningMsg = ((XtMessage)message).Arguments[4];
                        addWarningLog(warningMsg);
                    }
                }
            }
            catch (NullReferenceException)
            {
                addWarningLog("Error: NullReferenceException");
            }
            catch (Exception ex)
            {
                addWarningLog(ex.ToString());
            }
        }

        private async void createDropLog(Grimoire.Networking.Message message)
        {
            try
            {
                JToken jToken = ((JsonMessage)message).DataObject?["items"];
                InventoryItem drop = jToken.ToObject<Dictionary<int, InventoryItem>>().First().Value;
                if (!getSessionDropsById.ContainsKey(drop.Id)) getSessionDropsById.Add(drop.Id, drop);
                else getSessionDropsById[drop.Id].Quantity += drop.Quantity;
                string currentQty = $"{(Player.Inventory.Items.Find((InventoryItem x) => x.Name == drop.Name) ?? new InventoryItem()).Quantity}";
                addDropsLog($"(!) {drop.Quantity} {drop.Name} [{currentQty}]");
            }
            catch (Exception ex)
            {
                addWarningLog(ex.ToString());
            }
        }

        private async void createGetDropLog(Grimoire.Networking.Message message)
        {
            try
            {
                JToken getMsg = ((JsonMessage)message).DataObject;
                if (getMsg["bSuccess"].Value<bool>())
                {
                    int currentItemID = getMsg["ItemID"].Value<int>();
                    InventoryItem currentItem = getSessionDropsById[currentItemID];
                    int acceptQty = getSessionDropsById[currentItemID].Quantity;
                    string result = $"(+) Accepted: {acceptQty} {currentItem.Name}";
                    getSessionDropsById[currentItemID].Quantity = 0;
                    addDropsLog(result);
                }
                else
                {
                    int currentItemID = getMsg["ItemID"].Value<int>();
                    InventoryItem currentItem = getSessionDropsById[currentItemID];
                    int acceptQty = getSessionDropsById[currentItemID].Quantity;
                    string result = $"(X) Failed: {acceptQty} {currentItem.Name}";
                    addDropsLog(result);
                }
            }
            catch (Exception ex)
            {
                addWarningLog(ex.ToString());
            }
        }

        private async void doMonitoring()
        {
            try
            {
                while (cbEnable.Checked)
                {
                    // clear drop session (if logout)
                    if(!Player.IsLoggedIn) getSessionDropsById.Clear();

                    // monitor info
                    string username = Grimoire.Tools.Flash.Call<string>("GetUsername", new string[0]).ToUpper() ?? "(undetected)";
                    string isBotting = Grimoire.Botting.OptionsManager.IsRunning ? "Yes" : "No";
                    string isLogin = Player.IsLoggedIn ? "Yes" : "No";

                    string state = Player.IsLoggedIn ? Player.CurrentState.ToString() : "-";
                    string mapInfo = !World.IsMapLoading ? $"{getMapInfo} ({World.PlayersInMap.Count})" : "-";
                    string cellPad = Player.IsLoggedIn ? $"{Player.Cell}/{Player.Pad}" : "-";
                    string health = Player.IsLoggedIn ? $"{Player.Health}/{Player.HealthMax}" : "-";
                    string mana = Player.IsLoggedIn ? $"{Player.Mana}/{Player.ManaMax}" : "-";

                    string hasTarget = Player.IsLoggedIn ? (Player.HasTarget ? "Yes" : "No") : "-";
                    string isAlive = Player.IsLoggedIn ? (Player.IsAlive ? "Yes" : "No") : "-";
                    string isAfk = Player.IsLoggedIn ? (Player.IsAfk ? "Yes" : "No") : "-";
                    string lvl = Player.IsLoggedIn ? Player.Level.ToString() : "-";
                    string gold = Player.IsLoggedIn ? Player.Gold.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("de")) : "-";
                    string inv = Player.IsLoggedIn ? $"{Player.Inventory.UsedSlots}/{Player.Inventory.MaxSlots}" : "-";

                    // updating label
                    this.Text = username;
                    lbUsername.Text = username;
                    lbIsLogin.Text = isLogin;
                    lbIsBotting.Text = isBotting;

                    lbState.Text = Player.IsLoggedIn ? state : "-";
                    lbMapInfo.Text = mapInfo;
                    lbCellPad.Text = cellPad;
                    lbHealth.Text = health;
                    lbMana.Text = mana;

                    lbHasTarget.Text = hasTarget;
                    lbIsAlive.Text = isAlive;
                    lbIsAfk.Text = isAfk;

                    lbLevel.Text = lvl;
                    lbGold.Text = gold;
                    lbInv.Text = inv;

                    lbCpuUsage.Text = getCpuUsage() ?? "-";

                    // botting timer control
                    if (Grimoire.Botting.OptionsManager.IsRunning)
                        stopwatch.Start();
                    else if (!Grimoire.Botting.OptionsManager.IsRunning)
                        stopwatch.Stop();

                    // update botting timer text
                    lbBottingTime.Text = $"BottingTime: {getBottingTime}";

                    // delay 1s
                    await Task.Delay(1000);
                }
            }
            catch (NullReferenceException)
            {
                addWarningLog("Null Reference Exception");
                await Task.Delay(1500);
                cbEnable.Checked = false;
                cbEnable.Checked = true;
            }
        }

        private void addWarningLog(string msg)
        {
            try
            {
                // duplicate log spam fix
                if (tbWarningLog.Lines.Length > 2)
                    if (tbWarningLog.Lines[tbWarningLog.Lines.Length - 2].Contains(msg))
                    {
                        duplicateWarningLogCounter++;
                        var lines = tbWarningLog.Lines;
                        lines[tbWarningLog.Lines.Length - 2] = $" - {msg} ({duplicateWarningLogCounter})";
                        tbWarningLog.Lines = lines;
                        return;
                    }

                if (tbWarningLog.Text == String.Empty && !msg.StartsWith("["))
                    tbWarningLog.Text += $"[{getBottingTime}] {getMapInfo}\r\n";
                tbWarningLog.Text += msg.StartsWith("[") ? $"\r\n{msg}\r\n" : $" - {msg}\r\n";
                if (tbWarningLog.Lines.FirstOrDefault() == String.Empty)
                    tbWarningLog.Lines = tbWarningLog.Lines.Skip(1).ToArray();
                duplicateWarningLogCounter = 1;
            }
            catch
            {

            }
        }

        private void addDropsLog(string msg)
        {
            try
            {
                if (tbDropsLog.Text == String.Empty && !msg.StartsWith("["))
                    tbDropsLog.Text += $"[{getBottingTime}] {getMapInfo}\r\n";
                tbDropsLog.Text += msg.StartsWith("[") ? $"\r\n{msg}\r\n" : $"{msg}\r\n";
                if (tbDropsLog.Lines.FirstOrDefault() == String.Empty)
                    tbDropsLog.Lines = tbDropsLog.Lines.Skip(1).ToArray();
            }
            catch
            {

            }
        }

        private string getCpuUsage()
        {
            try
            {
                double cpuUsage = currCpuCounter.NextValue() / (double)Environment.ProcessorCount;
                cpuUsage = Math.Round(cpuUsage, 1);
                return $"{cpuUsage}%";
            }
            catch
            {
                return null;
            }
        }

        private void lblClearLog_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            tbDropsLog.Text = String.Empty;
            tbWarningLog.Text = String.Empty;
        }

        private void linkLabel1_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            if (!Grimoire.UI.Root.Instance.Visible)
            {
                Grimoire.UI.Root.Instance.Show();
                Grimoire.UI.Root.Instance.WindowState = FormWindowState.Normal;
                lblShowHide.Text = "[Hide Bot]";
            }
            else if(Grimoire.UI.Root.Instance.Visible)
            {
                Grimoire.UI.Root.Instance.Hide();
                lblShowHide.Text = "[Show Bot]";
            }
        }

        private void lblResetTime_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            stopwatch.Reset();
        }

        private void lblUIControl_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            if(lblUIControl.Text == "[<<]")
            {
                // hide panel
                lblClearLog.Visible = false;
                lastUIWidth = this.Width;
                this.Width = 216;
                this.MaximumSize = new System.Drawing.Size(this.Width, int.MaxValue);
                lblUIControl.Text = "[>>]";
            }
            else if (lblUIControl.Text == "[>>]")
            {
                // show panel
                lblClearLog.Visible = true;
                this.MaximumSize = new System.Drawing.Size(int.MaxValue, int.MaxValue);
                this.Width = lastUIWidth;
                lblUIControl.Text = "[<<]";
            }
        }

        // label state style

        private void lbIsBotting_TextChanged(object sender, EventArgs e)
        {
            if (lbIsBotting.Text == "Yes")
                lbIsBotting = LabelState.Yes(this.lbIsBotting);
            else if (lbIsBotting.Text == "No")
                lbIsBotting = LabelState.No(this.lbIsBotting);
            else
                lbIsBotting = LabelState.None(this.lbIsBotting);
        }

        private void lbIsLogin_TextChanged(object sender, EventArgs e)
        {
            if (lbIsLogin.Text == "Yes")
                lbIsLogin = LabelState.Yes(this.lbIsLogin);
            else if (lbIsLogin.Text == "No")
                lbIsLogin = LabelState.No(this.lbIsLogin);
            else
                lbIsLogin = LabelState.None(this.lbIsLogin);
        }

        private void lbState_TextChanged(object sender, EventArgs e)
        {
            if (lbState.Text == "Idle")
                lbState = LabelState.Idle(this.lbState);
            else if (lbState.Text == "InCombat")
                lbState = LabelState.InCombat(this.lbState);
            else if (lbState.Text == "Dead")
                lbState = LabelState.Dead(this.lbState);
            else
                lbState = LabelState.None(this.lbState);
        }

        private void lbInv_TextChanged(object sender, EventArgs e)
        {
            if (lbInv.Text == "-")
                lbInv = LabelState.None(this.lbInv);
            else if (Player.Inventory.AvailableSlots > 0)
                lbInv = LabelState.InvNotFull(this.lbInv);
            else
                lbInv = LabelState.InvFull(this.lbInv);

        }

        // scroll to carret

        private void tbWarningLog_TextChanged(object sender, EventArgs e)
        {
            tbWarningLog.SelectionStart = tbWarningLog.TextLength;
            tbWarningLog.ScrollToCaret();
        }

        private void tbDropsLog_TextChanged(object sender, EventArgs e)
        {
            tbDropsLog.SelectionStart = tbDropsLog.TextLength;
            tbDropsLog.ScrollToCaret();
        }
    }
}
