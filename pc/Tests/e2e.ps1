# End-to-end check on one PC: the real ClipBridge.exe watches the real Windows
# clipboard and delivers to the tablet's networking code running on a JVM, and puts
# what that code sends on the real Windows clipboard.
# Uses temporary settings and restores the clipboard afterwards.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing, System.Security

$root = Resolve-Path "$PSScriptRoot\..\.."
$exe = "$root\dist\ClipBridge.exe"
$classes = "$root\build\interop-classes"
$save = Join-Path $env:TEMP "cb-e2e-$(Get-Random)"
$cfg = Join-Path $env:TEMP "cb-e2e-data-$(Get-Random)"
$outbox = Join-Path $env:TEMP "cb-e2e-outbox-$(Get-Random)"
$code = 'ABCD-EFGH'
$failed = 0

function Check($name, $ok, $detail = '') {
    $word = if ($ok) { 'PASS' } else { $script:failed++; 'FAIL' }
    "$word  $name  $detail"
}
function WaitClipboard($test, $ms = 8000) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $ms) {
        try { if (& $test) { return $sw.ElapsedMilliseconds } } catch { }
        Start-Sleep -Milliseconds 100
    }
    return -1
}
# Writes then renames, so the tablet code never reads a half-written file.
function TabletCopies($name, [byte[]]$bytes) {
    [IO.File]::WriteAllBytes("$outbox\$name.tmp", $bytes)
    Move-Item "$outbox\$name.tmp" "$outbox\$name"
}
function NewClips($filter) { @(Get-ChildItem $save -Filter $filter -ErrorAction SilentlyContinue | Sort-Object Name) }
function WaitNew($filter, $count, $ms = 8000) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $ms) {
        if ((NewClips $filter).Count -ge $count) { return $sw.ElapsedMilliseconds }
        Start-Sleep -Milliseconds 50
    }
    return -1
}

if (Get-Process ClipBridge -ErrorAction SilentlyContinue) { throw 'Exit ClipBridge before running this test' }
$env:CLIPBRIDGE_DATA_DIR = $cfg
New-Item -ItemType Directory -Force $save, $cfg, $classes, $outbox | Out-Null

& javac -nowarn -d $classes "$root\android\src\com\clipbridge\Protocol.java" "$root\android\src\com\clipbridge\BridgeServer.java" "$root\android\test\InteropServer.java"

# Remember the clipboard so the test leaves it as it was.
$savedText = if ([Windows.Forms.Clipboard]::ContainsText()) { [Windows.Forms.Clipboard]::GetText() } else { $null }
$savedImage = if ([Windows.Forms.Clipboard]::ContainsImage()) { [Windows.Forms.Clipboard]::GetImage() } else { $null }

$settings = @{
    ProtectedCode = [Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect(
        [Text.Encoding]::UTF8.GetBytes(($code -replace '-', '')), $null, 'CurrentUser'))
    ManualHost = '127.0.0.1'
    LastHost = $null
    FirstRunDone = $true   # Keeps the test from registering autostart.
}
$settings | ConvertTo-Json | Set-Content "$cfg\settings.json" -Encoding ascii

$env:CB_SAVE_DIR = $save
$env:CB_OUTBOX_DIR = $outbox
$java = Start-Process java -ArgumentList '-cp', "`"$classes`"", 'InteropServer', $code, '47821', '47822' -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 1200
$app = Start-Process $exe -PassThru

try {
    Start-Sleep -Seconds 4
    Check 'tray app stays running' (-not $app.HasExited)

    # A screenshot-style image: bitmap only, as Lightshot puts on the clipboard.
    $bmp = New-Object Drawing.Bitmap 800, 500
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.Clear([Drawing.Color]::White)
    $brush = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(200, 30, 60))
    $g.FillRectangle($brush, 100, 100, 200, 150)
    $g.Dispose()
    [Windows.Forms.Clipboard]::SetImage($bmp)
    $ms = WaitNew '*.png' 1
    Check 'copied image reaches tablet code' ($ms -ge 0) "$ms ms"
    $png = (NewClips '*.png')[0]
    if ($png) {
        $got = New-Object Drawing.Bitmap $png.FullName
        Check 'image size is intact' ($got.Width -eq 800 -and $got.Height -eq 500) "$($got.Width)x$($got.Height)"
        $inside = $got.GetPixel(150, 150)
        $outside = $got.GetPixel(600, 400)
        Check 'image colors are intact' ($inside.R -eq 200 -and $inside.G -eq 30 -and $inside.B -eq 60 -and $outside.R -eq 255) "inside=$inside outside=$outside"
        $got.Dispose()
    }

    $hebrew = "hello " + [string]::new([char[]](0x05E9, 0x05DC, 0x05D5, 0x05DD))
    [Windows.Forms.Clipboard]::SetText($hebrew)
    $ms = WaitNew '*.txt' 1
    Check 'copied text reaches tablet code' ($ms -ge 0) "$ms ms"
    $txt = (NewClips '*.txt')[0]
    if ($txt) {
        $read = [IO.File]::ReadAllText($txt.FullName, [Text.Encoding]::UTF8)
        Check 'Hebrew text is intact' ($read -ceq $hebrew)
    }

    # Password managers mark their copies; those must stay on the PC.
    $marked = New-Object Windows.Forms.DataObject
    $marked.SetData([Windows.Forms.DataFormats]::UnicodeText, 'secret-password')
    $marked.SetData('ExcludeClipboardContentFromMonitorProcessing', $true, ([IO.MemoryStream]::new([byte[]](0, 0, 0, 0))))
    [Windows.Forms.Clipboard]::SetDataObject($marked, $true)
    Start-Sleep -Seconds 2
    Check 'password-manager copies stay on the PC' ((NewClips '*.txt').Count -eq 1)

    # Same image again, straight after: a deliberate second copy later must still send.
    Start-Sleep -Seconds 2
    [Windows.Forms.Clipboard]::SetImage($bmp)
    $ms = WaitNew '*.png' 2
    Check 'copying again sends again' ($ms -ge 0) "$ms ms"

    # --- Tablet to PC: what the tablet sends lands on the Windows clipboard.
    $txtBefore = (NewClips '*.txt').Count
    $pngBefore = (NewClips '*.png').Count
    $fromTablet = "from the tablet " + [string]::new([char[]](0x05E9, 0x05DC, 0x05D5, 0x05DD))
    TabletCopies 'out-1.txt' ([Text.Encoding]::UTF8.GetBytes($fromTablet))
    $ms = WaitClipboard { [Windows.Forms.Clipboard]::GetText() -ceq $fromTablet }
    Check 'tablet text lands on the PC clipboard' ($ms -ge 0) "$ms ms"

    $ms2 = New-Object IO.MemoryStream
    $bmp.Save($ms2, [Drawing.Imaging.ImageFormat]::Png)
    TabletCopies 'out-2.png' $ms2.ToArray()
    $ms = WaitClipboard {
        $img = [Windows.Forms.Clipboard]::GetImage()
        $img -and $img.Width -eq 800 -and $img.Height -eq 500 -and $img.GetPixel(150, 150).R -eq 200
    }
    Check 'tablet image lands on the PC clipboard' ($ms -ge 0) "$ms ms"
    $bmp.Dispose()

    # The PC must not send the tablet's own clip straight back to it.
    Start-Sleep -Seconds 2
    Check "tablet clips aren't echoed back" ((NewClips '*.txt').Count -eq $txtBefore -and (NewClips '*.png').Count -eq $pngBefore)

    # And a copy made on the PC afterwards still goes to the tablet.
    [Windows.Forms.Clipboard]::SetText('after the tablet sent one')
    $ms = WaitNew '*.txt' ($txtBefore + 1)
    Check 'PC copies still reach the tablet afterwards' ($ms -ge 0) "$ms ms"

    "--- app log"
    Get-Content "$cfg\log.txt" -ErrorAction SilentlyContinue | Select-Object -Last 10
}
finally {
    if ($app -and -not $app.HasExited) { Stop-Process -Id $app.Id -Force -Confirm:$false }
    if ($java -and -not $java.HasExited) { Stop-Process -Id $java.Id -Force -Confirm:$false }
    Start-Sleep -Milliseconds 300
    Remove-Item -Recurse -Force $cfg, $save, $outbox -Confirm:$false -ErrorAction SilentlyContinue
    if ($savedImage) { [Windows.Forms.Clipboard]::SetImage($savedImage) }
    elseif ($savedText) { [Windows.Forms.Clipboard]::SetText($savedText) }
}

if ($failed -eq 0) { 'ALL PASSED' } else { "$failed FAILED"; exit 1 }
