// MACHENIKE L16: read-only status monitor using the already installed OEM driver.
// Fixed query transactions below come from this computer's HEControlCenter ECManager.
// No profile-setting, fan-control, driver-installation or service-management operations.
using System;
using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace L16ModeMonitor {
 public class Snapshot {
  public int? Mode, CpuFan, GpuFan, CpuTemp, GpuTemp;
  public string Error, SampledAt;
  public bool Ok;
  static string Number(int? value){return value.HasValue?value.Value.ToString(CultureInfo.InvariantCulture):"";}
  public string ToLine(){return String.Join("\t",new string[]{Ok?"1":"0",Number(Mode),Number(CpuFan),Number(GpuFan),Number(CpuTemp),Number(GpuTemp),SampledAt??"",Convert.ToBase64String(Encoding.UTF8.GetBytes(Error??""))});}
  static int? ParseNumber(string value){return value.Length==0?(int?)null:Int32.Parse(value,CultureInfo.InvariantCulture);}
  public static Snapshot FromLine(string line){
   if(line==null)throw new IOException("读取进程已退出。");var fields=line.Split('\t');if(fields.Length!=8)throw new IOException("读取进程返回了无效数据。");
   return new Snapshot{Ok=fields[0]=="1",Mode=ParseNumber(fields[1]),CpuFan=ParseNumber(fields[2]),GpuFan=ParseNumber(fields[3]),CpuTemp=ParseNumber(fields[4]),GpuTemp=ParseNumber(fields[5]),SampledAt=fields[6],Error=fields[7].Length==0?null:Encoding.UTF8.GetString(Convert.FromBase64String(fields[7]))};
  }
  static string Quoted(string text){if(text==null)return "null";var b=new StringBuilder("\"");foreach(char c in text){if(c=='"'||c=='\\'){b.Append('\\');b.Append(c);}else if(c<32)b.Append("\\u"+((int)c).ToString("x4"));else b.Append(c);}return b.Append('"').ToString();}
  static string JsonNumber(int? value){return value.HasValue?value.Value.ToString(CultureInfo.InvariantCulture):"null";}
  public string ToJson(){return "{\"Mode\":"+JsonNumber(Mode)+",\"CpuFan\":"+JsonNumber(CpuFan)+",\"GpuFan\":"+JsonNumber(GpuFan)+",\"CpuTemp\":"+JsonNumber(CpuTemp)+",\"GpuTemp\":"+JsonNumber(GpuTemp)+",\"Error\":"+Quoted(Error)+",\"SampledAt\":"+Quoted(SampledAt)+",\"Ok\":"+(Ok?"true":"false")+"}";}
 }
 internal sealed class EcReader : IDisposable {
  const string DriverHash = "67DB6E77E353CDA23AFA8DBE9ADF1ECF117B9DE83EE74C34FE63C451D6067DE4";
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
  static extern SafeFileHandle CreateFile(string name,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
  [DllImport("kernel32.dll", SetLastError=true)]
  static extern bool DeviceIoControl(SafeFileHandle handle,uint code,byte[] input,uint inputSize,byte[] output,uint outputSize,out uint bytes,IntPtr overlapped);
  SafeFileHandle handle;
  static bool verified;
  readonly byte[] writeBuffer=new byte[4],readPort=new byte[]{0x68},readBuffer=new byte[1];
  static string Hash(string file) {using(var sha=SHA256.Create())using(var stream=File.OpenRead(file))return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","");}
  static void CheckMachine() {
   if(verified)return;
   using(var key=Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS")) {
    if(key==null || Convert.ToString(key.GetValue("SystemManufacturer"))!="MACHENIKE" || Convert.ToString(key.GetValue("SystemProductName"))!="L16")
     throw new InvalidOperationException("此版本只适配已验证的 MACHENIKE L16。");
   }
   string image;
   using(var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\ioportdrv")) {
    if(key==null)throw new InvalidOperationException("未找到原厂 ioportdrv 驱动。此程序不会安装驱动。");
    image=Environment.ExpandEnvironmentVariables(Convert.ToString(key.GetValue("ImagePath"))).Trim('"');
   }
   if(image.StartsWith(@"\??\"))image=image.Substring(4);
   if(!Path.IsPathRooted(image)||!File.Exists(image)||Hash(image)!=DriverHash)
    throw new InvalidOperationException("原厂驱动版本尚未验证，已停止 EC 查询。");
   verified=true;
  }
  EcReader() {
   handle=CreateFile(@"\\.\ReadAndWritePort",0xC0000000,0,IntPtr.Zero,3,0x80,IntPtr.Zero);
   if(handle.IsInvalid) {
    int error=Marshal.GetLastWin32Error();handle.Dispose();
    throw new InvalidOperationException(error==5?"驱动拒绝访问，请尝试以管理员身份运行。":"无法打开原厂驱动（错误 "+error+"）。");
   }
  }
  void PortQueryByte(byte port,byte value) {
   uint count;
   // OEM IOCTL: two little-endian ushort fields (port and value).
   writeBuffer[0]=port;writeBuffer[2]=value;
   if(!DeviceIoControl(handle,0x9C402008,writeBuffer,4,null,0,out count,IntPtr.Zero))
    throw new IOException("EC 查询发送失败（"+Marshal.GetLastWin32Error()+"）。");
  }
  byte ReadData() {
   uint count;
   if(!DeviceIoControl(handle,0x9C402004,readPort,1,readBuffer,1,out count,IntPtr.Zero)||count!=1)
    throw new IOException("EC 数据读取失败（"+Marshal.GetLastWin32Error()+"）。");
   return readBuffer[0];
  }
  int Query(byte group,byte index) {
   // Allow only OEM status queries. Setting a performance mode uses a different command.
   bool allowed=(group==0xde&&index==0x11)||(group==0xdd&&(index==0x20||index==0x23))||
    (group==0xd5&&(index==0x16||index==0x17||index==0x18||index==0x19));
   if(!allowed)throw new InvalidOperationException("未授权的 EC 查询。");
   PortQueryByte(0x6c,group);Thread.Sleep(10);PortQueryByte(0x68,index);Thread.Sleep(10);
   return ReadData();
  }
  int? Fan(byte lo,byte hi) {
   // Retry if the high byte changes while reading a changing 16-bit RPM value.
   for(int attempt=0;attempt<2;attempt++) {
    int first=Query(0xd5,hi),low=Query(0xd5,lo),second=Query(0xd5,hi);
    if(first==second) {int value=(first<<8)|low;return value<=12000?(int?)value:null;}
   }
   return null;
  }
  static int? Temperature(int value) {return value>=10&&value<=125?(int?)value:null;}
  public static Snapshot Capture(bool sensors=true,Snapshot previous=null) {
   var result=new Snapshot();
   try {
    CheckMachine();
    using(var gate=new Mutex(false,@"Global\IO_Mutex")) {
     bool acquired=false;
     try {
      try {acquired=gate.WaitOne(450);}catch(AbandonedMutexException){acquired=true;}
      if(!acquired)throw new IOException("EC 正忙，正在等待下一次读取。");
      using(var ec=new EcReader()) {
       int mode=ec.Query(0xde,0x11);
       if(mode<1||mode>3)throw new IOException("EC 返回未知模式值 "+mode+"，无法确认当前模式。");
       result.Mode=mode;
       if(sensors||previous==null||!previous.Ok||previous.Mode!=mode) {
        result.CpuFan=ec.Fan(0x18,0x19);result.GpuFan=ec.Fan(0x16,0x17);
        result.CpuTemp=Temperature(ec.Query(0xdd,0x20));result.GpuTemp=Temperature(ec.Query(0xdd,0x23));
       }else {result.CpuFan=previous.CpuFan;result.GpuFan=previous.GpuFan;result.CpuTemp=previous.CpuTemp;result.GpuTemp=previous.GpuTemp;}
       result.Ok=true;
      }
     }finally{if(acquired)gate.ReleaseMutex();}
    }
   }catch(Exception error){result.Mode=null;result.Error=error.Message;result.Ok=false;}
   result.SampledAt=DateTimeOffset.Now.ToString("o");return result;
  }
  public void Dispose(){if(handle!=null)handle.Dispose();}
 }
 internal static class Program {
  [STAThread] internal static void Main(string[] args) {
   if(args.Length>0&&args[0]=="--ec-worker") {RunWorker();return;}
   if(args.Length==2&&args[0]=="--probe") {using(var reader=new ReaderProcess())File.WriteAllText(args[1],reader.Sample(true).ToJson());return;}
   RunUi(args);
  }
  [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
  static void RunUi(string[] args) {
   Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
   if(args.Length==2&&args[0]=="--render") {
    using(var form=new MonitorForm(false)) {form.ShowInTaskbar=false;form.Opacity=0;using(var reader=new ReaderProcess())form.SetSnapshot(reader.Sample(true));form.Show();Application.DoEvents();
     using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height));bitmap.Save(args[1]);}}
    return;
   }
   bool first;
   using(var singleton=new Mutex(true,@"Local\MachenikeL16ModeMonitor.v1",out first)) {
    if(!first){MessageBox.Show("监测程序已在运行。请从任务栏或托盘打开。","L16 模式监测");return;}
    Application.Run(new MonitorForm(true));
   }
   UiFonts.DisposeAll();
  }
  static void RunWorker() {
   using(var input=new StreamReader(Console.OpenStandardInput(),new UTF8Encoding(false)))
   using(var output=new StreamWriter(Console.OpenStandardOutput(),new UTF8Encoding(false)){AutoFlush=true}) {
    Snapshot previous=null;string command;
    while((command=input.ReadLine())!=null) {
     if(command=="quit")break;if(command!="full"&&command!="mode")continue;
     previous=EcReader.Capture(command=="full",previous);output.WriteLine(previous.ToLine());
    }
   }
  }
 }
 // One persistent worker while visible. A timeout stops the session, avoiding a launch/retry loop.
 internal sealed class ReaderProcess : IDisposable {
  readonly object sync=new object();Process process;bool faulted,disposed;
  internal int Starts{get;private set;}internal int Samples{get;private set;}
  internal bool Faulted{get{lock(sync)return faulted;}}
  internal int Pid{get{lock(sync)return process!=null&&!process.HasExited?process.Id:0;}}
  public Snapshot Sample(bool full) {
   lock(sync) {
    if(disposed||faulted)return new Snapshot{Error="监测已停止。重新打开窗口可再试。",SampledAt=DateTimeOffset.Now.ToString("o")};
    try {
     if(process==null) {
      process=new Process();process.StartInfo=new ProcessStartInfo(typeof(Program).Assembly.Location,"--ec-worker") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,StandardOutputEncoding=Encoding.UTF8};
      process.Start();Starts++;
     }
     if(process.HasExited)throw new IOException("读取进程已退出。重新打开窗口可再试。");
     process.StandardInput.WriteLine(full?"full":"mode");process.StandardInput.Flush();
     var pending=process.StandardOutput.ReadLineAsync();
     if(!pending.Wait(2000))throw new IOException("EC 查询超时，已暂停监测。重新打开窗口可再试。");
     Snapshot snapshot=Snapshot.FromLine(pending.Result);Samples++;return snapshot;
    }catch(Exception error){faulted=true;StopLocked();return new Snapshot{Error=error.GetBaseException().Message,SampledAt=DateTimeOffset.Now.ToString("o")};}
   }
  }
  void StopLocked() {
   var current=process;process=null;if(current==null)return;
   try{if(!current.HasExited){current.StandardInput.WriteLine("quit");current.StandardInput.Flush();if(!current.WaitForExit(200))current.Kill();}}catch{try{current.Kill();}catch{}}
   finally{current.Dispose();}
  }
  public void Stop(){lock(sync){StopLocked();faulted=false;}}
  public void Dispose(){lock(sync){disposed=true;StopLocked();}}
 }
 internal static class UiFonts {
  static readonly System.Collections.Generic.Dictionary<string,Font> fonts=new System.Collections.Generic.Dictionary<string,Font>();
  internal static Font Get(float size,FontStyle style){string key=size.ToString(CultureInfo.InvariantCulture)+":"+style;Font value;if(!fonts.TryGetValue(key,out value)){value=new Font("Microsoft YaHei UI",size,style);fonts.Add(key,value);}return value;}
  internal static void DisposeAll(){foreach(var font in fonts.Values)font.Dispose();fonts.Clear();}
 }
}

namespace L16ModeMonitor {
 internal class MonitorForm : Form {
  static readonly Color Bg=Color.FromArgb(17,22,33),Panel=Color.FromArgb(27,34,47),Muted=Color.FromArgb(150,163,185),Fg=Color.FromArgb(235,241,250);
  readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
  readonly CheckBox pin=new CheckBox();
  readonly bool live;
  readonly ReaderProcess reader=new ReaderProcess();
  readonly Icon[] modeIcons=new Icon[4];
  NotifyIcon tray;
  ToolStripMenuItem pinMenu;
  Snapshot data=new Snapshot();
  bool busy;
  bool started,monitorActive,deferredRefresh,fullNext=true;
  int session,trayMode=-1;
  Task stopTask=Task.FromResult(0);
  long lastFull;
  internal int WorkerPid{get{return reader.Pid;}}
  internal int WorkerStarts{get{return reader.Starts;}}
  internal int QueryCount{get{return reader.Samples;}}
  internal bool Monitoring{get{return monitorActive;}}
  internal Snapshot CurrentSnapshot{get{return data;}}
  int? lastMode;
  Toast toast;
  string updated="等待第一次读取";
  public MonitorForm(bool startLive) {
   live=startLive;Text="机械师 L16 · 模式监测";ClientSize=new Size(570,490);
   BackColor=Bg;ForeColor=Fg;Font=new Font("Microsoft YaHei UI",10);
   AutoScaleMode=AutoScaleMode.None;FormBorderStyle=FormBorderStyle.FixedSingle;
   MaximizeBox=false;StartPosition=FormStartPosition.CenterScreen;DoubleBuffered=true;
   for(int i=0;i<modeIcons.Length;i++)modeIcons[i]=MakeIcon(i);Icon=modeIcons[0];
   pin.Text="置顶";pin.ForeColor=Muted;pin.BackColor=Bg;pin.AutoSize=true;pin.Location=new Point(482,21);
   pin.CheckedChanged+=delegate{TopMost=pin.Checked;if(pinMenu!=null)pinMenu.Checked=pin.Checked;};Controls.Add(pin);
   if(live) {
    var menu=new ContextMenuStrip();menu.Items.Add("显示窗口",null,delegate{RestoreWindow();});
    pinMenu=new ToolStripMenuItem("置顶");pinMenu.Click+=delegate{pin.Checked=!pin.Checked;};menu.Items.Add(pinMenu);
    menu.Items.Add(new ToolStripSeparator());menu.Items.Add("退出",null,delegate{Close();});
    tray=new NotifyIcon{Icon=modeIcons[0],Text="L16 模式监测 · 正在读取",ContextMenuStrip=menu,Visible=true};
    tray.DoubleClick+=delegate{RestoreWindow();};
    Resize+=delegate{if(WindowState==FormWindowState.Minimized)Hide();else if(started)UpdateMonitoringState();};
    VisibleChanged+=delegate{if(started)UpdateMonitoringState();};
    timer.Interval=1000;timer.Tick+=async delegate{await RefreshSnapshot();};
    Shown+=delegate{started=true;UpdateMonitoringState();};
   }
  }
  internal static string ModeName(int? value) {return value==1?"普通模式":value==2?"游戏模式":value==3?"狂暴模式":"未确认";}
  internal static Color ModeColor(int? value) {return value==1?Color.FromArgb(64,205,171):value==2?Color.FromArgb(73,158,255):value==3?Color.FromArgb(255,158,73):Muted;}
  internal static Icon MakeIcon(int mode) {
   using(var image=new Bitmap(32,32))using(var g=Graphics.FromImage(image)) {
    g.SmoothingMode=SmoothingMode.AntiAlias;g.Clear(Color.Transparent);
    using(var brush=new SolidBrush(ModeColor(mode)))g.FillEllipse(brush,1,1,30,30);
    using(var f=new Font("Segoe UI",16,FontStyle.Bold))using(var b=new SolidBrush(Bg)) {
     using(var sf=new StringFormat{Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center})
      g.DrawString(mode==0?"?":mode.ToString(),f,b,new RectangleF(0,0,32,32),sf);
    }
    IntPtr handle=image.GetHicon();try{using(var icon=Icon.FromHandle(handle))return (Icon)icon.Clone();}finally{DestroyIcon(handle);}
   }
  }
  [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
  async void UpdateMonitoringState() {
   bool next=live&&Visible&&WindowState!=FormWindowState.Minimized&&!IsDisposed;
   if(next==monitorActive)return;monitorActive=next;session++;timer.Stop();
   if(!next) {
    deferredRefresh=false;fullNext=true;lastMode=null;
    if(toast!=null){toast.Close();toast=null;}
    if(tray!=null){tray.Text="L16 · 已暂停监测（后台）";tray.Icon=modeIcons[0];trayMode=0;}
    stopTask=Task.Run(()=>reader.Stop());Invalidate();return;
   }
   data=new Snapshot();updated="正在刷新";lastMode=null;fullNext=true;timer.Start();Invalidate();
   if(busy)deferredRefresh=true;else await RefreshSnapshot();
  }
  async Task RefreshSnapshot() {
   if(busy||IsDisposed||!monitorActive)return;busy=true;int currentSession=session;
   try {
    await stopTask;
    if(!IsDisposed&&monitorActive&&currentSession==session) {
     bool full=fullNext||lastFull==0||(Stopwatch.GetTimestamp()-lastFull)/(double)Stopwatch.Frequency>=3;
     var snapshot=await Task.Run(()=>reader.Sample(full));
     if(!IsDisposed&&monitorActive&&currentSession==session) {
      SetSnapshot(snapshot);fullNext=!snapshot.Ok;if(full&&snapshot.Ok)lastFull=Stopwatch.GetTimestamp();
      if(reader.Faulted)timer.Stop();
     }
    }
   }finally {busy=false;}
   if(deferredRefresh&&!IsDisposed&&monitorActive){deferredRefresh=false;await RefreshSnapshot();}
  }
  public void SetSnapshot(Snapshot snapshot) {
   data=snapshot??new Snapshot{Error="没有读到数据。"};updated=DateTime.Now.ToString("HH:mm:ss");
   if(tray!=null) {
    string tooltip="L16 · "+ModeName(data.Ok?data.Mode:null)+(data.Ok?" · EC 实时读取":" · 读取失败");
    if(tray.Text!=tooltip)tray.Text=tooltip;
    int mode=data.Ok&&data.Mode.HasValue?data.Mode.Value:0;
    if(trayMode!=mode){tray.Icon=modeIcons[mode];trayMode=mode;}
   }
   if(data.Ok&&data.Mode.HasValue) {
    if(live&&lastMode.HasValue&&lastMode!=data.Mode) {
     if(toast!=null)toast.Close();toast=new Toast(data.Mode.Value);toast.Show();
    }
    lastMode=data.Mode;
   }
   Invalidate();
  }
  internal void RestoreWindow(){Show();WindowState=FormWindowState.Normal;Activate();}
  protected override void OnFormClosed(FormClosedEventArgs e) {
   monitorActive=false;session++;timer.Stop();timer.Dispose();if(toast!=null)toast.Close();
   if(tray!=null){tray.Visible=false;tray.Dispose();}
   reader.Dispose();Icon=null;foreach(var icon in modeIcons)if(icon!=null)icon.Dispose();base.OnFormClosed(e);
  }
  static void Label(Graphics g,string text,float size,FontStyle style,Color color,Rectangle rect,TextFormatFlags flags=TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis) {
   TextRenderer.DrawText(g,text,UiFonts.Get(size,style),rect,color,flags|TextFormatFlags.NoPadding);
  }
  internal static void Round(Graphics g,Rectangle r,int radius,Color fill) {
   using(var path=new GraphicsPath()) {int d=radius*2;path.AddArc(r.X,r.Y,d,d,180,90);path.AddArc(r.Right-d,r.Y,d,d,270,90);
    path.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);path.AddArc(r.X,r.Bottom-d,d,d,90,90);path.CloseFigure();using(var b=new SolidBrush(fill))g.FillPath(b,path);}
  }
  static void Metric(Graphics g,Rectangle box,string caption,int? number,string unit) {
   Round(g,box,12,Panel);Label(g,caption,10,FontStyle.Regular,Muted,new Rectangle(box.X+18,box.Y+12,box.Width-36,22));
   string value=number.HasValue?number.Value.ToString():"—";
   Label(g,value,24,FontStyle.Bold,Fg,new Rectangle(box.X+18,box.Y+39,145,37));
   Label(g,number.HasValue?unit:"暂无有效读数",9,FontStyle.Regular,Muted,new Rectangle(box.X+18,box.Y+80,box.Width-36,20));
  }
  protected override void OnPaint(PaintEventArgs e) {
   base.OnPaint(e);var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
   bool paused=live&&!monitorActive;bool ok=!paused&&data.Ok&&data.Mode.HasValue;int? mode=ok?data.Mode:null;Color accent=ModeColor(mode);
   Label(g,"MACHENIKE  /  L16",10,FontStyle.Bold,Muted,new Rectangle(26,20,370,24));
   Label(g,"当前性能模式",11,FontStyle.Regular,Muted,new Rectangle(26,68,300,24));
   Label(g,ModeName(mode),31,FontStyle.Bold,Fg,new Rectangle(24,100,350,56));
   Round(g,new Rectangle(376,104,167,37),18,Panel);
   using(var b=new SolidBrush(accent))g.FillEllipse(b,389,117,9,9);
   Label(g,paused?"已暂停监测":ok?"EC 实时读取":"等待有效读数",10,FontStyle.Regular,accent,new Rectangle(406,109,128,27));
   string[] names={"普通 / 办公","游戏","狂暴"};
   for(int i=0;i<3;i++) {
    int x=26+i*178;bool selected=mode==i+1;
    Round(g,new Rectangle(x,178,162,39),9,selected?accent:Panel);
    Label(g,names[i],10,selected?FontStyle.Bold:FontStyle.Regular,selected?Bg:Muted,new Rectangle(x,181,162,33),TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
   }
   Metric(g,new Rectangle(26,238,250,115),"CPU 风扇",ok?data.CpuFan:null,"RPM");
   Metric(g,new Rectangle(294,238,250,115),"GPU 风扇",ok?data.GpuFan:null,"RPM");
   Round(g,new Rectangle(26,368,518,49),10,Panel);
   Label(g,"CPU  "+(ok&&data.CpuTemp.HasValue?data.CpuTemp.Value+" °C":"—"),11,FontStyle.Regular,Fg,new Rectangle(44,380,216,25));
   Label(g,"GPU  "+(ok&&data.GpuTemp.HasValue?data.GpuTemp.Value+" °C":"—"),11,FontStyle.Regular,Fg,new Rectangle(312,380,216,25));
   if(ok) {
    Label(g,"模式每秒检查   ·   风扇 / 温度每三秒刷新",10,FontStyle.Regular,Muted,new Rectangle(26,429,518,23));
    Label(g,"最小化暂停监测   ·   打开窗口立即刷新",9,FontStyle.Regular,Muted,new Rectangle(26,457,355,21));
    Label(g,updated,9,FontStyle.Regular,Muted,new Rectangle(418,457,126,21),TextFormatFlags.Right|TextFormatFlags.VerticalCenter);
   }else {
    Label(g,paused?"后台已暂停监测。打开窗口后自动刷新。":data.Error??"正在读取原厂驱动…",9,FontStyle.Regular,Color.FromArgb(255,176,126),new Rectangle(26,430,518,48),TextFormatFlags.WordBreak|TextFormatFlags.Top);
   }
  }
 }
 internal sealed class Toast : Form {
  readonly int mode;
  readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
  public Toast(int newMode) {
   mode=newMode;FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;DoubleBuffered=true;
   BackColor=Color.FromArgb(20,27,40);ClientSize=new Size(300,103);StartPosition=FormStartPosition.Manual;
   var area=Screen.PrimaryScreen.WorkingArea;Location=new Point(area.Right-Width-24,area.Bottom-Height-24);
   timer.Interval=2600;timer.Tick+=delegate{Close();};Shown+=delegate{timer.Start();};
  }
  protected override bool ShowWithoutActivation {get{return true;}}
  protected override CreateParams CreateParams {get{var cp=base.CreateParams;cp.ExStyle|=0x08000000;return cp;}}
  protected override void OnPaint(PaintEventArgs e) {
   var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
   using(var b=new SolidBrush(MonitorForm.ModeColor(mode)))g.FillRectangle(b,0,0,5,Height);
   using(var small=new Font("Microsoft YaHei UI",9))using(var large=new Font("Microsoft YaHei UI",21,FontStyle.Bold)) {
    TextRenderer.DrawText(g,"性能模式已变更  ·  EC 读取确认",small,new Point(24,14),Color.FromArgb(155,171,195));
    TextRenderer.DrawText(g,MonitorForm.ModeName(mode),large,new Point(21,42),Color.FromArgb(235,241,250));
   }
  }
  protected override void OnFormClosed(FormClosedEventArgs e){timer.Stop();timer.Dispose();base.OnFormClosed(e);}
 }
}
