using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using System.Collections.Generic;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

public class WorkbenchWindow:Form {
    readonly WebView2 browser=new WebView2();
    readonly Label status=new Label();
    readonly Button connect=new Button();
    readonly string site;
    readonly Action<string> pair;
    string pairingNonce;
    bool initialized;
    readonly bool smoke;
    public bool BrowserSmokeOk {get;private set;}
    public string BrowserDiagnostic {get{return status.Text;}}
    public void UpdateSyncStatus(string text) {if(!IsDisposed)status.Text=text;}
    protected override bool ShowWithoutActivation {get{return smoke;}}
    public static bool TrustedPage(string source,string expected) {
        Uri actual,target;return Uri.TryCreate(source,UriKind.Absolute,out actual)&&Uri.TryCreate(expected,UriKind.Absolute,out target)&&actual.Scheme=="https"&&actual.GetLeftPart(UriPartial.Authority)==target.GetLeftPart(UriPartial.Authority);
    }
    public WorkbenchWindow(string url,Action statistics,Action<string> savePair,bool testMode=false) {
        smoke=testMode;
        site=url.TrimEnd('/');pair=savePair;
        Text="Steam 时间球 · 我的任务工作台";ClientSize=new Size(1120,780);MinimumSize=new Size(650,500);
        AutoScaleMode=AutoScaleMode.None;StartPosition=FormStartPosition.CenterScreen;BackColor=Color.FromArgb(244,245,239);
        Font=new Font("Microsoft YaHei UI",15,FontStyle.Regular,GraphicsUnit.Pixel);
        var shell=new TableLayoutPanel {Dock=DockStyle.Fill,RowCount=2,ColumnCount=1,Margin=Padding.Empty};
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute,60));shell.RowStyles.Add(new RowStyle(SizeType.Percent,100));shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        var bar=new FlowLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(10),WrapContents=false,BackColor=Color.White};
        AddButton(bar,"工作台首页",delegate {if(initialized)browser.CoreWebView2.Navigate(site);});
        AddButton(bar,"返回",delegate {if(initialized&&browser.CanGoBack)browser.GoBack();});
        AddButton(bar,"刷新",delegate {if(initialized)browser.Reload();});
        connect.Text="连接本机时间球";connect.AutoSize=true;connect.Height=37;connect.Enabled=false;connect.Click+=Connect;bar.Controls.Add(connect);
        AddButton(bar,"游戏统计与设置",statistics);
        status.AutoSize=true;status.Margin=new Padding(12,8,0,0);status.Text="正在启动网页组件…";status.ForeColor=Color.DimGray;bar.Controls.Add(status);
        browser.Dock=DockStyle.Fill;shell.Controls.Add(bar,0,0);shell.Controls.Add(browser,0,1);Controls.Add(shell);
        Shown+=StartBrowser;
        FormClosed+=delegate {browser.Dispose();Font.Dispose();};
    }
    static void AddButton(Control root,string text,Action action) {var button=new Button {Text=text,AutoSize=true,Height=37,Margin=new Padding(0,0,8,0)};button.Click+=delegate {action();};root.Controls.Add(button);}
    async void StartBrowser(object sender,EventArgs args) {
        try {
            string profile=smoke?Path.Combine(Path.GetTempPath(),"SteamTimeBall-WebView-Smoke"):Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"data","WebView2");
            var environment=await CoreWebView2Environment.CreateAsync(null,profile,null);
            if(IsDisposed)return;
            await browser.EnsureCoreWebView2Async(environment);
            if(IsDisposed)return;
            initialized=true;
            browser.CoreWebView2.Settings.AreDevToolsEnabled=false;
            browser.CoreWebView2.NavigationStarting+=delegate(object s,CoreWebView2NavigationStartingEventArgs e) {
                if(smoke)return;
                Uri uri;if(!Uri.TryCreate(e.Uri,UriKind.Absolute,out uri)||uri.Scheme!="https") {e.Cancel=true;return;}
                status.Text="正在加载…";
            };
            browser.CoreWebView2.NavigationCompleted+=delegate(object s,CoreWebView2NavigationCompletedEventArgs e) {
                bool trusted=TrustedPage(browser.Source==null?"":browser.Source.AbsoluteUri,site);connect.Enabled=trusted;
                status.Text=e.IsSuccess?(trusted?"私人工作台 · 登录后使用":"正在登录账号"):("加载失败："+e.WebErrorStatus);
            };
            browser.CoreWebView2.NewWindowRequested+=delegate(object s,CoreWebView2NewWindowRequestedEventArgs e) {
                e.Handled=true;Uri uri;if(Uri.TryCreate(e.Uri,UriKind.Absolute,out uri)&&uri.Scheme=="https")browser.CoreWebView2.Navigate(uri.AbsoluteUri);
            };
            browser.CoreWebView2.WebMessageReceived+=MessageReceived;
            browser.CoreWebView2.ProcessFailed+=delegate(object s,CoreWebView2ProcessFailedEventArgs e) {status.Text="网页组件暂时中断，请点刷新或重开窗口";DiagnosticsLog.Write("WebView process failed: "+e.ProcessFailedKind);};
            if(smoke){browser.CoreWebView2.DocumentTitleChanged+=delegate {BrowserSmokeOk=browser.CoreWebView2.DocumentTitle=="SteamTimeBall smoke ready";};browser.CoreWebView2.NavigateToString("<html><head><title>SteamTimeBall smoke ready</title></head><body>Embedded workbench runtime ready.</body></html>");}
            else browser.CoreWebView2.Navigate(site);
        } catch(Exception error) {
            if(IsDisposed)return;status.Text="网页组件启动失败："+error.GetType().Name+" "+error.Message;DiagnosticsLog.Write("embedded workbench startup",error);
            var label=new Label {Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleCenter,Text="无法启动内嵌工作台。\n请确认已安装 Microsoft Edge WebView2 Runtime，或查看 data/diagnostics.log。\n游戏统计与声音提醒仍可独立使用。"};Controls.Add(label);label.BringToFront();
        }
    }
    async void Connect(object sender,EventArgs args) {
        if(!initialized||!TrustedPage(browser.Source.AbsoluteUri,site))return;
        pairingNonce=Guid.NewGuid().ToString("N");connect.Enabled=false;status.Text="正在创建专属设备连接…";
        var serializer=new JavaScriptSerializer();
        string script="(async function(){try{const r=await fetch('/api/device/create',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({name:'本机 Steam 时间球'})});if(!r.ok)throw new Error('请先登录；若设备已满，请在网站设置中撤销旧设备');const p=await r.json();window.chrome.webview.postMessage(JSON.stringify({type:'steam-pair',nonce:"+serializer.Serialize(pairingNonce)+",deviceKey:p.deviceKey}));}catch(e){window.chrome.webview.postMessage(JSON.stringify({type:'steam-pair-error',nonce:"+serializer.Serialize(pairingNonce)+",message:e.message}));}})();";
        try {await browser.CoreWebView2.ExecuteScriptAsync(script);}catch(Exception error){DiagnosticsLog.Write("embedded pairing",error);status.Text="连接未完成，请重试";connect.Enabled=true;}
    }
    void MessageReceived(object sender,CoreWebView2WebMessageReceivedEventArgs e) {
        if(!TrustedPage(e.Source,site)||pairingNonce==null)return;
        try {
            var message=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(e.TryGetWebMessageAsString());
            if(!message.ContainsKey("nonce")||Convert.ToString(message["nonce"])!=pairingNonce)return;
            if(Convert.ToString(message["type"])=="steam-pair") {pair(Convert.ToString(message["deviceKey"]));pairingNonce=null;status.Text="设备已保存 · 正在同步额度";}
            else if(Convert.ToString(message["type"])=="steam-pair-error") {status.Text="连接未完成，请先登录或检查设备数量";MessageBox.Show(this,Convert.ToString(message["message"]),"连接未完成");pairingNonce=null;}
        }catch(Exception error){DiagnosticsLog.Write("embedded pairing response",error);status.Text="配对未保存，请重试";}
        connect.Enabled=true;
    }
}

