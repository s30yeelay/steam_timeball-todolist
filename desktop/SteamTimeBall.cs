using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;
using System.Xml.Serialization;
using Microsoft.Win32;
using System.Net;
using System.Security.Cryptography;
using System.Web.Script.Serialization;

public class Record { public string Day; public string Game; public double Seconds; }
public class ManualGame { public string Path; public string Name; }
public class Store {
    public int X = -1, Y = -1;
    public int Volume = 30;
    public string ResetWeek="", ResetDay="";
    public double ResetBaseline;
    public string CloudDay="";
    public int CloudEarnedMinutes;
    public List<Record> Records = new List<Record>();
    public List<ManualGame> Manual = new List<ManualGame>();
    public List<string> Excluded = new List<string>();
    public List<string> Roots = new List<string>();
    public List<string> Alerts = new List<string>();
}
public class Game { public string Id, Name, Directory; }
public class WorkbenchConfig {
    public string Site,DeviceKey,GatewayToken,CoachStatePath;
    public static string ConfigPath {get{return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"data","workbench.dat");}}
    public static WorkbenchConfig Load() {
        var config=Read(ConfigPath);return config!=null&&Regex.IsMatch(config.DeviceKey??"","^[a-f0-9]{64}$")?config:null;
    }
    public static WorkbenchConfig Read(string path) {try {var data=ProtectedData.Unprotect(File.ReadAllBytes(path),null,DataProtectionScope.CurrentUser);using(var stream=new MemoryStream(data))return (WorkbenchConfig)new XmlSerializer(typeof(WorkbenchConfig)).Deserialize(stream);}catch{return null;}}
    public static WorkbenchConfig Defaults() {return Read(Path.Combine(Path.GetDirectoryName(ConfigPath),"workbench-defaults.dat"));}
    public static void Save(WorkbenchConfig config) {
        Uri uri;if(!Uri.TryCreate(config.Site,UriKind.Absolute,out uri)||uri.Scheme!="https"||uri.AbsolutePath!="/"||!uri.Host.EndsWith(".chatgpt.site",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("工作台地址须为受保护的 HTTPS Sites 地址");
        if(!Regex.IsMatch(config.DeviceKey??"","^[a-f0-9]{64}$"))throw new ArgumentException("设备密钥格式不正确");
        using(var stream=new MemoryStream()){new XmlSerializer(typeof(WorkbenchConfig)).Serialize(stream,config);var bytes=ProtectedData.Protect(stream.ToArray(),null,DataProtectionScope.CurrentUser);Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));File.WriteAllBytes(ConfigPath,bytes);}
    }
}
public class TimedWebClient:WebClient {protected override WebRequest GetWebRequest(Uri address){var request=base.GetWebRequest(address);request.Timeout=15000;return request;}}
public class DetailsWindow : Form {
    protected override bool ShowWithoutActivation { get { return true; } }
}
public static class Quota {
    public const double LimitSeconds=90*60;
    public const double WarningSeconds=105*60;
    public static bool NeedsWarning(double seconds,bool hasGames,bool alreadyWarning) {
        return hasGames && (alreadyWarning || seconds>=WarningSeconds);
    }
    public static int WarningVolume(int volume) {return volume<=0?30:Math.Min(100,volume);}
    public static bool NeedsEarnedWarning(double limit,double used,bool hasGames,bool latched) {return hasGames && (latched || limit<=0 || used>=Math.Min(90*60,limit)+900);}
    public static int EarnedAlertSlot(double used,double limit) {if(limit<=0)return -1;if(used>=limit)return (int)(limit/60);if(limit>600&&used>=limit-600)return (int)(limit/60)-10;if(limit>1800&&used>=limit-1800)return (int)(limit/60)-30;return -1;}
}
public class ForcedWarning : DetailsWindow {
    bool released;
    readonly Label games;
    readonly Font warningFont=new Font("Microsoft YaHei UI",18,FontStyle.Regular,GraphicsUnit.Pixel);
    public ForcedWarning(IList<string> names,Action recheck) {
        Text="Steam 时间球 · 请结束今天的游戏";ClientSize=new Size(640,350);AutoScaleMode=AutoScaleMode.None;
        Font=warningFont;BackColor=Color.FromArgb(255,247,244);TopMost=true;ControlBox=false;
        FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;StartPosition=FormStartPosition.CenterScreen;
        var layout=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(28),ColumnCount=1,RowCount=5};
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,44));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,56));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,58));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,44));
        layout.Controls.Add(new Label {Text="今日额度已超时 15 分钟",Dock=DockStyle.Fill,ForeColor=Color.FromArgb(177,46,48),TextAlign=ContentAlignment.MiddleLeft},0,0);
        layout.Controls.Add(new Label {Text="每天额度 90 分钟，累计达到 105 分钟后强制提醒。\n此警告会保持显示并持续发声，直到游戏进程退出。",Dock=DockStyle.Fill},0,1);
        games=new Label {Dock=DockStyle.Fill,AutoEllipsis=true,Padding=new Padding(0,8,0,0)};layout.Controls.Add(games,0,2);
        layout.Controls.Add(new Label {Text="请先保存进度，再退出游戏。最小化或切回桌面不会解除。\n游戏退出后自动关闭；跨过零点也不会提前解除本次警告。",Dock=DockStyle.Fill,ForeColor=Color.DimGray},0,3);
        var button=new Button {Text="重新检测游戏是否已退出",AutoSize=true,Dock=DockStyle.Left};button.Click+=delegate {recheck();};layout.Controls.Add(button,0,4);
        Controls.Add(layout);UpdateGames(names);
    }
    public void UpdateGames(IList<string> names) {games.Text="仍在运行："+String.Join("、",names);}
    public void SetWorkbenchMessage(double minutes) {
        foreach(Control panel in Controls)foreach(Label label in panel.Controls.OfType<Label>()) {
            if(label.Text.StartsWith("今日额度"))label.Text=minutes<=0?"今日任务额度尚未解锁":"已超过任务额度 15 分钟";
            if(label.Text.StartsWith("每天额度"))label.Text=minutes<=0?"请先完成工作台任务，赚取游戏分钟数。\n请保存进度并退出游戏后，完成任务再重新开始。":"本次已赚额度 "+minutes+" 分钟，超时 15 分钟后强提醒。\n警告与声音会持续到游戏进程退出。";
        }
    }
    protected override void OnFormClosing(FormClosingEventArgs e) {
        if(!released && e.CloseReason==CloseReason.UserClosing)e.Cancel=true;
        base.OnFormClosing(e);
    }
    protected override void WndProc(ref Message message) {
        if(!released && message.Msg==0x112 && (((long)message.WParam&0xFFF0)==0xF020 || ((long)message.WParam&0xFFF0)==0xF060)) return;
        base.WndProc(ref message);
    }
    public bool BlocksUserClose() {var e=new FormClosingEventArgs(CloseReason.UserClosing,false);OnFormClosing(e);return e.Cancel;}
    public void ReleaseAfterGamesExit() {released=true;Close();}
    protected override void Dispose(bool disposing) {if(disposing)warningFont.Dispose();base.Dispose(disposing);}
}
public static class DiagnosticsLog {
    static readonly object sync=new object();
    static string path;
    [DllImport("user32.dll")] static extern int GetGuiResources(IntPtr process,int flags);
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MemoryStatus value);
    [StructLayout(LayoutKind.Sequential)] struct MemoryStatus {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    public static void Configure(string file) {path=file;}
    public static void Write(string stage,Exception error=null) {
        if(path==null) return;
        try {
            lock(sync) {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using(var process=Process.GetCurrentProcess()) {
                    var memory=new MemoryStatus {Length=(uint)Marshal.SizeOf(typeof(MemoryStatus))};
                    bool available=GlobalMemoryStatusEx(ref memory);
                    string line=DateTime.Now.ToString("s")+" "+stage+" privateMB="+(process.PrivateMemorySize64/1048576)+" managedMB="+(GC.GetTotalMemory(false)/1048576)+" handles="+process.HandleCount+" GDI="+GetGuiResources(process.Handle,0)+" USER="+GetGuiResources(process.Handle,1)+" threads="+process.Threads.Count;
                    if(available) line+=" availablePhysicalMB="+(memory.AvailablePhysical/1048576)+" memoryLoadPercent="+memory.Load;
                    if(error!=null) {string detail=error.ToString();line+="\r\n"+(detail.Length>8192?detail.Substring(0,8192):detail);}
                    if(File.Exists(path) && new FileInfo(path).Length+System.Text.Encoding.UTF8.GetByteCount(line+Environment.NewLine)>65536) File.WriteAllText(path,"");
                    File.AppendAllText(path,line+Environment.NewLine);
                }
            }
        } catch { /* Diagnostics must never cause another failure. */ }
    }
}
public static class Ledger {
    public static string WeekKey(DateTime time) {return Day(time.Date.AddDays(-(((int)time.DayOfWeek+6)%7)));}
    public static DateTime BeijingNow() {return TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow,"China Standard Time");}
    public static bool CanReset(Store s,DateTime weeklyClock) {return String.CompareOrdinal(s.ResetWeek??"",WeekKey(weeklyClock))<0;}
    public static double BudgetUsed(Store s,DateTime time) {return Math.Max(0,Total(s,time)-(s.ResetDay==Day(time)?s.ResetBaseline:0));}
    public static bool TryReset(Store s,DateTime localTime,DateTime weeklyClock) {
        if(!CanReset(s,weeklyClock))return false;
        s.ResetWeek=WeekKey(weeklyClock);s.ResetDay=Day(localTime);s.ResetBaseline=Total(s,localTime);
        s.Alerts.RemoveAll(a=>a.StartsWith(Day(localTime)+":",StringComparison.Ordinal));return true;
    }
    public static void Prune(Store s, DateTime today) {
        string first = Day(today.Date.AddDays(-6)), last = Day(today.Date);
        s.Records.RemoveAll(r => String.CompareOrdinal(r.Day, first) < 0 || String.CompareOrdinal(r.Day, last) > 0);
        s.Alerts.RemoveAll(a => a.Length < 10 || String.CompareOrdinal(a.Substring(0, 10), first) < 0 || String.CompareOrdinal(a.Substring(0, 10), last) > 0);
    }
    public static string Day(DateTime time) { return time.ToString("yyyy-MM-dd"); }
    public static double Total(Store s, DateTime time) { return s.Records.Where(r => r.Day == Day(time)).Sum(r => r.Seconds); }
    public static void Add(Store s, DateTime start, DateTime end, IList<string> names) {
        if (names.Count == 0 || end <= start) return;
        while (start < end) {
            DateTime stop = end < start.Date.AddDays(1) ? end : start.Date.AddDays(1);
            double seconds = (stop - start).TotalSeconds / names.Count;
            foreach (string name in names) {
                string day = Day(start);
                Record r = s.Records.Find(x => x.Day == day && x.Game == name);
                if (r == null) { r = new Record { Day = day, Game = name }; s.Records.Add(r); }
                r.Seconds += seconds;
            }
            start = stop;
        }
    }
    public static int AlertSlot(double seconds) {
        if(seconds<3600)return -1;
        if(seconds<4800)return 60;
        if(seconds<5400)return 80;
        return 90;
    }
}
public class Detector {
    public List<Game> Games = new List<Game>();
    public Dictionary<int,string> RunningProcesses=new Dictionary<int,string>();
    public string Status = "尚未找到 Steam，可在详情中选择 Steam 文件夹";
    public static string Field(string input, string key) {
        Match m = Regex.Match(input, "\"" + key + "\"\\s*\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value.Replace("\\\\", "\\") : "";
    }
    public void Refresh(Store s) {
        var roots = new HashSet<string>(s.Roots, StringComparer.OrdinalIgnoreCase);
        try { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")) {
            if (k != null) { string p = k.GetValue("SteamPath") as string; if (!String.IsNullOrEmpty(p)) roots.Add(p); }
        } } catch { }
        string standard = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        if (Directory.Exists(standard)) roots.Add(standard);
        foreach (string root in roots.ToArray()) {
            string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            try { if (File.Exists(vdf)) foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s*\"([^\"]+)\"")) roots.Add(m.Groups[1].Value.Replace("\\\\", "\\")); } catch { }
        }
        var games = new List<Game>();
        foreach (string root in roots) {
            try {
                string apps = Path.Combine(root, "steamapps");
                if (!Directory.Exists(apps)) continue;
                foreach (string file in Directory.GetFiles(apps, "appmanifest_*.acf")) {
                    try {
                        string text = File.ReadAllText(file), id = Field(text, "appid"), dir = Field(text, "installdir"), name = Field(text, "name");
                        if (id != "" && dir != "" && name != "" && id != "228980" && !games.Any(g => g.Id == id))
                            games.Add(new Game { Id = id, Name = name, Directory = Path.GetFullPath(Path.Combine(apps, "common", dir)) + Path.DirectorySeparatorChar });
                    } catch { }
                }
            } catch { }
        }
        Games = games;
        Status = games.Count == 0 ? "未发现已安装游戏；可选择 Steam 文件夹或手动添加游戏" : "已识别 " + games.Count + " 个 Steam 应用（工具类应用可排除）";
    }
    public List<string> Active(Store s) {
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Steam's per-app registry flag complements process-path detection for protected processes.
        foreach (Game game in Games) {
            try { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\Apps\" + game.Id)) {
                if (k != null && Convert.ToInt32(k.GetValue("Running", 0)) == 1) active.Add(game.Name);
            } } catch { }
        }
        var processes=Process.GetProcesses();
        var liveIds=new HashSet<int>(processes.Select(p=>p.Id));
        foreach(int id in RunningProcesses.Keys.ToArray()) {
            if(!liveIds.Contains(id))RunningProcesses.Remove(id);else active.Add(RunningProcesses[id]);
        }
        foreach (Process p in processes) {
            using (p) { try {
                bool hasWindow=p.MainWindowHandle!=IntPtr.Zero;
                if(!hasWindow && s.Manual.Count==0) continue;
                string path = p.MainModule.FileName;
                ManualGame manual = s.Manual.Find(m => String.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase));
                if (manual != null) {active.Add(manual.Name);RunningProcesses[p.Id]=manual.Name;}
                if (!hasWindow) continue;
                string exe = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                if (exe.Contains("crash") || exe.Contains("launcher") || exe.Contains("unins") || exe.Contains("setup")) continue;
                foreach (Game game in Games) if (path.StartsWith(game.Directory, StringComparison.OrdinalIgnoreCase)) {active.Add(game.Name);RunningProcesses[p.Id]=game.Name;}
            } catch { } }
        }
        return active.Where(n => !s.Excluded.Contains(n)).OrderBy(n => n).ToList();
    }
}
public class Ball : Form {
    public const int BallSize = 116;
    static readonly object audioLock=new object();
    static CancellationTokenSource audioCancellation;
    static SoundPlayer audioPlayer;
    public static bool IsQuickClick(bool moved, long milliseconds, Point start, Point end) {
        return !moved && milliseconds <= 300 && Math.Abs(end.X-start.X) < 3 && Math.Abs(end.Y-start.Y) < 3;
    }
    public Store Data;
    public Detector Detector = new Detector();
    readonly string dataPath;
    readonly bool previewMode;
    WorkbenchConfig workbench;
    bool syncingWorkbench;
    string workbenchStatus="未连接任务工作台";
    double CurrentLimit {get{return workbench==null?Quota.LimitSeconds:(Data.CloudDay==Ledger.Day(Ledger.BeijingNow())?Math.Max(0,Math.Min(90,Data.CloudEarnedMinutes))*60:0);}}
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer overlayTimer = new System.Windows.Forms.Timer();
    readonly Stopwatch watch = Stopwatch.StartNew();
    readonly Font numberFont=new Font("Segoe UI",22,FontStyle.Bold,GraphicsUnit.Pixel);
    readonly Font wideNumberFont=new Font("Segoe UI",17,FontStyle.Bold,GraphicsUnit.Pixel);
    readonly Font captionFont=new Font("Microsoft YaHei UI",9,FontStyle.Regular,GraphicsUnit.Pixel);
    readonly Font detailsFont=new Font("Microsoft YaHei UI",18,FontStyle.Regular,GraphicsUnit.Pixel);
    readonly Font chartFont=new Font("Microsoft YaHei UI",12,FontStyle.Regular,GraphicsUnit.Pixel);
    static readonly XmlSerializer storeSerializer=new XmlSerializer(typeof(Store));
    static readonly object serializerLock=new object();
    double lastStamp;
    DateTime lastWall = DateTime.Now;
    List<string> active = new List<string>();
    int ticks;
    bool suspended, exiting, dragging, pressed, scanPending, resourcesDisposed;
    long pressedAt;
    Point down, origin;
    NotifyIcon tray;
    ToolTip tip = new ToolTip();
    Form details;
    WorkbenchWindow workbenchWindow;
    ForcedWarning warning;
    public bool WarningActive {get {return warning!=null && !warning.IsDisposed;}}
    ListView rows;
    Label summary;
    Chart gameChart,totalChart;
    Button resetButton;
    string chartKey="";
    string saveError = "";
    IntPtr lastForeground;
    long nextOverlayRefresh;
    long nextPaintLog;
    public int PaintFailureCount {get;private set;}
    public bool LastPaintSucceeded {get;private set;}
    [DllImport("user32.dll", SetLastError=true)] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x08000080; return cp; } }
    public bool OverlayStylesReady { get { return (GetWindowLong(Handle,-20) & 0x08080088) == 0x08080088; } }
    public void RestoreOverlay() {
        if(WarningActive) {RaiseWarning();return;}
        if(workbenchWindow!=null && !workbenchWindow.IsDisposed && workbenchWindow.ContainsFocus)return;
        if(!IsHandleCreated || !Visible || IsDisposed) return;
        if(pressed || (ContextMenuStrip!=null && ContextMenuStrip.Visible) || (details!=null && details.ContainsFocus)) return;
        IntPtr foreground=GetForegroundWindow();
        if(foreground==lastForeground && watch.ElapsedMilliseconds<nextOverlayRefresh && (GetWindowLong(Handle,-20)&8)!=0) return;
        lastForeground=foreground;nextOverlayRefresh=watch.ElapsedMilliseconds+10000;
        // Reinsert at the front of the topmost group without taking keyboard focus.
        SetWindowPos(Handle,new IntPtr(-1),0,0,0,0,0x0013);
    }
    protected override void OnShown(EventArgs e) { base.OnShown(e); RestoreOverlay(); }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); RestoreOverlay(); }
    public Ball(string path, bool preview) {
        dataPath = path;
        previewMode=preview;
        if(!preview)workbench=WorkbenchConfig.Load();
        Data = ReadStore(path);
        Data.Volume=Math.Max(0,Math.Min(100,Data.Volume));
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false; TopMost = true; DoubleBuffered = true;
        // Layered composition lets Windows composite this GDI window over borderless games.
        Opacity = 0.99;
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(BallSize,BallSize); BackColor = Color.FromArgb(24,29,39);
        using (GraphicsPath gp = new GraphicsPath()) { gp.AddEllipse(1,1,BallSize-2,BallSize-2); Region = new Region(gp); }
        StartPosition = FormStartPosition.Manual;
        Rectangle area = Screen.PrimaryScreen.WorkingArea;
        Location = Data.X < 0 ? new Point(area.Right-BallSize-18, area.Top+180) : new Point(Data.X,Data.Y);
        Clamp();
        MouseDown += delegate(object sender, MouseEventArgs e) { if(e.Button == MouseButtons.Left) { down=System.Windows.Forms.Cursor.Position; origin=Location; dragging=false; pressed=true; pressedAt=watch.ElapsedMilliseconds; Capture=true; } };
        MouseMove += delegate(object sender, MouseEventArgs e) {
            if(!pressed || !Capture) return;
            int dx=System.Windows.Forms.Cursor.Position.X-down.X, dy=System.Windows.Forms.Cursor.Position.Y-down.Y;
            if(Math.Abs(dx)>=3 || Math.Abs(dy)>=3) dragging=true;
            if(dragging) Location = new Point(origin.X+dx,origin.Y+dy);
        };
        MouseUp += delegate(object sender, MouseEventArgs e) {
            if(e.Button != MouseButtons.Left || !pressed) return;
            Point end=System.Windows.Forms.Cursor.Position;
            bool click=IsQuickClick(dragging,watch.ElapsedMilliseconds-pressedAt,down,end);
            if(!click) Location=new Point(origin.X+end.X-down.X,origin.Y+end.Y-down.Y);
            pressed=false; Capture=false;
            if(!click) { Clamp(); Data.X=Left; Data.Y=Top; Save(); } else OpenWorkbench();
        };
        MouseCaptureChanged += delegate { if(!Capture) { pressed=false; dragging=false; } };
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开任务工作台", null, delegate { OpenWorkbench(); });
        menu.Items.Add("游戏统计与设置", null, delegate { OpenDetails(); });
        menu.Items.Add("重新置顶", null, delegate { Show(); Clamp(); RestoreOverlay(); });
        menu.Items.Add("试听提醒音", null, delegate { PlayTone(); });
        menu.Items.Add("隐藏悬浮球（托盘可恢复）", null, delegate { Hide(); });
        menu.Items.Add("退出", null, delegate { ExitApp(); });
        ContextMenuStrip=menu;
        if(preview) return;
        Save();
        tray = new NotifyIcon { Icon = SystemIcons.Information, Visible = true, Text = "Steam 时间球", ContextMenuStrip = menu };
        tray.DoubleClick += delegate { Show(); Clamp(); RestoreOverlay(); };
        SystemEvents.PowerModeChanged += PowerChanged;
        SystemEvents.SessionSwitch += SessionChanged;
        Detector.Refresh(Data); active=Detector.Active(Data);
        lastWall=DateTime.Now; lastStamp=watch.Elapsed.TotalSeconds;
        timer.Interval=2000; timer.Tick += Tick; timer.Start();
        overlayTimer.Interval=500; overlayTimer.Tick+=delegate { RestoreOverlay(); }; overlayTimer.Start();
        UpdateDisplay();
    }
    void RaiseWarning() {
        if(!WarningActive || !warning.IsHandleCreated)return;
        if(warning.WindowState!=FormWindowState.Normal)warning.WindowState=FormWindowState.Normal;
        if(!warning.Visible)warning.Show();
        SetWindowPos(warning.Handle,new IntPtr(-1),0,0,0,0,0x0013);
    }
    void UpdateWarning(bool confirmedScan) {
        bool hasGames=active.Count>0;
        bool shouldWarn=workbench==null?Quota.NeedsWarning(Ledger.BudgetUsed(Data,DateTime.Now),hasGames,WarningActive):Quota.NeedsEarnedWarning(CurrentLimit,Ledger.BudgetUsed(Data,DateTime.Now),hasGames,WarningActive);
        if(shouldWarn) {
            if(!WarningActive) {
                if(details!=null && !details.IsDisposed)details.Close();
                if(workbenchWindow!=null && !workbenchWindow.IsDisposed)workbenchWindow.Close();
                warning=new ForcedWarning(active,delegate {QueueDetection(false);});
                if(workbench!=null)warning.SetWorkbenchMessage(CurrentLimit/60);
                if(previewMode) {warning.ShowInTaskbar=false;warning.StartPosition=FormStartPosition.Manual;warning.Location=new Point(-30000,-30000);}
                warning.Show();if(!previewMode)warning.Activate();
                Save();PlayTone(6,true,true);
            } else {
                warning.UpdateGames(active);RaiseWarning();
                lock(audioLock) {if(audioCancellation==null)PlayTone(6,true,true);}
            }
        } else if(WarningActive && confirmedScan && !hasGames) {
            warning.ReleaseAfterGamesExit();warning.Dispose();warning=null;StopTone();Save();
        }
    }
    public static Store ReadStore(string path) {
        foreach(string file in new [] {path, path+".bak"}) {
            try { if(File.Exists(file)) using(var stream=File.OpenRead(file)) {lock(serializerLock) return (Store)storeSerializer.Deserialize(stream);} } catch(Exception error) {DiagnosticsLog.Write("read settings",error);}
        }
        return new Store();
    }
    static void WriteSnapshot(string path, Store data) {
        string temp=path+".tmp";
        using(var stream=File.Create(temp)) {lock(serializerLock) storeSerializer.Serialize(stream,data);}
        if(File.Exists(path)) File.Replace(temp,path,null); else File.Move(temp,path);
    }
    public static void Persist(string path, Store data, DateTime today) {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        Ledger.Prune(data,today);
        Store previous=File.Exists(path) || File.Exists(path+".bak") ? ReadStore(path) : data;
        Ledger.Prune(previous,today);
        WriteSnapshot(path+".bak",previous);
        WriteSnapshot(path,data);
    }
    void Save() {
        try {
            Persist(dataPath,Data,DateTime.Now);
            saveError="";
        } catch(Exception e) { saveError="记录保存失败："+e.Message; }
    }
    void Clamp() {
        Rectangle area=Screen.FromPoint(Location).WorkingArea;
        Location=new Point(Math.Max(area.Left,Math.Min(Left,area.Right-Width)),Math.Max(area.Top,Math.Min(Top,area.Bottom-Height)));
    }
    void PowerChanged(object sender, PowerModeChangedEventArgs e) {
        if(IsDisposed) return;
        BeginInvoke((Action)delegate { suspended=e.Mode == PowerModes.Suspend; lastWall=DateTime.Now; lastStamp=watch.Elapsed.TotalSeconds; active.Clear(); Save(); });
    }
    void SessionChanged(object sender, SessionSwitchEventArgs e) {
        if(e.Reason == SessionSwitchReason.SessionLock || e.Reason == SessionSwitchReason.SessionUnlock) {
            BeginInvoke((Action)delegate { lastWall=DateTime.Now; lastStamp=watch.Elapsed.TotalSeconds; active.Clear(); });
        }
    }
    void Account() {
        DateTime now=DateTime.Now; double stamp=watch.Elapsed.TotalSeconds, elapsed=stamp-lastStamp;
        // A delayed UI tick, suspend, or wall-clock adjustment must not create fictitious hours.
        if(!suspended && elapsed > 0 && elapsed < 10 && Math.Abs((now-lastWall).TotalSeconds-elapsed)<2)
            Ledger.Add(Data,now.AddSeconds(-elapsed),now,active);
        lastWall=now; lastStamp=stamp;
    }
    void Tick(object sender, EventArgs e) {
        Account();
        ticks++;
        if(workbench!=null && (ticks==1 || ticks%15==0))QueueWorkbenchSync();
        QueueDetection(ticks % 30 == 0);
        double total=Ledger.BudgetUsed(Data,DateTime.Now);
        UpdateWarning(false);
        int slot=workbench==null?Ledger.AlertSlot(total):Quota.EarnedAlertSlot(total,CurrentLimit);
        string key=Ledger.Day(DateTime.Now)+":"+slot;
        if(slot >= 0 && active.Count>0 && !Data.Alerts.Contains(key) && !WarningActive) { Data.Alerts.Add(key); Save(); PlayTone(); }
        if(ticks % 5 == 0) Save();
        if(ticks % 150 == 0) DiagnosticsLog.Write("periodic");
        UpdateDisplay();
    }
    void QueueWorkbenchSync() {
        if(syncingWorkbench || workbench==null || previewMode)return;
        syncingWorkbench=true;var config=workbench;double spent=Ledger.BudgetUsed(Data,DateTime.Now);
        ThreadPool.QueueUserWorkItem(delegate {
            string day="",status="",completed="";int earned=0;bool ok=false;
            try {
                var serializer=new JavaScriptSerializer();
                try {if(!String.IsNullOrEmpty(config.CoachStatePath)&&File.Exists(config.CoachStatePath)) {
                    var pointer=serializer.Deserialize<Dictionary<string,object>>(File.ReadAllText(config.CoachStatePath));
                    if(pointer.ContainsKey("status")&&Convert.ToString(pointer["status"])=="completed"&&pointer.ContainsKey("completion_basis")&&Convert.ToString(pointer["completion_basis"])=="explicit_user_confirmation")completed=Convert.ToString(pointer["date"]);
                }}catch(Exception error){DiagnosticsLog.Write("read IELTS completion",error);}
                using(var client=new TimedWebClient()) {
                    ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;
                    client.Encoding=System.Text.Encoding.UTF8;client.Headers[HttpRequestHeader.ContentType]="application/json";
                    if(!String.IsNullOrEmpty(config.GatewayToken))client.Headers["OAI-Sites-Authorization"]="Bearer "+config.GatewayToken;
                    var response=serializer.Deserialize<Dictionary<string,object>>(client.UploadString(config.Site.TrimEnd('/')+"/api/device/poll",serializer.Serialize(new {deviceKey=config.DeviceKey,spentSeconds=spent,ieltsCompletedDay=completed})));
                    day=Convert.ToString(response["day"]);earned=Math.Max(0,Math.Min(90,Convert.ToInt32(response["earnedMinutes"])));ok=true;status="已同步工作台 · 已赚 "+earned+" 分钟";
                }
            } catch(Exception error) {status="工作台同步失败："+error.GetType().Name;DiagnosticsLog.Write("workbench sync",error);}
            try {if(IsDisposed||!IsHandleCreated)return;BeginInvoke((Action)delegate {
                syncingWorkbench=false;if(IsDisposed)return;workbenchStatus=status;if(ok){Data.CloudDay=day;Data.CloudEarnedMinutes=earned;Save();}UpdateDisplay();
            });}catch(InvalidOperationException){}
        });
    }
    void OpenWorkbenchSettings() {
        if(WarningActive)return;
        using(var form=new Form {Text="任务工作台配对",ClientSize=new Size(600,460),Font=detailsFont,StartPosition=FormStartPosition.CenterParent,AutoScaleMode=AutoScaleMode.None}) {
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(20),ColumnCount=1,RowCount=7};
            var defaults=workbench??WorkbenchConfig.Defaults();
            var site=new TextBox {Text=defaults==null?"https://your-workbench.chatgpt.site":defaults.Site,Dock=DockStyle.Fill};
            var key=new TextBox {Text=workbench==null?"":workbench.DeviceKey,UseSystemPasswordChar=true,Dock=DockStyle.Fill};
            var gateway=new TextBox {Text=defaults==null?"":defaults.GatewayToken,UseSystemPasswordChar=true,Dock=DockStyle.Fill};
            foreach(var pair in new[]{new {Label="工作台地址",Input=site},new {Label="网站中创建的设备密钥",Input=key},new {Label="本机登录网关访问凭据（不应分享）",Input=gateway}}) {var panel=new Panel {Dock=DockStyle.Fill,Height=72};panel.Controls.Add(new Label {Text=pair.Label,Dock=DockStyle.Top,Height=27});pair.Input.Dock=DockStyle.Bottom;panel.Controls.Add(pair.Input);layout.Controls.Add(panel);layout.RowStyles.Add(new RowStyle(SizeType.Absolute,72));}
            layout.Controls.Add(new Label {Text="连接后按任务赚取分钟数，无额度时立即强提醒。凭据只加密保存在本机，不会进入分享包。",Dock=DockStyle.Fill,AutoSize=true});layout.RowStyles.Add(new RowStyle(SizeType.Absolute,70));
            var save=new Button {Text="保存并同步",Dock=DockStyle.Top,Height=45};save.Click+=delegate {try{string deviceKey=key.Text.Trim();if(deviceKey.StartsWith("{")){var parsed=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(deviceKey);deviceKey=Convert.ToString(parsed["deviceKey"]);}
                var config=new WorkbenchConfig {Site=site.Text.Trim(),DeviceKey=deviceKey,GatewayToken=gateway.Text.Trim(),CoachStatePath=defaults==null?"":defaults.CoachStatePath};WorkbenchConfig.Save(config);workbench=config;workbenchStatus="等待首次同步";QueueWorkbenchSync();form.Close();}catch(Exception e){MessageBox.Show(form,e.Message,"配对未保存");}};layout.Controls.Add(save);form.Controls.Add(layout);form.ShowDialog(details);
        }
    }
    void OpenWorkbench() {
        if(WarningActive){RaiseWarning();return;}
        if(workbenchWindow!=null&&!workbenchWindow.IsDisposed){workbenchWindow.Show();workbenchWindow.Activate();return;}
        var defaults=workbench??WorkbenchConfig.Defaults();
        string site=defaults==null?"https://your-workbench.chatgpt.site":defaults.Site;
        workbenchWindow=new WorkbenchWindow(site,delegate {OpenDetails();},delegate(string deviceKey) {
            var config=new WorkbenchConfig {Site=site,DeviceKey=deviceKey,GatewayToken=defaults==null?"":defaults.GatewayToken,CoachStatePath=defaults==null?"":defaults.CoachStatePath};
            WorkbenchConfig.Save(config);workbench=config;workbenchStatus="等待首次同步";QueueWorkbenchSync();
        });
        workbenchWindow.FormClosed+=delegate {workbenchWindow=null;};
        workbenchWindow.Show();Rectangle area=Screen.FromControl(workbenchWindow).WorkingArea;workbenchWindow.Size=new Size(Math.Min(workbenchWindow.Width,area.Width-40),Math.Min(workbenchWindow.Height,area.Height-40));workbenchWindow.Activate();
    }
    void QueueDetection(bool refresh) {
        if(scanPending) return;
        scanPending=true;
        var snapshot=new Store { Roots=new List<string>(Data.Roots), Manual=new List<ManualGame>(Data.Manual), Excluded=new List<string>(Data.Excluded) };
        var detector=new Detector { Games=Detector.Games, Status=Detector.Status, RunningProcesses=new Dictionary<int,string>(Detector.RunningProcesses) };
        ThreadPool.QueueUserWorkItem(delegate {
            List<string> found=null;
            try { if(refresh) detector.Refresh(snapshot); found=detector.Active(snapshot); } catch(Exception error) {DiagnosticsLog.Write("detect games",error);}
            try {
                if(IsDisposed || !IsHandleCreated) return;
                BeginInvoke((Action)delegate {
                    if(IsDisposed || exiting) return;
                    Account();
                    if(found!=null) {Detector=detector;active=found;UpdateWarning(true);}
                    scanPending=false;
                });
            } catch(InvalidOperationException) { }
        });
    }
    public static byte[] CreateReminderWave(int volume=30, int pulses=6) {
                double gain=0.10*Math.Max(0,Math.Min(100,volume))/100;
                using(var memory=new MemoryStream()) {
                    using(var writer=new BinaryWriter(memory,System.Text.Encoding.UTF8,true)) {
                        int rate=22050, pulse=9261, gap=rate, period=pulse+gap;
                        int count=pulses*pulse+(pulses-1)*gap;
                        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36+count*2);
                        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
                        writer.Write(rate); writer.Write(rate*2); writer.Write((short)2); writer.Write((short)16);
                        writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(count*2);
                        for(int i=0;i<count;i++) {
                            int offset=i%period;
                            double local=(double)offset/rate;
                            double amp=offset<pulse ? Math.Sin(Math.PI*offset/pulse)*gain : 0;
                            writer.Write((short)(Math.Sin(2*Math.PI*660*local)*amp*32767));
                        }
                    }
                    return memory.ToArray();
                }
    }
    public static void StopTone() {
        lock(audioLock) {
            if(audioCancellation!=null) {audioCancellation.Cancel(); audioCancellation=null;}
            if(audioPlayer!=null) {try {audioPlayer.Stop();} catch { } audioPlayer=null;}
        }
    }
    public void PlayTone(int pulses=6,bool force=false,bool continuous=false) {
        if(previewMode)return;
        if(WarningActive && !force) {RaiseWarning();return;}
        StopTone();
        int volume=force?Quota.WarningVolume(Data.Volume):Data.Volume;
        if(volume<=0) return;
        var cancellation=new CancellationTokenSource();
        lock(audioLock) {audioCancellation=cancellation;}
        ThreadPool.QueueUserWorkItem(delegate {
            try {
                using(var memory=new MemoryStream(CreateReminderWave(volume,1)))
                using(var player=new SoundPlayer(memory)) {
                    player.Load();
                    for(int i=0;continuous || i<pulses;i++) {
                        lock(audioLock) {if(cancellation.IsCancellationRequested) return; audioPlayer=player;}
                        if(cancellation.IsCancellationRequested) return;
                        player.PlaySync();
                        if((continuous || i<pulses-1) && cancellation.Token.WaitHandle.WaitOne(1000)) return;
                    }
                }
            } catch(Exception error) {DiagnosticsLog.Write("audio",error);}
            finally {
                lock(audioLock) {if(audioCancellation==cancellation) {audioPlayer=null;audioCancellation=null;}}
                cancellation.Dispose();
            }
        });
    }
    static bool IsDrawingFailure(Exception error) {
        return error is OutOfMemoryException || error is ExternalException || error is ArgumentException;
    }
    void ReportPaintFailure(string stage,Exception error) {
        PaintFailureCount++;LastPaintSucceeded=false;
        if(PaintFailureCount==1 || watch.ElapsedMilliseconds>=nextPaintLog) {
            DiagnosticsLog.Write(stage,error);nextPaintLog=watch.ElapsedMilliseconds+30000;
        }
    }
    protected override void OnPaintBackground(PaintEventArgs e) {
        if(resourcesDisposed) return;
        try {base.OnPaintBackground(e);}
        catch(Exception error) {if(!IsDrawingFailure(error))throw;ReportPaintFailure("paint background",error);}
    }
    protected override void OnPaint(PaintEventArgs e) {
        if(resourcesDisposed) return;
        GraphicsState saved=null;
        try {
            saved=e.Graphics.Save();
            base.OnPaint(e);PaintBall(e.Graphics);LastPaintSucceeded=true;
        } catch(Exception error) {
            if(!IsDrawingFailure(error)) throw;
            ReportPaintFailure("paint foreground",error);
            // Contain recoverable GDI+ errors before WinForms permanently switches to its red-X placeholder.
            try {
                e.Graphics.ResetTransform();e.Graphics.Clear(BackColor);
                TextRenderer.DrawText(e.Graphics,"!",SystemFonts.MessageBoxFont,ClientRectangle,Color.White,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
            } catch(Exception fallbackError) {if(!IsDrawingFailure(fallbackError))throw;}
        } finally {
            if(saved!=null) try {e.Graphics.Restore(saved);} catch(Exception error) {if(!IsDrawingFailure(error))throw;ReportPaintFailure("restore graphics",error);}
        }
    }
    void PaintBall(Graphics g) {
        g.SmoothingMode=SmoothingMode.AntiAlias;
        g.ScaleTransform(2,2);
        double used=Ledger.BudgetUsed(Data,DateTime.Now),limit=CurrentLimit,remain=Math.Max(0,limit-used);
        Color color=remain<=600?Color.FromArgb(255,105,114):remain<=1800?Color.FromArgb(255,202,96):Color.FromArgb(97,219,174);
        using(var track=new Pen(Color.FromArgb(53,61,76),4)) g.DrawEllipse(track,5,5,47,47);
        if(remain>0) {
            float sweep=(float)(remain/limit*359.9);
            if(sweep<3) {using(var dot=new SolidBrush(color))g.FillEllipse(dot,26.5f,3,4,4);}
            else using(var ring=new Pen(color,4)) {
                ring.StartCap=LineCap.Round;ring.EndCap=LineCap.Round;
                if(remain>=limit)g.DrawEllipse(ring,5,5,47,47);else g.DrawArc(ring,5,5,47,47,-90,sweep);
            }
        }
        string label=limit<=0?"锁":used>=limit?"+"+(int)((used-limit)/60):Math.Ceiling(remain/60).ToString();
        using(var brush=new SolidBrush(color))
        using(var format=new StringFormat { Alignment=StringAlignment.Center, LineAlignment=StringAlignment.Center })
            g.DrawString(label,label.Length>3?wideNumberFont:numberFont,brush,new RectangleF(0,9,58,30),format);
        using(var brush=new SolidBrush(Color.FromArgb(184,194,210)))
        using(var format=new StringFormat { Alignment=StringAlignment.Center })
            g.DrawString(limit<=0?"待完成":used>=limit?"超时分":"剩余分",captionFont,brush,new RectangleF(0,36,58,16),format);
    }
    void UpdateDisplay() {
        if(workbench!=null && workbenchWindow!=null && !workbenchWindow.IsDisposed)workbenchWindow.UpdateSyncStatus(workbenchStatus);
        Invalidate(); double total=Ledger.BudgetUsed(Data,DateTime.Now);
        tip.SetToolTip(this,"Steam 时间球 · 今日 "+Duration(total)+" / 90 分钟\n"+(active.Count==0?"等待游戏运行":String.Join("、",active))+"\n单击工作台 · 拖动移动 · 右键统计与设置");
        if(tray!=null) tray.Text="Steam 时间球 · 今日 "+Duration(total);
        RefreshDetails();
    }
    static string Duration(double seconds) { return ((int)seconds/3600)+"时"+(((int)seconds%3600)/60)+"分"+((int)seconds%60)+"秒"; }
    void RefreshDetails() {
        if(details==null || details.IsDisposed) return;
        double total=Ledger.Total(Data,DateTime.Now);
        summary.Text="今日实际 "+Duration(total)+" / 额度已用 "+Duration(Ledger.BudgetUsed(Data,DateTime.Now))+" / 已赚 "+(CurrentLimit/60)+" 分钟\n"+(active.Count==0?"当前：未检测到游戏":"正在运行："+String.Join("、",active))+"\n"+(workbench==null?Detector.Status:workbenchStatus)+"\n每周重置："+(Ledger.CanReset(Data,Ledger.BeijingNow())?"本周可用 1 次":"本周已使用，下周一恢复")+(saveError==""?"":"\n"+saveError);
        if(resetButton!=null)resetButton.Enabled=Ledger.CanReset(Data,Ledger.BeijingNow()) && Ledger.BudgetUsed(Data,DateTime.Now)>0;
        RefreshCharts();
        rows.BeginUpdate(); rows.Items.Clear();
        string since=Ledger.Day(DateTime.Now.AddDays(-6));
        foreach(var r in Data.Records.Where(r=>String.CompareOrdinal(r.Day,since)>=0).OrderByDescending(r=>r.Day).ThenByDescending(r=>r.Seconds))
            rows.Items.Add(new ListViewItem(new [] {r.Day,r.Game,Duration(r.Seconds)}));
        rows.EndUpdate();
    }
    Chart CreateChart(bool columns) {
        var chart=new Chart {Dock=DockStyle.Fill,BackColor=Color.White};
        var area=new ChartArea("Usage");area.AxisY.Title="分钟";area.AxisY.Minimum=0;area.AxisX.Interval=1;
        area.AxisX.IsLabelAutoFit=false;area.AxisY.IsLabelAutoFit=false;
        area.AxisX.LabelStyle.Font=chartFont;area.AxisY.LabelStyle.Font=chartFont;area.AxisY.TitleFont=chartFont;
        area.AxisX.LabelStyle.IsStaggered=false;
        area.AxisX.MajorGrid.Enabled=false;area.AxisY.MajorGrid.LineColor=Color.FromArgb(230,234,240);
        chart.ChartAreas.Add(area);if(columns)chart.Legends.Add(new Legend {Docking=Docking.Bottom,MaximumAutoSize=20,Font=chartFont});
        return chart;
    }
    void RefreshCharts() {
        if(gameChart==null || gameChart.IsDisposed || totalChart==null || totalChart.IsDisposed)return;
        string first=Ledger.Day(DateTime.Now.AddDays(-6)),last=Ledger.Day(DateTime.Now);
        var records=Data.Records.Where(r=>String.CompareOrdinal(r.Day,first)>=0 && String.CompareOrdinal(r.Day,last)<=0).ToList();
        string key=last+String.Join("|",records.Select(r=>r.Day+":"+r.Game+":"+(int)(r.Seconds/60)));
        if(key==chartKey)return;chartKey=key;
        foreach(var series in gameChart.Series.ToArray()) {gameChart.Series.Remove(series);series.Dispose();}
        foreach(var series in totalChart.Series.ToArray()) {totalChart.Series.Remove(series);series.Dispose();}
        foreach(string name in records.Select(r=>r.Game).Distinct().OrderBy(n=>n)) {
            var series=new Series(name) {ChartType=SeriesChartType.Column,IsXValueIndexed=true,ToolTip="#SERIESNAME · #AXISLABEL：#VALY{0.0} 分钟"};
            for(int i=0;i<7;i++) {DateTime day=DateTime.Now.Date.AddDays(i-6);series.Points.AddXY(day.ToString("MM-dd"),records.Where(r=>r.Day==Ledger.Day(day)&&r.Game==name).Sum(r=>r.Seconds)/60);}
            gameChart.Series.Add(series);
        }
        var daily=new Series("每日总时长") {ChartType=SeriesChartType.Line,BorderWidth=3,MarkerStyle=MarkerStyle.Circle,MarkerSize=7,IsXValueIndexed=true,Color=Color.FromArgb(35,145,112),ToolTip="#AXISLABEL：#VALY{0.0} 分钟"};
        for(int i=0;i<7;i++) {DateTime day=DateTime.Now.Date.AddDays(i-6);daily.Points.AddXY(day.ToString("MM-dd"),records.Where(r=>r.Day==Ledger.Day(day)).Sum(r=>r.Seconds)/60);}
        totalChart.Series.Add(daily);
        double maximum=records.GroupBy(r=>r.Day).Select(group=>group.Sum(r=>r.Seconds)/60).DefaultIfEmpty(0).Max();
        foreach(var chart in new [] {gameChart,totalChart})chart.ChartAreas[0].AxisY.Interval=Math.Max(1,Math.Ceiling(maximum/4));
    }
    void ResetToday() {
        if(WarningActive)return;
        Account();
        if(!Ledger.CanReset(Data,Ledger.BeijingNow()))return;
        if(MessageBox.Show(details,"使用本周唯一一次重置，将今日额度已用时长清零。\n历史记录和图表保留真实时长，下一次机会在北京时间下周一恢复。", "重置今日额度",MessageBoxButtons.OKCancel,MessageBoxIcon.Question)!=DialogResult.OK)return;
        string week=Data.ResetWeek,day=Data.ResetDay;double baseline=Data.ResetBaseline;var alerts=new List<string>(Data.Alerts);
        if(!Ledger.TryReset(Data,DateTime.Now,Ledger.BeijingNow()))return;
        Save();if(saveError!="") {Data.ResetWeek=week;Data.ResetDay=day;Data.ResetBaseline=baseline;Data.Alerts=alerts;MessageBox.Show(details,saveError,"未能保存重置");}
        UpdateDisplay();
    }
    public bool CheckVolumeControl() {
        OpenDetails(false);
        try {
            var slider=Descendants(details).OfType<TrackBar>().Single();
            slider.Value=67;
            bool saved=Data.Volume==67 && ReadStore(dataPath).Volume==67;
            slider.Value=0;
            var number=Descendants(details).OfType<NumericUpDown>().Single();
            bool muted=Data.Volume==0 && ReadStore(dataPath).Volume==0 && number.Value==0;
            number.Value=82;
            return saved && muted && slider.Value==82 && ReadStore(dataPath).Volume==82 && slider.Minimum==0 && slider.Maximum==100;
        }
        finally { details.Dispose(); details=null; }
    }
    public bool CheckCharts(string folder) {
        var original=Data.Records;
        try {
            Data.Records=new List<Record> {new Record {Day=Ledger.Day(DateTime.Now),Game="游戏 A",Seconds=900},new Record {Day=Ledger.Day(DateTime.Now),Game="游戏 B",Seconds=1800}};
            OpenDetails(false);RefreshCharts();
            bool correct=gameChart.Series.Count==2 && gameChart.Series.All(s=>s.Points.Count==7) && totalChart.Series[0].Points.Count==7 && totalChart.Series[0].Points[6].YValues[0]==45 && totalChart.Series[0].Points[0].YValues[0]==0;
            correct &= gameChart.Series["游戏 A"].Points[6].YValues[0]==15 && gameChart.Series["游戏 B"].Points[6].YValues[0]==30;
            return RenderDetailsPreview(Path.Combine(folder,"statistics-preview.png")) && correct;
        } finally {Data.Records=original;if(details!=null){details.Dispose();details=null;}chartKey="";}
    }
    static IEnumerable<Control> Descendants(Control root) {
        foreach(Control child in root.Controls) {yield return child; foreach(Control nested in Descendants(child)) yield return nested;}
    }
    public bool RenderDetailsPreview(string path) {
        OpenDetails(false);
        try {
            details.ShowInTaskbar=false; details.StartPosition=FormStartPosition.Manual; details.Location=new Point(-30000,-30000);
            details.Show(); details.PerformLayout(); Application.DoEvents();
            using(var image=new Bitmap(details.Width,details.Height)) {details.DrawToBitmap(image,new Rectangle(Point.Empty,details.Size));image.Save(path);}
            var tabs=Descendants(details).OfType<TabControl>().FirstOrDefault();
            if(tabs!=null) {tabs.SelectedIndex=1;details.PerformLayout();Application.DoEvents();using(var image=new Bitmap(details.Width,details.Height)){details.DrawToBitmap(image,new Rectangle(Point.Empty,details.Size));image.Save(Path.Combine(Path.GetDirectoryName(path),Path.GetFileNameWithoutExtension(path)+"-totals.png"));}tabs.SelectedIndex=0;}
            bool fits=Descendants(details).OfType<Button>().All(b=>b.Bottom<=b.Parent.ClientSize.Height && b.Right<=b.Parent.ClientSize.Width);
            details.ClientSize=new Size(1280,760);details.PerformLayout();Application.DoEvents();
            fits &= Descendants(details).OfType<Button>().All(b=>b.Bottom<=b.Parent.ClientSize.Height && b.Right<=b.Parent.ClientSize.Width);
            return fits;
        } finally {details.Dispose();details=null;}
    }
    void OpenDetails(bool show=true) {
        if(WarningActive) {RaiseWarning();return;}
        if(details!=null && !details.IsDisposed) { if(show) {details.Show(); details.Activate();} return; }
        chartKey="";
        details=new DetailsWindow { Text="Steam 时间球 · 使用详情", StartPosition=FormStartPosition.CenterScreen, AutoScaleMode=AutoScaleMode.None, ClientSize=new Size(820,880), BackColor=Color.FromArgb(244,246,249), Font=detailsFont, Icon=SystemIcons.Information };
        details.SuspendLayout();
        var layout=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(24),ColumnCount=1,RowCount=5,AutoScroll=true};
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,132));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,190));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        details.Controls.Add(layout);
        summary=new Label {Dock=DockStyle.Fill,Padding=new Padding(16,12,16,8),BackColor=Color.White,ForeColor=Color.FromArgb(37,48,65),Margin=new Padding(0,0,0,14)};
        layout.Controls.Add(summary,0,0);
        var history=new GroupBox {Text="最近 7 天 · 游戏记录",Dock=DockStyle.Fill,Padding=new Padding(12),Margin=new Padding(0,0,0,16),MinimumSize=new Size(0,240)};
        rows=new ListView {Dock=DockStyle.Fill,View=View.Details,FullRowSelect=true,BorderStyle=BorderStyle.None,BackColor=Color.White,HideSelection=false};
        rows.Columns.Add("日期",110); rows.Columns.Add("游戏",360); rows.Columns.Add("累计时间",170);
        rows.Resize+=delegate {if(rows.ClientSize.Width>200) rows.Columns[1].Width=Math.Max(120,rows.ClientSize.Width-rows.Columns[0].Width-rows.Columns[2].Width-24);};
        var tabs=new TabControl {Dock=DockStyle.Fill};
        var gamesTab=new TabPage("各游戏 · 柱状图");var totalsTab=new TabPage("每日总和 · 折线图");var listTab=new TabPage("明细");
        gameChart=CreateChart(true);totalChart=CreateChart(false);gamesTab.Controls.Add(gameChart);totalsTab.Controls.Add(totalChart);listTab.Controls.Add(rows);
        tabs.TabPages.AddRange(new [] {gamesTab,totalsTab,listTab});history.Controls.Add(tabs);layout.Controls.Add(history,0,1);
        var sound=new GroupBox {Text="声音提醒",Dock=DockStyle.Fill,Padding=new Padding(14),Margin=new Padding(0,0,0,14)};
        var soundLayout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=3};
        soundLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        soundLayout.RowStyles.Add(new RowStyle(SizeType.Absolute,48)); soundLayout.RowStyles.Add(new RowStyle(SizeType.Absolute,54)); soundLayout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        sound.Controls.Add(soundLayout); layout.Controls.Add(sound,0,2);
        var volumeRow=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=3,RowCount=1,Margin=Padding.Empty};
        volumeRow.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        volumeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,64)); volumeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); volumeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,100));
        volumeRow.Controls.Add(new Label {Text="音量",AutoSize=true,Anchor=AnchorStyles.Left,Margin=Padding.Empty},0,0);
        var volumeSlider=new TrackBar {Dock=DockStyle.Fill,Minimum=0,Maximum=100,Value=Data.Volume,TickStyle=TickStyle.None,SmallChange=1,LargeChange=10,AccessibleName="提示音音量",Margin=new Padding(4,7,12,0)};
        var volumeNumber=new NumericUpDown {Minimum=0,Maximum=100,Value=Data.Volume,Dock=DockStyle.Fill,Margin=new Padding(0,5,0,0),AccessibleName="音量百分比"};
        volumeRow.Controls.Add(volumeSlider,1,0);volumeRow.Controls.Add(volumeNumber,2,0);soundLayout.Controls.Add(volumeRow,0,0);
        var soundButtons=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=false,Margin=Padding.Empty};
        AddButton(soundButtons,"试听 6 下",delegate {PlayTone();}); AddButton(soundButtons,"停止试听",delegate {if(!WarningActive)StopTone();});
        var volumeStatus=new Label {Text=Data.Volume==0?"已静音（0%）":"当前音量 "+Data.Volume+"%",AutoSize=true,Margin=new Padding(12,9,0,0),ForeColor=Color.FromArgb(35,117,88)};
        soundButtons.Controls.Add(volumeStatus);soundLayout.Controls.Add(soundButtons,0,1);
        soundLayout.Controls.Add(new Label {Text="拖动即保存 · 松开滑块试听 1 下 · 0% 静音 · 不改变游戏音量",Dock=DockStyle.Fill,ForeColor=Color.DimGray,AutoEllipsis=true},0,2);
        bool adjusting=false;
        Action<int> changeVolume=delegate(int value) {
            if(adjusting || WarningActive) return; adjusting=true;
            try {StopTone();Data.Volume=value;volumeSlider.Value=value;volumeNumber.Value=value;Save();volumeStatus.Text=value==0?"已静音（0%）":"已保存 "+value+"%";if(saveError!="")volumeStatus.Text="保存失败";}
            finally {adjusting=false;}
        };
        volumeSlider.ValueChanged+=delegate {changeVolume(volumeSlider.Value);};
        volumeNumber.ValueChanged+=delegate {changeVolume((int)volumeNumber.Value);};
        volumeSlider.MouseUp+=delegate(object sender,MouseEventArgs e) {if(e.Button==MouseButtons.Left)PlayTone(1);};
        volumeSlider.KeyUp+=delegate(object sender,KeyEventArgs e) {if(e.KeyCode==Keys.Left || e.KeyCode==Keys.Right || e.KeyCode==Keys.Up || e.KeyCode==Keys.Down)PlayTone(1);};
        var bar=new FlowLayoutPanel {Dock=DockStyle.Fill,AutoSize=true,WrapContents=true,Margin=new Padding(0,0,0,12)};
        AddButton(bar,"任务工作台配对",delegate {OpenWorkbenchSettings();});
        resetButton=new Button {Text="每周一次 · 重置今日额度",AutoSize=true,Padding=new Padding(10,6,10,6),Margin=new Padding(0,0,10,8)};resetButton.Click+=delegate {ResetToday();};bar.Controls.Add(resetButton);
        AddButton(bar,"添加游戏 EXE",delegate {
            using(var dialog=new OpenFileDialog { Filter="游戏程序 (*.exe)|*.exe",Title="选择实际游戏程序（勿选择 Steam 或启动器）" }) {
                if(dialog.ShowDialog(details)!=DialogResult.OK) return;
                if(!Data.Manual.Any(m=>String.Equals(m.Path,dialog.FileName,StringComparison.OrdinalIgnoreCase))) Data.Manual.Add(new ManualGame {Path=dialog.FileName,Name=Path.GetFileNameWithoutExtension(dialog.FileName)});
                Save();
            }
        });
        AddButton(bar,"选择 Steam 目录",delegate {
            using(var dialog=new FolderBrowserDialog {Description="选择包含 steamapps 的 Steam 或游戏库文件夹"}) {
                if(dialog.ShowDialog(details)==DialogResult.OK) { Data.Roots.Add(dialog.SelectedPath); Detector.Refresh(Data); Save(); UpdateDisplay(); }
            }
        });
        AddButton(bar,"管理识别 / 排除",delegate { ManageGames(); });
        AddButton(bar,"打开记录目录",delegate { Process.Start(Path.GetDirectoryName(dataPath)); });
        AddButton(bar,"创建开机启动",delegate { Startup(true); });
        AddButton(bar,"取消开机启动",delegate { Startup(false); });
        layout.Controls.Add(bar,0,3);
        layout.Controls.Add(new Label {AutoSize=true,Dock=DockStyle.Fill,Margin=Padding.Empty,ForeColor=Color.DimGray,Text="60 / 80 / 90 分钟声音提醒；105 分钟起强制弹窗并持续发声。\n退出所有被统计的游戏后才解除强提醒；静音时强提醒使用 30% 音量。"},0,4);
        details.ResumeLayout(true);
        RefreshDetails();
        if(show) {
            details.Show();
            Rectangle area=Screen.FromControl(details).WorkingArea;
            details.Size=new Size(Math.Min(details.Width,area.Width-40),Math.Min(details.Height,area.Height-40));
            details.MinimumSize=new Size(Math.Min(details.Width,650),Math.Min(details.Height,620));
            details.Activate();
        }
    }
    void ManageGames() {
        using(var form=new Form {Text="识别管理 · 勾选的应用不计时",ClientSize=new Size(510,390),StartPosition=FormStartPosition.CenterParent,Font=Font}) {
            var list=new CheckedListBox {Dock=DockStyle.Fill,CheckOnClick=true};
            var names=Detector.Games.Select(g=>g.Name).Concat(Data.Manual.Select(m=>m.Name)).Concat(Data.Excluded).Distinct().OrderBy(n=>n).ToList();
            foreach(string n in names) list.Items.Add(n,Data.Excluded.Contains(n));
            var save=new Button {Text="保存排除设置",Dock=DockStyle.Bottom,Height=38};
            save.Click+=delegate { Account(); Data.Excluded=list.CheckedItems.Cast<string>().ToList(); active=Detector.Active(Data); Save(); form.Close(); };
            form.Controls.Add(list); form.Controls.Add(save); form.ShowDialog(details);
        }
    }
    void Startup(bool enabled) {
        string file=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup),"SteamTimeBall.cmd");
        try {
            if(enabled) File.WriteAllText(file,"@echo off\r\nstart \"\" \""+Application.ExecutablePath+"\"\r\n",System.Text.Encoding.Default);
            else if(File.Exists(file)) File.Delete(file);
            MessageBox.Show(details,enabled?"已设置开机启动。请保持程序所在文件夹位置不变。":"已取消开机启动。","Steam 时间球");
        } catch(Exception e) { MessageBox.Show(details,e.Message,"设置失败"); }
    }
    static void AddButton(Control panel,string text,Action action) { var b=new Button {Text=text,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Padding=new Padding(10,6,10,6),Margin=new Padding(0,0,10,8),FlatStyle=FlatStyle.Flat,BackColor=Color.White}; b.FlatAppearance.BorderColor=Color.FromArgb(210,218,228); b.Click+=delegate {action();}; panel.Controls.Add(b); }
    void ExitApp() { if(WarningActive) {RaiseWarning();if(!previewMode)warning.Activate();return;}exiting=true;Close(); }
    public bool CheckWarningLifecycle() {
        if(!previewMode)throw new InvalidOperationException("Test requires isolated preview mode");
        var originalRecords=Data.Records;var originalActive=active;
        try {
            Data.Records=new List<Record> {new Record {Day=Ledger.Day(DateTime.Now),Game="Warning test",Seconds=Quota.WarningSeconds}};
            active=new List<string> {"Warning test"};UpdateWarning(true);
            bool started=WarningActive;warning.Close();ExitApp();
            bool cannotBypass=WarningActive && !exiting;
            Data.Records.Clear();UpdateWarning(true);bool latched=WarningActive;
            active.Clear();UpdateWarning(false);bool awaitsConfirmation=WarningActive;
            UpdateWarning(true);bool ended=!WarningActive;
            Data.Records.Add(new Record {Day=Ledger.Day(DateTime.Now),Game="Warning test",Seconds=Quota.WarningSeconds});active.Add("Warning test");UpdateWarning(true);
            bool restarted=WarningActive;active.Clear();UpdateWarning(true);
            return started && cannotBypass && latched && awaitsConfirmation && ended && restarted && !WarningActive;
        } finally {if(WarningActive){warning.ReleaseAfterGamesExit();warning.Dispose();warning=null;}Data.Records=originalRecords;active=originalActive;}
    }
    public void PauseAfterFailure() {
        timer.Stop();overlayTimer.Stop();StopTone();
        try {Account();Save();} catch { }
    }
    protected override void Dispose(bool disposing) {
        if(disposing && !resourcesDisposed) {
            resourcesDisposed=true;
            StopTone();
            timer.Stop();overlayTimer.Stop();timer.Dispose();overlayTimer.Dispose();
            SystemEvents.PowerModeChanged-=PowerChanged;SystemEvents.SessionSwitch-=SessionChanged;
            if(tray!=null) {tray.Visible=false;tray.Dispose();tray=null;}
            if(details!=null) {details.Dispose();details=null;}
            if(workbenchWindow!=null) {workbenchWindow.Dispose();workbenchWindow=null;}
            if(warning!=null) {warning.Dispose();warning=null;}
            tip.Dispose();if(ContextMenuStrip!=null) {ContextMenuStrip.Dispose();ContextMenuStrip=null;}
            if(Region!=null) {var old=Region;Region=null;old.Dispose();}
            numberFont.Dispose();wideNumberFont.Dispose();captionFont.Dispose();detailsFont.Dispose();chartFont.Dispose();
        }
        base.Dispose(disposing);
    }
    protected override void OnFormClosing(FormClosingEventArgs e) {
        if(!exiting && e.CloseReason==CloseReason.UserClosing) { e.Cancel=true; Hide(); return; }
        timer.Stop(); overlayTimer.Stop(); StopTone(); Account(); Save();
        base.OnFormClosing(e);
    }
}
public static class Program {
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [STAThread] public static void Main(string[] args) {
        SetProcessDPIAware(); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        string folder=AppDomain.CurrentDomain.BaseDirectory;
        if(args.Contains("--webview-smoke")) {
            try {using(var window=new WorkbenchWindow("https://your-workbench.chatgpt.site",delegate {},delegate(string key){},true)) {
                window.StartPosition=FormStartPosition.Manual;window.Location=new Point(-30000,-30000);window.ShowInTaskbar=false;
                int elapsed=0;var check=new System.Windows.Forms.Timer {Interval=250};check.Tick+=delegate {elapsed+=250;if(elapsed==250){window.Hide();window.Show();}if(window.BrowserSmokeOk||elapsed>=20000){check.Stop();File.WriteAllText(Path.Combine(folder,"webview-test-results.txt"),window.BrowserSmokeOk?"PASS: WebView2 initialized and loaded its native browser document":"FAIL: "+window.BrowserDiagnostic);Environment.ExitCode=window.BrowserSmokeOk?0:1;window.Close();}};check.Start();Application.Run(window);check.Dispose();
            }} catch(Exception error){File.WriteAllText(Path.Combine(folder,"webview-test-results.txt"),error.GetType().Name+": "+error.Message);Environment.ExitCode=1;}return;
        }
        if(args.Contains("--process-fixture")) {Thread.Sleep(10000);return;}
        // An earlier running copy may hold its renamed binary until the user exits it.
        try { File.Delete(Path.Combine(folder,"SteamTimeBall.obsolete")); } catch(IOException) { } catch(UnauthorizedAccessException) { }
        if(args.Contains("--smoke-test")) {
            try {
                var ball=new Ball(Path.Combine(folder,"smoke-data","history.xml"),false);
                ball.Shown+=delegate { ball.Hide(); };
                var stop=new System.Windows.Forms.Timer {Interval=6500};
                stop.Tick+=delegate {stop.Stop(); Application.Exit();}; stop.Start();
                Application.Run(ball);
                File.WriteAllText(Path.Combine(folder,"smoke-results.txt"),"PASS: native startup, timer, detection, save and shutdown");
            } catch(Exception error) { File.WriteAllText(Path.Combine(folder,"smoke-results.txt"),error.GetType().FullName+"\r\n"+error.Message+"\r\n"+error.StackTrace); Environment.ExitCode=1; }
            return;
        }
        if(args.Contains("--self-test")) {
            try { Tests.Run(folder); }
            catch(Exception error) { File.WriteAllText(Path.Combine(folder,"test-error.txt"),error.GetType().FullName+"\r\n"+error.Message+"\r\n"+error.StackTrace); Environment.ExitCode=1; }
            return;
        }
        bool created;
        using(var mutex=new Mutex(true,@"Local\SteamTimeBall.Singleton",out created)) {
            if(!created) return;
            DiagnosticsLog.Configure(Path.Combine(folder,"data","diagnostics.log"));
            DiagnosticsLog.Write("start build 2026-10-27 quota90 warning105");
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException+=delegate(object sender,ThreadExceptionEventArgs e) {
                DiagnosticsLog.Write("UI exception",e.Exception);
                var failed=Application.OpenForms.OfType<Ball>().FirstOrDefault();if(failed!=null)failed.PauseAfterFailure();
                MessageBox.Show("程序遇到异常。诊断已记录在 data/diagnostics.log，请退出后重新启动。","Steam 时间球",MessageBoxButtons.OK,MessageBoxIcon.Error);
                Application.Exit();
            };
            AppDomain.CurrentDomain.UnhandledException+=delegate(object sender,UnhandledExceptionEventArgs e) {DiagnosticsLog.Write("unhandled exception",e.ExceptionObject as Exception);};
            try {using(var ball=new Ball(Path.Combine(folder,"data","history.xml"),false)) Application.Run(ball);}
            catch(Exception error) {DiagnosticsLog.Write("startup/main loop",error);MessageBox.Show("程序未能正常运行。诊断已记录在 data/diagnostics.log。","Steam 时间球");}
            finally {DiagnosticsLog.Write("exit");}
        }
    }
}
public static class Tests {
    static int count;
    static void Check(bool value,string message) { if(!value) throw new Exception(message); count++; }
    public static void Run(string folder) {
        var s=new Store();
        Check(WorkbenchWindow.TrustedPage("https://your-workbench.chatgpt.site/settings","https://your-workbench.chatgpt.site"),"Embedded pairing accepts only expected HTTPS origin");
        Check(!WorkbenchWindow.TrustedPage("https://evil.example","https://your-workbench.chatgpt.site")&&!WorkbenchWindow.TrustedPage("http://your-workbench.chatgpt.site","https://your-workbench.chatgpt.site"),"Embedded pairing rejects other sites and HTTP");
        Check(Quota.NeedsEarnedWarning(0,0,true,false)&&!Quota.NeedsEarnedWarning(0,0,false,false),"No earned minutes warns only while a game is running");
        Check(!Quota.NeedsEarnedWarning(2700,3599,true,false)&&Quota.NeedsEarnedWarning(2700,3600,true,false),"Earned 45 minutes enforces warning at 60 minutes");
        Check(Quota.NeedsEarnedWarning(5400,0,true,true)&&!Quota.NeedsEarnedWarning(5400,9999,false,true),"Earned quota preserves warning latch and game-exit release");
        Check(Quota.EarnedAlertSlot(1800,2700)==15&&Quota.EarnedAlertSlot(2100,2700)==35&&Quota.EarnedAlertSlot(2700,2700)==45,"Reminders scale with earned quota");
        var defaults=WorkbenchConfig.Defaults();if(File.Exists(Path.Combine(Path.GetDirectoryName(WorkbenchConfig.ConfigPath),"workbench-defaults.dat")))Check(defaults!=null&&!String.IsNullOrEmpty(defaults.GatewayToken)&&defaults.Site.StartsWith("https://"),"Local encrypted workbench defaults can be decrypted");
        var resetState=new Store();DateTime saturday=new DateTime(2026,10,31,12,0,0);
        resetState.Records.Add(new Record {Day=Ledger.Day(saturday),Game="Reset test",Seconds=3600});resetState.Alerts.Add(Ledger.Day(saturday)+":60");
        Check(Ledger.TryReset(resetState,saturday,saturday) && Ledger.BudgetUsed(resetState,saturday)==0 && Ledger.Total(resetState,saturday)==3600 && resetState.Alerts.Count==0,"Weekly reset clears quota usage and reminders while preserving actual history");
        resetState.Records[0].Seconds+=1200;
        Check(Ledger.BudgetUsed(resetState,saturday)==1200 && !Ledger.TryReset(resetState,saturday,saturday),"Reset continues counting and cannot repeat within week");
        Check(!Ledger.CanReset(resetState,saturday.AddDays(1)) && Ledger.CanReset(resetState,saturday.AddDays(2)),"Weekly allowance renews Monday, not Sunday");
        Check(Ledger.BudgetUsed(resetState,saturday.AddDays(1))==Ledger.Total(resetState,saturday.AddDays(1)),"Reset baseline applies only to its calendar day");
        string resetFile=Path.Combine(folder,"reset-test.xml");Ball.Persist(resetFile,resetState,saturday);
        var restoredReset=Ball.ReadStore(resetFile);
        Check(!Ledger.CanReset(restoredReset,saturday) && Ledger.BudgetUsed(restoredReset,saturday)==1200,"Restart preserves weekly use and today's reset baseline");
        Ledger.Prune(restoredReset,saturday.AddDays(10));Check(!Ledger.CanReset(restoredReset,saturday.AddDays(-7)),"Pruning history and clock rollback cannot restore consumed older allowance");
        File.Delete(resetFile);File.Delete(resetFile+".bak");
        string diagnosticsPath=Path.Combine(folder,"test-diagnostics.log");
        DiagnosticsLog.Configure(diagnosticsPath);
        DiagnosticsLog.Write("test",new InvalidOperationException("diagnostic marker"));
        string diagnosticsText=File.ReadAllText(diagnosticsPath);
        Check(diagnosticsText.Contains("diagnostic marker") && diagnosticsText.Contains("privateMB=") && diagnosticsText.Contains("GDI=") && diagnosticsText.Contains("availablePhysicalMB="),"Diagnostic exception and resource snapshot");
        File.WriteAllText(diagnosticsPath,new string('x',65536));DiagnosticsLog.Write("bounded log");
        Check(new FileInfo(diagnosticsPath).Length<=65536 && File.ReadAllText(diagnosticsPath).Contains("bounded log"),"Diagnostic file bounded to 64 KB");
        DiagnosticsLog.Configure(null);File.Delete(diagnosticsPath);
        DateTime start=new DateTime(2026,10,22,23,59,30);
        Ledger.Add(s,start,start.AddSeconds(90),new [] {"A","B"});
        Check(Math.Abs(Ledger.Total(s,start)-30)<0.001,"Midnight previous date");
        Check(Math.Abs(Ledger.Total(s,start.AddMinutes(1))-60)<0.001,"Midnight next date");
        Check(s.Records.Count==4 && s.Records[0].Seconds==15,"Concurrent split");
        Ledger.Add(s,start,start.AddSeconds(-1),new [] {"A"}); Check(s.Records.Count==4,"Clock backwards");
        Check(Ledger.AlertSlot(3599)==-1 && Ledger.AlertSlot(3600)==60,"60 minute reminder boundary");
        Check(Ledger.AlertSlot(4799)==60 && Ledger.AlertSlot(4800)==80,"80 minute reminder boundary");
        Check(Ledger.AlertSlot(5399)==80 && Ledger.AlertSlot(5400)==90,"90 minute quota boundary");
        Check(!Quota.NeedsWarning(6299.99,true,false) && Quota.NeedsWarning(6300,true,false),"Strong warning starts at exactly 105 minutes");
        Check(Quota.NeedsWarning(0,true,true) && !Quota.NeedsWarning(10000,false,true),"Warning latches over midnight and releases only without games");
        Check(Quota.NeedsWarning(7000,true,false) && !Quota.NeedsWarning(7000,false,false),"Restarting a game above threshold retriggers warning");
        Check(Quota.WarningVolume(0)==30 && Quota.WarningVolume(67)==67,"Strong warning uses audible default when normal reminders muted");
        using(var warning=new ForcedWarning(new [] {"测试游戏"},delegate {})) {
            Check(warning.BlocksUserClose() && !warning.ControlBox && !warning.MinimizeBox,"Strong warning rejects close button and Alt+F4 closure");
            warning.ShowInTaskbar=false;warning.StartPosition=FormStartPosition.Manual;warning.Location=new Point(-30000,-30000);warning.Show();Application.DoEvents();
            warning.Close();Check(!warning.IsDisposed,"Closing warning cannot dismiss it before games exit");
            using(var preview=new Bitmap(warning.Width,warning.Height)) {warning.DrawToBitmap(preview,new Rectangle(Point.Empty,warning.Size));preview.Save(Path.Combine(folder,"warning-preview.png"));}
            warning.ReleaseAfterGamesExit();Check(warning.IsDisposed,"Warning closes after verified game exit");
        }
        using(var child=Process.Start(new ProcessStartInfo(Application.ExecutablePath,"--process-fixture") {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden})) {
            try {
                var tracking=new Detector();tracking.RunningProcesses[child.Id]="Process lifetime test";
                Check(tracking.Active(new Store()).Contains("Process lifetime test"),"Recognized process remains active without a game window");
                child.Kill();child.WaitForExit(5000);
                Check(!tracking.Active(new Store()).Contains("Process lifetime test") && tracking.RunningProcesses.Count==0,"Verified process exit clears tracking");
            } finally {if(!child.HasExited) {child.Kill();child.WaitForExit(5000);}}
        }
        Check(Detector.Field("\"name\" \"Test Game\"", "name")=="Test Game","Manifest parser");
        string testPath=Path.Combine(folder,"test-history.xml");
        using(var stream=File.Create(testPath)) new XmlSerializer(typeof(Store)).Serialize(stream,s);
        Check(Ball.ReadStore(testPath).Records.Count==4,"Persistence roundtrip"); File.Delete(testPath);
        var retention = new Store();
        DateTime today = DateTime.Now.Date;
        for(int i=-1;i<=8;i++) {
            retention.Records.Add(new Record {Day=Ledger.Day(today.AddDays(-i)),Game="Retention",Seconds=60});
            retention.Alerts.Add(Ledger.Day(today.AddDays(-i))+":90");
        }
        retention.Roots.Add("Keep configuration");
        using(var stream=File.Create(testPath)) new XmlSerializer(typeof(Store)).Serialize(stream,retention);
        Ball.Persist(testPath,retention,today);
        Check(retention.Records.Count==7 && retention.Alerts.Count==7,"Keep exactly today and preceding six dates");
        Check(retention.Records.Any(r=>r.Day==Ledger.Day(today.AddDays(-6))) && !retention.Records.Any(r=>r.Day==Ledger.Day(today.AddDays(-7))),"Retention cutoff inclusive");
        Check(Ball.ReadStore(testPath).Records.Count==7 && Ball.ReadStore(testPath+".bak").Records.Count==7,"Primary and backup prune on first save");
        Check(Ball.ReadStore(testPath).Roots.Count==1,"Configuration preserved");
        Ball.Persist(testPath,retention,today.AddDays(1));
        Check(Ball.ReadStore(testPath).Records.Count==6 && Ball.ReadStore(testPath+".bak").Records.Count==6,"Rollover prunes primary and backup");
        File.Delete(testPath); File.Delete(testPath+".bak");
        var detector=new Detector(); detector.Refresh(new Store()); var detected=detector.Active(new Store());
        Check(detected!=null,"Detector smoke test");
        Check(Ball.BallSize==116,"Double original radius");
        Check(new Store().Volume==30,"Existing settings default to previous volume");
        byte[] wave=Ball.CreateReminderWave();
        Check(System.Text.Encoding.ASCII.GetString(wave,0,4)=="RIFF" && BitConverter.ToInt32(wave,40)==wave.Length-44,"Valid generated WAV header");
        int pulse=9261, gap=22050, period=pulse+gap;
        Check((wave.Length-44)/2==6*pulse+5*gap,"Six pulses and five one-second pauses");
        Check(Ball.CreateReminderWave(30,1).Length==44+2*pulse,"Slider preview produces exactly one short pulse");
        bool pulsesPresent=true, gapsSilent=true; int peak=0;
        for(int p=0;p<6;p++) {
            bool audible=false;
            for(int j=0;j<pulse;j++) {
                int sample=Math.Abs((int)BitConverter.ToInt16(wave,44+2*(p*period+j)));
                peak=Math.Max(peak,sample); if(sample>0) audible=true;
            }
            pulsesPresent &= audible;
            if(p<5) for(int j=0;j<gap;j++) if(BitConverter.ToInt16(wave,44+2*(p*period+pulse+j))!=0) gapsSilent=false;
        }
        Check(pulsesPresent && gapsSilent && peak>=900 && peak<=983,"Six audible pulses, exact silent gaps, moderately raised amplitude");
        byte[] muted=Ball.CreateReminderWave(0), low=Ball.CreateReminderWave(10), high=Ball.CreateReminderWave(100);
        int lowPeak=0, highPeak=0; bool silent=true;
        for(int i=44;i<muted.Length;i+=2) {
            if(BitConverter.ToInt16(muted,i)!=0) silent=false;
            lowPeak=Math.Max(lowPeak,Math.Abs((int)BitConverter.ToInt16(low,i)));
            highPeak=Math.Max(highPeak,Math.Abs((int)BitConverter.ToInt16(high,i)));
        }
        Check(silent && lowPeak<peak && highPeak>peak && highPeak<=3276,"Mute, volume scaling and maximum amplitude");
        retention.Volume=67; Ball.Persist(testPath,retention,today);
        Check(Ball.ReadStore(testPath).Volume==67,"Volume saved and restored");
        File.Delete(testPath); File.Delete(testPath+".bak");
        Check(Ball.IsQuickClick(false,100,new Point(0,0),new Point(1,1)),"Short stationary click opens details");
        Check(!Ball.IsQuickClick(true,100,new Point(0,0),new Point(0,0)),"Drag returning to origin stays drag");
        Check(!Ball.IsQuickClick(false,500,new Point(0,0),new Point(0,0)),"Long press never opens details");
        Check(!Ball.IsQuickClick(false,100,new Point(0,0),new Point(8,0)),"Release displacement catches missed mouse move");
        using(var ball=new Ball(Path.Combine(folder,"unused.xml"),true)) {
            ball.CreateControl();
            Check(ball.OverlayStylesReady,"Layered, topmost, non-activating tool-window styles");
            using(var boundaryImage=new Bitmap(Ball.BallSize,Ball.BallSize)) {
                bool boundarySafe=true;
                foreach(double remaining in new double[]{5400,60,20,1,0.1,0.00001,0,-1}) {
                    ball.Data.Records.Clear();ball.Data.Records.Add(new Record {Day=Ledger.Day(DateTime.Now),Game="Boundary",Seconds=Quota.LimitSeconds-remaining});
                    ball.DrawToBitmap(boundaryImage,new Rectangle(Point.Empty,boundaryImage.Size));boundarySafe &= ball.LastPaintSucceeded;
                }
                Check(boundarySafe && ball.PaintFailureCount==0,"Rendering at full, nearly empty, empty and overtime limits");
                PaintEventHandler injected=delegate {throw new OutOfMemoryException("Injected drawing failure");};
                ball.Paint+=injected;
                try {ball.DrawToBitmap(boundaryImage,new Rectangle(Point.Empty,boundaryImage.Size));}
                finally {ball.Paint-=injected;}
                Check(ball.PaintFailureCount==1 && !ball.LastPaintSucceeded,"Drawing exception contained before WinForms red-X state");
                ball.DrawToBitmap(boundaryImage,new Rectangle(Point.Empty,boundaryImage.Size));
                Check(ball.LastPaintSucceeded && ball.PaintFailureCount==1,"Normal rendering resumes after drawing exception");
                ball.Data.Records.Clear();
            }
            Check(ball.CheckVolumeControl(),"Volume slider updates live settings, persists and mutes");
            Check(ball.CheckCharts(folder),"Grouped game columns, daily total line, seven dates and zero-day values");
            Check(ball.CheckWarningLifecycle(),"Warning lifecycle: forced open, blocked close/exit, midnight latch, confirmed release, retrigger");
            Check(ball.RenderDetailsPreview(Path.Combine(folder,"details-preview.png")),"Default and wide detail windows keep action buttons fully visible");
            File.Delete(Path.Combine(folder,"unused.xml")); File.Delete(Path.Combine(folder,"unused.xml.bak"));
            using(var image=new Bitmap(392,132)) {
                using(var g=Graphics.FromImage(image)) g.Clear(Color.FromArgb(230,233,239));
                double[] used={0,3900,5700};
                for(int i=0;i<3;i++) {
                    ball.Data.Records.Clear(); ball.Data.Records.Add(new Record {Day=Ledger.Day(DateTime.Now),Game="Preview",Seconds=used[i]});
                    using(var tile=new Bitmap(Ball.BallSize,Ball.BallSize)) { ball.DrawToBitmap(tile,new Rectangle(0,0,Ball.BallSize,Ball.BallSize)); using(var g=Graphics.FromImage(image)) g.DrawImage(tile,8+i*130,8); }
                }
                image.Save(Path.Combine(folder,"preview.png"));
            }
        }
        File.WriteAllText(Path.Combine(folder,"test-results.txt"),"PASS: "+count+" checks\r\nInstalled Steam apps: "+detector.Games.Count+"\r\n"+detector.Status+"\r\n"+DateTime.Now.ToString("s"));
    }
}




