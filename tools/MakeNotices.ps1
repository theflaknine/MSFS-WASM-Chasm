# Writes THIRD-PARTY-NOTICES.txt from the packages the app bundles. Rerun it after changing a package.
# Build the app first, so the package list and the NuGet cache are up to date.
$repo = Split-Path $PSScriptRoot -Parent
$assets = Get-Content (Join-Path $repo 'src\WasmChasm.App\obj\project.assets.json') -Raw | ConvertFrom-Json
$cache = Join-Path $env:USERPROFILE '.nuget\packages'
$packages = $assets.libraries.PSObject.Properties |
    Where-Object { $_.Value.type -eq 'package' -and $_.Name -notmatch '^PolySharp/' } |   # PolySharp is compile-time only
    ForEach-Object { $_.Name } | Sort-Object

function Normalise([string]$text) { ($text -replace '\s+', ' ').Trim() }

$dotnet = $packages | Where-Object { $_ -notmatch '^NAudio' }
$licence = $null
$notices = [ordered]@{}
foreach ($p in $dotnet) {
    $dir = Join-Path $cache $p.ToLower()
    $text = Get-Content -LiteralPath (Join-Path $dir 'LICENSE.TXT') -Raw
    if (-not $licence) { $licence = $text }
    elseif ((Normalise $text) -ne (Normalise $licence)) { throw "$p has a different licence. Add it to this script." }
    $file = Join-Path $dir 'THIRD-PARTY-NOTICES.TXT'
    if (Test-Path -LiteralPath $file) {
        $body = Get-Content -LiteralPath $file -Raw
        $key = Normalise $body
        if (-not $notices.Contains($key)) { $notices[$key] = @{ Text = $body; From = New-Object System.Collections.ArrayList } }
        [void]$notices[$key].From.Add($p)
    }
}

$line = '=' * 79
$out = New-Object System.Text.StringBuilder
[void]$out.AppendLine('THIRD-PARTY NOTICES FOR WASM CHASM')
[void]$out.AppendLine()
[void]$out.AppendLine('WasmChasm.exe contains the libraries below. WASM Chasm itself is under the MIT licence (LICENSE.txt),')
[void]$out.AppendLine('and its VT323 font is under the SIL Open Font Licence (OFL.txt).')
[void]$out.AppendLine()
[void]$out.AppendLine($line)
[void]$out.AppendLine(($packages | Where-Object { $_ -match '^NAudio' }) -join ', ')
[void]$out.AppendLine($line)
[void]$out.AppendLine(@'

Copyright 2020 Mark Heath

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

'@)
[void]$out.AppendLine($line)
[void]$out.AppendLine($dotnet -join ', ')
[void]$out.AppendLine($line)
[void]$out.AppendLine()
[void]$out.AppendLine($licence.Trim())
[void]$out.AppendLine()
foreach ($n in $notices.Values) {
    [void]$out.AppendLine($line)
    [void]$out.AppendLine('Notices included with: ' + ($n.From -join ', '))
    [void]$out.AppendLine($line)
    [void]$out.AppendLine()
    [void]$out.AppendLine($n.Text.Trim())
    [void]$out.AppendLine()
}

$path = Join-Path $repo 'THIRD-PARTY-NOTICES.txt'
[IO.File]::WriteAllText($path, $out.ToString(), (New-Object Text.UTF8Encoding $false))
"Wrote $path for $($packages.Count) packages, with $($notices.Count) distinct notice files."
