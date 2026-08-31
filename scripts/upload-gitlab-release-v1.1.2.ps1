[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$gitlabBase = 'https://git.tsinghua.edu.cn'
$projectPath = 'bbc26/meetingrecorder'
$releaseTag = 'v1.1.2'
$packageName = 'meetingrecorder-release'
$releaseVersion = '1.1.2'
$artifactDirectory = Join-Path $PSScriptRoot '..\artifacts'
$artifactPaths = @(
    (Join-Path $artifactDirectory 'MeetingRecorder-1.1.2-portable-win-x64.zip'),
    (Join-Path $artifactDirectory 'MeetingRecorder-1.1.2-Setup-win-x64.exe')
)

function Get-ApiErrorSummary {
    param([System.Management.Automation.ErrorRecord]$ErrorRecord)

    $status = $null
    if ($null -ne $ErrorRecord.Exception.Response) {
        try { $status = [int]$ErrorRecord.Exception.Response.StatusCode } catch { }
    }
    $detail = $ErrorRecord.ErrorDetails.Message
    if ([string]::IsNullOrWhiteSpace($detail)) {
        $detail = $ErrorRecord.Exception.Message
    }
    if ($null -ne $status) {
        return "HTTP $status - $detail"
    }
    return $detail
}

foreach ($artifactPath in $artifactPaths) {
    if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) {
        throw "找不到附件：$artifactPath"
    }
}

Write-Host '准备上传以下附件：' -ForegroundColor Cyan
foreach ($artifactPath in $artifactPaths) {
    $artifact = Get-Item -LiteralPath $artifactPath
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $artifactPath).Hash
    Write-Host ("  {0}  {1:N1} MiB" -f $artifact.Name, ($artifact.Length / 1MB))
    Write-Host ("    SHA256: {0}" -f $hash)
}

$secureToken = Read-Host '请输入刚创建的 GitLab Token（输入不会显示）' -AsSecureString
$tokenPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureToken)
$plainToken = $null

try {
    $plainToken = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($tokenPointer)
    if ([string]::IsNullOrWhiteSpace($plainToken)) {
        throw 'Token 为空。'
    }

    $headers = @{ 'PRIVATE-TOKEN' = $plainToken }
    $encodedProjectPath = [Uri]::EscapeDataString($projectPath)
    $apiBase = "$gitlabBase/api/v4"

    Write-Host '正在验证 Token 和项目权限……' -ForegroundColor Cyan
    $project = Invoke-RestMethod -Method Get -Uri "$apiBase/projects/$encodedProjectPath" -Headers $headers
    $projectId = [int]$project.id
    $encodedTag = [Uri]::EscapeDataString($releaseTag)
    $releaseApi = "$apiBase/projects/$projectId/releases/$encodedTag"
    $release = Invoke-RestMethod -Method Get -Uri $releaseApi -Headers $headers
    if ($release.tag_name -ne $releaseTag) {
        throw "未找到 Release：$releaseTag"
    }

    $linksApi = "$releaseApi/assets/links"
    $existingLinks = @(Invoke-RestMethod -Method Get -Uri $linksApi -Headers $headers)
    $packageVersion = "$releaseVersion-r$(Get-Date -Format 'yyyyMMddHHmmss')"

    foreach ($artifactPath in $artifactPaths) {
        $artifact = Get-Item -LiteralPath $artifactPath
        $encodedPackageName = [Uri]::EscapeDataString($packageName)
        $encodedPackageVersion = [Uri]::EscapeDataString($packageVersion)
        $encodedFileName = [Uri]::EscapeDataString($artifact.Name)
        $packagePath = "/api/v4/projects/$projectId/packages/generic/$encodedPackageName/$encodedPackageVersion/$encodedFileName"
        $packageUrl = "$gitlabBase$packagePath"

        Write-Host ("正在上传 {0}……" -f $artifact.Name) -ForegroundColor Cyan
        try {
            Invoke-RestMethod -Method Put -Uri $packageUrl -Headers $headers -InFile $artifact.FullName -ContentType 'application/octet-stream' | Out-Null
        }
        catch {
            $packageError = Get-ApiErrorSummary -ErrorRecord $_
            Write-Host "软件包上传不可用，改用项目附件上传：$packageError" -ForegroundColor Yellow
            $upload = Invoke-RestMethod -Method Post -Uri "$apiBase/projects/$projectId/uploads" -Headers $headers -Form @{ file = $artifact }
            if (-not [string]::IsNullOrWhiteSpace($upload.full_path)) {
                $packageUrl = "$gitlabBase$($upload.full_path)"
            }
            elseif (-not [string]::IsNullOrWhiteSpace($upload.url)) {
                $packageUrl = "$gitlabBase/$projectPath$($upload.url)"
            }
            else {
                throw "GitLab 未返回附件 URL：$($artifact.Name)"
            }
        }

        $directAssetPath = "/$($artifact.Name)"
        $payload = @{
            name = $artifact.Name
            url = $packageUrl
            direct_asset_path = $directAssetPath
            link_type = 'package'
        } | ConvertTo-Json

        $sameNameLink = @($existingLinks | Where-Object { $_.name -eq $artifact.Name }) | Select-Object -First 1
        if ($null -ne $sameNameLink) {
            Write-Host ("正在更新 Release 中的同名附件链接：{0}" -f $artifact.Name)
            $linkResult = Invoke-RestMethod -Method Put -Uri "$linksApi/$($sameNameLink.id)" -Headers $headers -ContentType 'application/json' -Body $payload
        }
        else {
            Write-Host ("正在添加 Release 附件链接：{0}" -f $artifact.Name)
            $linkResult = Invoke-RestMethod -Method Post -Uri $linksApi -Headers $headers -ContentType 'application/json' -Body $payload
        }

        Write-Host ("已关联：{0}" -f $linkResult.direct_asset_url) -ForegroundColor Green
    }

    Write-Host '正在核验 Release 附件列表……' -ForegroundColor Cyan
    $verifiedLinks = @(Invoke-RestMethod -Method Get -Uri $linksApi -Headers $headers)
    foreach ($artifactPath in $artifactPaths) {
        $artifactName = Split-Path -Leaf $artifactPath
        $verified = @($verifiedLinks | Where-Object { $_.name -eq $artifactName }) | Select-Object -First 1
        if ($null -eq $verified) {
            throw "核验失败，Release 中未找到：$artifactName"
        }
        Write-Host ("  OK  {0}" -f $verified.name) -ForegroundColor Green
        Write-Host ("      {0}" -f $verified.direct_asset_url)
    }

    Write-Host 'UPLOAD_COMPLETE' -ForegroundColor Green
    Write-Host "$gitlabBase/$projectPath/-/releases/$releaseTag"
}
catch {
    Write-Host ("上传失败：{0}" -f (Get-ApiErrorSummary -ErrorRecord $_)) -ForegroundColor Red
    exit 1
}
finally {
    $plainToken = $null
    $headers = $null
    if ($tokenPointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($tokenPointer)
    }
    $secureToken.Dispose()
}
