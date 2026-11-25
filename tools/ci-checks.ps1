$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$sourceRoot = Join-Path $root "src"
$sourceFiles = Get-ChildItem -LiteralPath $sourceRoot -Recurse -File |
    Where-Object {
        ($_.Extension -in @(".cs", ".xaml", ".csproj")) -and
        $_.FullName -notmatch '[\\/]obj[\\/]|[\\/]bin[\\/]'
    }
$testRoot = Join-Path $root "tests"
$testFiles = Get-ChildItem -LiteralPath $testRoot -Recurse -File |
    Where-Object {
        ($_.Extension -in @(".cs", ".xaml", ".csproj")) -and
        $_.FullName -notmatch '[\\/]obj[\\/]|[\\/]bin[\\/]'
    }

$bannedPatterns = [ordered]@{
    "File.Delete" = '\bFile\s*\.\s*Delete\s*\('
    "Directory.Delete" = '\bDirectory\s*\.\s*Delete\s*\('
    "File.Copy" = '\bFile\s*\.\s*Copy\s*\('
    "File.Replace" = '\bFile\s*\.\s*Replace\s*\('
    "Visual Basic file operations" = 'Microsoft\.VisualBasic\.FileIO|(?m)^\s*using\s+Microsoft\.VisualBasic\.FileIO\s*;'
    "FileSystem.DeleteFile" = '\bFileSystem\s*\.\s*DeleteFile\s*\('
    "RecycleOption deletion APIs" = '\bRecycleOption\s*\.\s*(?:SendToRecycleBin|DeletePermanently)\b'
    "Portable storage deletion API" = '\b\w+\.DeleteAsync\s*\('
    "networking APIs" = 'System\.Net(?:\.|\s|;)'
    "HttpClient" = '\bHttpClient\b'
    "Socket" = '\bSocket\b'
    "WebView" = '\bWebView\b'
    "P/Invoke" = '\b(?:DllImport|LibraryImport)\s*\('
    "UriSource image loading" = '\bUriSource\b'
}

$violations = [System.Collections.Generic.List[string]]::new()
$moveBoundary = Join-Path $sourceRoot "Purg.Mover\Mover.cs"
$portableDeleteBoundary = Join-Path $sourceRoot "Purg.Mover\PortableMover.cs"
$boundaryOnlyPatterns = @(
    "Visual Basic file operations",
    "FileSystem.DeleteFile",
    "RecycleOption deletion APIs"
)
foreach ($file in $sourceFiles) {
    $content = Get-Content -LiteralPath $file.FullName -Raw
    $isMoveBoundary = [string]::Equals(
        $file.FullName,
        $moveBoundary,
        [System.StringComparison]::OrdinalIgnoreCase)
    $isPortableDeleteBoundary = [string]::Equals(
        $file.FullName,
        $portableDeleteBoundary,
        [System.StringComparison]::OrdinalIgnoreCase)
    foreach ($entry in $bannedPatterns.GetEnumerator()) {
        if ($isMoveBoundary -and $boundaryOnlyPatterns -contains $entry.Key) {
            continue
        }
        if ($isPortableDeleteBoundary -and $entry.Key -eq "Portable storage deletion API") {
            continue
        }

        if ([regex]::IsMatch($content, $entry.Value, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
            $violations.Add("$($file.FullName): $($entry.Key)")
        }
    }

    if (!$isMoveBoundary -and [regex]::IsMatch($content, '\bFile\s*\.\s*Move\s*\(')) {
        $violations.Add("$($file.FullName): File.Move outside Purg.Mover")
    }

    if (!$isMoveBoundary -and [regex]::IsMatch($content, '\bDirectory\s*\.\s*CreateDirectory\s*\(')) {
        $violations.Add("$($file.FullName): Directory.CreateDirectory outside Purg.Mover")
    }

    if (!$isMoveBoundary -and [regex]::IsMatch($content, '\b(?:ProcessStartInfo|Process\s*\.\s*Start)\b')) {
        $violations.Add("$($file.FullName): external process launch outside Purg.Mover")
    }

    if (!$isPortableDeleteBoundary -and [regex]::IsMatch($content, '\b\w+\.DeleteAsync\s*\(')) {
        $violations.Add("$($file.FullName): portable-device deletion outside Purg.Mover")
    }
}

foreach ($file in $testFiles) {
    $content = Get-Content -LiteralPath $file.FullName -Raw
    foreach ($patternName in $boundaryOnlyPatterns) {
        if ([regex]::IsMatch(
                $content,
                $bannedPatterns[$patternName],
                [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
            $violations.Add("$($file.FullName): $patternName must remain inside Purg.Mover")
        }
    }
    if ([regex]::IsMatch($content, $bannedPatterns["Portable storage deletion API"], [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
        $violations.Add("$($file.FullName): portable-device deletion must remain inside Purg.Mover")
    }
}

if (Test-Path -LiteralPath $moveBoundary) {
    $moverContent = Get-Content -LiteralPath $moveBoundary -Raw
    $deleteCalls = [regex]::Matches($moverContent, '(?s)\bFileSystem\s*\.\s*DeleteFile\s*\((.*?)\)')
    if ($deleteCalls.Count -ne 2) {
        $violations.Add("$($moveBoundary): expected isolated Recycle Bin and permanent-delete calls")
    }

    $recycleCall = $deleteCalls | Where-Object { $_.Groups[1].Value -match 'RecycleOption\.SendToRecycleBin' }
    $permanentCall = $deleteCalls | Where-Object { $_.Groups[1].Value -match 'RecycleOption\.DeletePermanently' }
    foreach ($deleteCall in $deleteCalls) {
        if ($deleteCall.Groups[1].Value -notmatch 'UIOption\.OnlyErrorDialogs') {
            $violations.Add("$($moveBoundary): deletion must use UIOption.OnlyErrorDialogs")
        }
    }
    if (!$recycleCall -or !$permanentCall) {
        $violations.Add("$($moveBoundary): both supported RecycleOption modes must be explicit")
    }

    if ([regex]::IsMatch($moverContent, '\bFile\s*\.\s*Move\s*\(') -or
        [regex]::IsMatch($moverContent, '\bDirectory\s*\.\s*CreateDirectory\s*\(')) {
        $violations.Add("$($moveBoundary): obsolete move-folder operations are not allowed")
    }

    if (Test-Path -LiteralPath $portableDeleteBoundary) {
        $portableMoverContent = Get-Content -LiteralPath $portableDeleteBoundary -Raw
        $portableDeleteCalls = [regex]::Matches($portableMoverContent, '\b\w+\.DeleteAsync\s*\(')
        if ($portableDeleteCalls.Count -ne 1 -or
            $portableMoverContent -notmatch 'DeleteAsync\s*\(\s*StorageDeleteOption\.PermanentDelete\s*\)') {
            $violations.Add("$($portableDeleteBoundary): expected one explicit permanent portable-device delete call")
        }
    }

    if ([regex]::Matches($moverContent, '\bProcess\s*\.\s*Start\s*\(').Count -ne 1 -or
        $moverContent -notmatch 'ProcessStartInfo\s*\(\s*"explorer\.exe"\s*\)' -or
        $moverContent -notmatch 'UseShellExecute\s*=\s*false' -or
        $moverContent -notmatch 'ArgumentList\.Add\s*\(\s*"shell:RecycleBinFolder"\s*\)') {
        $violations.Add("$($moveBoundary): only Explorer may be launched to open the Recycle Bin")
    }
}

if ($violations.Count -gt 0) {
    $violations | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Output "File-operation boundary checks passed."
