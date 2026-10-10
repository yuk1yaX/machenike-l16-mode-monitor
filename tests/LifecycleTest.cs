using System;
using System.IO;
using System.Text;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
namespace L16ModeMonitor {
 internal class LifecycleTest {
  static string reportPath;
  [STAThread] static void Main(string[] args) {
   if(args.Length>0&&args[0]=="--ec-worker") {
    if(Environment.GetEnvironmentVariable("L16_TEST_STALL")=="1"){Thread.Sleep(5000);return;}
    Program.Main(args);return;
   }
   reportPath=args[0];Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
   using(var form=new MonitorForm(true){Opacity=0,ShowInTaskbar=false}) {
    form.Shown+=async delegate {
     var report=new StringBuilder();
     try {
      await Until(()=>form.QueryCount>=8&&form.CurrentSnapshot.Ok,12000);
      int visiblePid=form.WorkerPid;Assert(visiblePid>0&&form.WorkerStarts==1,"one worker for eight visible samples");
      var parent=Process.GetCurrentProcess();parent.Refresh();var worker=Process.GetProcessById(visiblePid);worker.Refresh();
      report.AppendLine("visible: samples="+form.QueryCount+", workerStarts="+form.WorkerStarts+", workerPid="+visiblePid);
      report.AppendLine("parent workingSetMiB="+(parent.WorkingSet64/1048576.0).ToString("F2")+", privateBytesMiB="+(parent.PrivateMemorySize64/1048576.0).ToString("F2"));
      report.AppendLine("worker workingSetMiB="+(worker.WorkingSet64/1048576.0).ToString("F2")+", privateBytesMiB="+(worker.PrivateMemorySize64/1048576.0).ToString("F2"));worker.Dispose();
      form.WindowState=FormWindowState.Minimized;
      await Until(()=>form.WorkerPid==0&&!form.Monitoring,3500);
      int stoppedCount=form.QueryCount;await Task.Delay(3100);
      Assert(form.QueryCount==stoppedCount&&form.WorkerPid==0,"no samples or worker while hidden");
      report.AppendLine("hidden: no worker; query count stayed "+stoppedCount+" for 3.1 seconds");
      var restoreClock=Stopwatch.StartNew();form.Show();form.WindowState=FormWindowState.Normal;
      await Until(()=>form.QueryCount>stoppedCount&&form.CurrentSnapshot.Ok,2500);restoreClock.Stop();
      Assert(form.WorkerStarts==2&&form.WorkerPid!=visiblePid,"worker starts once on restore");
      report.AppendLine("restored: immediate valid sample in "+restoreClock.ElapsedMilliseconds+" ms; workerStarts="+form.WorkerStarts);
      int beforeRapid=form.QueryCount;form.WindowState=FormWindowState.Minimized;form.Show();form.WindowState=FormWindowState.Normal;
      await Until(()=>form.QueryCount>beforeRapid&&form.CurrentSnapshot.Ok,3500);
      report.AppendLine("rapid minimize/restore: recovered valid sample; workerStarts="+form.WorkerStarts);
      form.Hide();await Until(()=>form.WorkerPid==0,3500);
      await Task.Run(()=>CheckBusyAndTimeout(report));
      report.AppendLine("PASS");
     }catch(Exception e){report.AppendLine("FAIL: "+e);Environment.ExitCode=1;}
     File.WriteAllText(reportPath,report.ToString());form.Close();
    };
    Application.Run(form);
   }
   UiFonts.DisposeAll();
  }
  static async Task Until(Func<bool> condition,int timeout){var clock=Stopwatch.StartNew();while(!condition()){if(clock.ElapsedMilliseconds>timeout)throw new Exception("Timed out waiting for lifecycle condition");await Task.Delay(40);}}
  static void Assert(bool good,string message){if(!good)throw new Exception(message);}
  static void CheckBusyAndTimeout(StringBuilder report) {
   using(var reader=new ReaderProcess()) {
    using(var gate=new Mutex(false,@"Global\IO_Mutex")) {
     bool held=false;try{try{held=gate.WaitOne(1000);}catch(AbandonedMutexException){held=true;}Assert(held,"test owns EC mutex");
      Snapshot busy=reader.Sample(true);Assert(!busy.Ok&&busy.Mode==null&&busy.Error.Contains("EC 正忙"),"busy response is unknown with intact Chinese error");
     }finally{if(held)gate.ReleaseMutex();}
    }
    int pid=reader.Pid;Snapshot recovery=reader.Sample(true);Assert(recovery.Ok&&reader.Pid==pid&&reader.Starts==1,"busy recovery reuses same worker");
    report.AppendLine("busy/recovery: unknown then valid; same worker; UTF-8 error preserved");
   }
   Environment.SetEnvironmentVariable("L16_TEST_STALL","1");
   try{using(var reader=new ReaderProcess()) {
    var clock=Stopwatch.StartNew();var timedOut=reader.Sample(true);clock.Stop();
    Assert(!timedOut.Ok&&timedOut.Mode==null&&reader.Faulted&&timedOut.Error.Contains("超时"),"timeout marks unknown and stops session");
    var next=reader.Sample(true);Assert(!next.Ok&&reader.Starts==1&&reader.Pid==0,"timeout does not spawn a retry loop");
    report.AppendLine("timeout: "+clock.ElapsedMilliseconds+" ms; worker stopped; no repeated launches");
   }}finally{Environment.SetEnvironmentVariable("L16_TEST_STALL",null);}
  }
 }
}
