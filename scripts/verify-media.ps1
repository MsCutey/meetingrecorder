[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $VideoFile,
    [Parameter(Mandatory = $true)] [string] $AudioFile,
    [string] $Ffprobe = "ffprobe"
)

$ErrorActionPreference = "Stop"

function Get-MediaStreams([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "媒体文件不存在：$Path"
    }
    $json = & $Ffprobe -v error -show_streams -of json -- $Path
    if ($LASTEXITCODE -ne 0) { throw "ffprobe 无法读取：$Path" }
    return ($json | ConvertFrom-Json).streams
}

$videoStreams = @(Get-MediaStreams $VideoFile)
$mp4Video = @($videoStreams | Where-Object codec_type -eq "video")
$mp4Audio = @($videoStreams | Where-Object codec_type -eq "audio")
if ($mp4Video.Count -ne 1 -or $mp4Video[0].codec_name -ne "h264") {
    throw "MP4 必须恰有一条 H.264 视频轨。"
}
if ($mp4Audio.Count -ne 1 -or $mp4Audio[0].codec_name -ne "aac") {
    throw "MP4 必须恰有一条 AAC 音轨。"
}

$audioStreams = @(Get-MediaStreams $AudioFile)
$m4aVideo = @($audioStreams | Where-Object codec_type -eq "video")
$m4aAudio = @($audioStreams | Where-Object codec_type -eq "audio")
if ($m4aVideo.Count -ne 0) { throw "M4A 不得包含视频轨。" }
if ($m4aAudio.Count -ne 1 -or $m4aAudio[0].codec_name -ne "aac") {
    throw "M4A 必须恰有一条 AAC 音轨。"
}

Write-Host "媒体轨道检查通过。" -ForegroundColor Green
