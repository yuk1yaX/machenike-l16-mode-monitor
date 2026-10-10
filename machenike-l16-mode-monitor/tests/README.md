# 硬件测试

这是依赖已验证 L16 机型及原厂驱动的集成测试，不能在普通云端 CI 环境完成。它会读取真实 EC 数据并使用临时窗口检查刷新、隐藏、恢复和读取进程生命周期，不设置模式或风扇参数。

在仓库根目录执行：

```powershell
New-Item -ItemType Directory -Path .\artifacts -Force | Out-Null
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /main:L16ModeMonitor.LifecycleTest /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /out:artifacts\LifecycleTest.exe src\ModeMonitor.cs tests\LifecycleTest.cs
Start-Process -FilePath .\artifacts\LifecycleTest.exe -ArgumentList 'lifecycle-report.txt' -Wait
Get-Content .\lifecycle-report.txt
```

成功报告末尾为 `PASS`。超时测试使用测试子进程模拟等待，不让真实驱动卡死。报告可能包含进程编号和即时内存采样，仅用于本地诊断。
