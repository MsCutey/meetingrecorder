[CmdletBinding()]
param(
    [string] $Version = "1.0.0",
    [string] $DotNet = "dotnet",
    [string] $MakeNsis = "makensis",
    [switch] $SkipInstaller
)

$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "artifacts"))
if (-not $artifactsRoot.StartsWith($repositoryRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "发布目录必须位于仓库内部。"
}
if (Test-Path -LiteralPath $artifactsRoot) {
    Remove-Item -LiteralPath $artifactsRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $artifactsRoot | Out-Null

$solution = Join-Path $repositoryRoot "MeetingRecorder.sln"
$project = Join-Path $repositoryRoot "src\MeetingRecorder.App\MeetingRecorder.App.csproj"
$tests = Join-Path $repositoryRoot "tests\MeetingRecorder.Tests\MeetingRecorder.Tests.csproj"
$nuget = Join-Path $repositoryRoot "NuGet.config"
$installedDirectory = Join-Path $artifactsRoot "MeetingRecorder-win-x64"
$portableDirectory = Join-Path $artifactsRoot "MeetingRecorder-portable-win-x64"
$portableZip = Join-Path $artifactsRoot "MeetingRecorder-$Version-portable-win-x64.zip"
$installerOutput = Join-Path $artifactsRoot "MeetingRecorder-$Version-Setup-win-x64.exe"
$vcRedist = Join-Path $repositoryRoot "installer\prerequisites\vc_redist.x64.exe"

& $DotNet restore $solution -p:Platform=x64 --configfile $nuget
if ($LASTEXITCODE -ne 0) { throw "NuGet 还原失败。" }
& $DotNet restore $project -r win-x64 -p:Platform=x64 --configfile $nuget
if ($LASTEXITCODE -ne 0) { throw "win-x64 运行时包还原失败。" }
& $DotNet test $tests -c Release -p:Platform=x64 --no-restore
if ($LASTEXITCODE -ne 0) { throw "测试失败。" }
& $DotNet publish $project -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:Version=$Version -p:PublishSingleFile=false --no-restore -o $installedDirectory
if ($LASTEXITCODE -ne 0) { throw "自包含发布失败。" }

Copy-Item -LiteralPath (Join-Path $repositoryRoot "README.md") -Destination $installedDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot "THIRD-PARTY-NOTICES.txt") -Destination $installedDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot "docs") -Destination (Join-Path $installedDirectory "docs") -Recurse
Copy-Item -LiteralPath $installedDirectory -Destination $portableDirectory -Recurse
New-Item -ItemType File -Path (Join-Path $portableDirectory "portable.marker") | Out-Null
if (Test-Path -LiteralPath $vcRedist) {
    $prerequisites = Join-Path $portableDirectory "prerequisites"
    New-Item -ItemType Directory -Path $prerequisites | Out-Null
    Copy-Item -LiteralPath $vcRedist -Destination $prerequisites
}
else {
    Write-Warning "未找到 vc_redist.x64.exe；便携包不会包含 VC++ 运行库安装程序。"
}
Compress-Archive -Path (Join-Path $portableDirectory "*") -DestinationPath $portableZip -CompressionLevel Optimal

if (-not $SkipInstaller) {
    $makeNsisCommand = Get-Command $MakeNsis -ErrorAction SilentlyContinue
    if ($null -eq $makeNsisCommand) {
        Write-Warning "未找到 makensis，已跳过安装包；安装 NSIS 3 后重新运行即可。"
    }
    elseif (-not (Test-Path -LiteralPath $vcRedist)) {
        Write-Warning "缺少 VC++ 运行库，已跳过离线安装包。"
    }
    else {
        $script = Join-Path $repositoryRoot "installer\MeetingRecorder.nsi"
        & $makeNsisCommand.Source "/INPUTCHARSET" "UTF8" "/DVERSION=$Version" "/DAPP_FILES=$installedDirectory" "/DVC_REDIST=$vcRedist" "/DOUTPUT_FILE=$installerOutput" $script
        if ($LASTEXITCODE -ne 0) { throw "NSIS 安装包生成失败。" }
    }
}

$hashTargets = @(Get-ChildItem -LiteralPath $artifactsRoot -File | Where-Object Extension -in ".zip", ".exe")
$hashLines = foreach ($file in $hashTargets) {
    $hash = Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256
    "$($hash.Hash.ToLowerInvariant())  $($file.Name)"
}
[IO.File]::WriteAllLines((Join-Path $artifactsRoot "SHA256SUMS.txt"), $hashLines, [Text.UTF8Encoding]::new($false))
Write-Host "发布完成：$artifactsRoot" -ForegroundColor Green
