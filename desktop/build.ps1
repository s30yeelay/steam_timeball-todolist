$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
& $compiler /nologo /target:winexe /platform:x64 /optimize+ "/out:$PSScriptRoot\SteamTimeBall.exe" /reference:System.Windows.Forms.dll /reference:System.Windows.Forms.DataVisualization.dll /reference:System.Drawing.dll /reference:System.Core.dll /reference:System.Xml.dll /reference:System.Web.Extensions.dll /reference:System.Security.dll "/reference:$PSScriptRoot\Microsoft.Web.WebView2.Core.dll" "/reference:$PSScriptRoot\Microsoft.Web.WebView2.WinForms.dll" "$PSScriptRoot\SteamTimeBall.cs" "$PSScriptRoot\WorkbenchWindow.cs"
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
