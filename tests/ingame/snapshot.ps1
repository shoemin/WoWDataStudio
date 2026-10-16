# snapshot.ps1 -- record (relative path, size, mtime, sha256) of every file of the main Client folder and the wave clones Client1..6,
# so the visual test can prove it left them untouched. Junctions (the clones' Data -> Client\Data) are recorded as links, not followed.
# Files above 64 MB (the stock MPQs) get size + mtime only (hashing 5 GB per run adds nothing: they are never opened for writing).
#   .\snapshot.ps1 -Out before.tsv          then  .\snapshot.ps1 -Out after.tsv -Compare before.tsv
param([Parameter(Mandatory)][string]$Out, [string]$Compare = '')
$base = '<your WoW Custom Client folder>'
$roots = @('Client') + (1..6 | ForEach-Object { "Client$_" })
$lines = New-Object System.Collections.Generic.List[string]
function Walk([string]$dir, [string]$rel) {
    foreach ($e in [IO.Directory]::EnumerateFileSystemEntries($dir)) {
        $name = [IO.Path]::GetFileName($e)
        $r = if ($rel) { "$rel\$name" } else { $name }
        $attr = [IO.File]::GetAttributes($e)
        if ($attr -band [IO.FileAttributes]::Directory) {
            if ($attr -band [IO.FileAttributes]::ReparsePoint) { $t = (Get-Item -LiteralPath $e -Force).Target -join ','; $lines.Add("$r`tLINK`t$t`t-"); continue }
            Walk $e $r
        } else {
            $fi = New-Object IO.FileInfo $e
            $h = '-'
            if ($fi.Length -le 64MB) {
                try { $h = (Get-FileHash -LiteralPath $e -Algorithm SHA256).Hash } catch { $h = 'LOCKED' }
            }
            $lines.Add("$r`t$($fi.Length)`t$($fi.LastWriteTimeUtc.ToString('o'))`t$h")
        }
    }
}
foreach ($root in $roots) { if (Test-Path "$base\$root") { Walk "$base\$root" $root } }
[IO.File]::WriteAllLines($Out, $lines)
"{0} entries -> {1}" -f $lines.Count, $Out
if ($Compare) {
    $a = @{}; foreach ($l in [IO.File]::ReadAllLines($Compare)) { $k = $l.Split("`t")[0]; $a[$k] = $l }
    $b = @{}; foreach ($l in $lines) { $k = $l.Split("`t")[0]; $b[$k] = $l }
    $diff = 0
    foreach ($k in $a.Keys) { if (-not $b.ContainsKey($k)) { "REMOVED  $k"; $diff++ } elseif ($a[$k] -ne $b[$k]) { "CHANGED  $($a[$k])  ->  $($b[$k])"; $diff++ } }
    foreach ($k in $b.Keys) { if (-not $a.ContainsKey($k)) { "ADDED    $($b[$k])"; $diff++ } }
    "differences: $diff"
}
