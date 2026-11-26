param (
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$InstallDir = "$env:LOCALAPPDATA\Islet"
$SettingsDir = "$env:APPDATA\Islet"
$StartMenuPath = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Islet.lnk"
$StartupPath = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup\Islet.lnk"
$ProcessName = "Islet"

# Islet used to be called MusicIsland; anything left under the old name goes too
$OldName = "MusicIsland"
$OldInstallDir = "$env:LOCALAPPDATA\$OldName"
$OldStartMenuPath = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\$OldName.lnk"
$OldStartupPath = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup\$OldName.lnk"

# The look below is the installer's, kept in step with install.ps1: this file is run on its
# own with irm | iex, so it can't borrow it from there.

# ================= Look =================
# Every symbol is built from its code point, so this file stays plain ASCII and reads
# the same whether it's run from disk or piped in with irm | iex.

function Chars([int[]]$codes) { -join ($codes | ForEach-Object { [char]$_ }) }

$Levels = Chars (0x2581..0x2588)                    # eighth blocks, lowest to full
$Spin = Chars 0x280B, 0x2819, 0x2839, 0x2838, 0x283C, 0x2834, 0x2826, 0x2827, 0x2807, 0x280F   # a braille spinner
$Note = [string][char]0x266A
$Notes = [string][char]0x266B
$Line = [string][char]0x2500
$Heavy = [string][char]0x2501
$HeavyLeft = [string][char]0x257E                   # heavy left half, light right half
$HeavyRight = [string][char]0x257C                  # light left half, heavy right half
$Knob = [string][char]0x25CF
$Corner = @([string][char]0x256D, [string][char]0x256E, [string][char]0x2570, [string][char]0x256F)   # rounded corners
$Side = [string][char]0x2502
$Esc = [string][char]27

# Colours as RGB. Things fade in from Shadow, which sits close to a dark console's background.
$Palette = @{
    Shadow = @(44, 44, 52)
    Edge   = @(86, 86, 100)
    Dim    = @(120, 120, 134)
    Gray   = @(172, 172, 184)
    White  = @(244, 244, 248)
    Accent = @(178, 116, 255)
    Pink   = @(255, 104, 176)
    Green  = @(96, 222, 150)
}

function Rgb([int[]]$rgb) { "{0}[38;2;{1};{2};{3}m" -f $Esc, $rgb[0], $rgb[1], $rgb[2] }

# A colour part of the way from one to another
function Mix([string]$From, [string]$To, [double]$T) {
    $T = [Math]::Max(0.0, [Math]::Min(1.0, $T))
    $a = $Palette[$From]
    $b = $Palette[$To]
    Rgb @(
        [int]($a[0] + ($b[0] - $a[0]) * $T),
        [int]($a[1] + ($b[1] - $a[1]) * $T),
        [int]($a[2] + ($b[2] - $a[2]) * $T))
}

$C = @{}
foreach ($name in @($Palette.Keys)) { $C[$name] = Rgb $Palette[$name] }

function Ease-Out([double]$t) { 1 - [Math]::Pow(1 - $t, 3) }

function Ease-InOut([double]$t) {
    if ($t -lt 0.5) { return 4 * $t * $t * $t }
    return 1 - [Math]::Pow(-2 * $t + 2, 3) / 2
}

# Overshoots a touch and settles, like the island's springs
function Ease-OutBack([double]$t) {
    $c1 = 1.4
    1 + ($c1 + 1) * [Math]::Pow($t - 1, 3) + $c1 * [Math]::Pow($t - 1, 2)
}

$Tips = @(
    "Your settings stay behind, so a reinstall picks up where you left off.",
    "The install command brings it back any time.",
    "Found a bug or missing something? Issues are open on GitHub.",
    "Thanks for listening."
)

# Animation needs a real console window that understands colour codes; anywhere else gets plain lines
$Fancy = $false
try {
    $vt = $Host.UI.SupportsVirtualTerminal
    if ($null -eq $vt) { $vt = [Environment]::OSVersion.Version.Build -ge 10586 }
    $Fancy = $vt -and $Host.Name -eq 'ConsoleHost' -and -not [Console]::IsOutputRedirected -and [Console]::WindowWidth -ge 60
}
catch {
}

$Width = 72
if ($Fancy) { $Width = [Math]::Min([Console]::WindowWidth - 1, 72) }

# One line of a frame from (text, colour) pairs, cut to fit the window
function Paint([object[]]$Parts) {
    $out = New-Object Text.StringBuilder
    $used = 0

    for ($i = 0; $i -lt $Parts.Count; $i += 2) {
        $text = [string]$Parts[$i]
        if ($used + $text.Length -gt $Width) { $text = $text.Substring(0, [Math]::Max(0, $Width - $used)) }
        [void]$out.Append([string]$Parts[$i + 1]).Append($text)
        $used += $text.Length
    }

    return $out.ToString()
}

# Writes a whole frame in one go, so the console never shows half of one
function Draw([int]$Row, [string[]]$Lines) {
    $clearEnd = $Esc + '[K'
    [Console]::SetCursorPosition(0, $Row)
    [Console]::Write(($Lines -join ($clearEnd + "`r`n")) + $clearEnd + $Esc + '[0m')
}

function Reserve([int]$Lines) {
    for ($i = 0; $i -lt $Lines; $i++) { Write-Host "" }
    return [Console]::CursorTop - $Lines
}

# Calls the frame with 0 to 1 over the time given, a frame about every 16 ms
function Animate([int]$Ms, [scriptblock]$Frame) {
    $animClock = [Diagnostics.Stopwatch]::StartNew()

    while ($true) {
        $animT = [Math]::Min(1.0, $animClock.Elapsed.TotalMilliseconds / $Ms)
        & $Frame $animT
        if ($animT -ge 1) { break }
        Start-Sleep -Milliseconds 15
    }
}

# ================= The island =================

function Island-Lines([int]$Inner, [object[]]$Content, [string]$Edge) {
    $pad = '  '
    $length = 0
    for ($i = 0; $i -lt $Content.Count; $i += 2) { $length += ([string]$Content[$i]).Length }

    $middle = @(($pad + $Side), $Edge) + $Content + @(((' ' * [Math]::Max(0, $Inner - $length)) + $Side), $Edge)

    @(
        (Paint @(($pad + $Corner[0] + ($Line * $Inner) + $Corner[1]), $Edge)),
        (Paint $middle),
        (Paint @(($pad + $Corner[2] + ($Line * $Inner) + $Corner[3]), $Edge))
    )
}

# ================= Steps =================

# The equalizer keeps each bar's height between frames, so it eases instead of jumping
$EqRows = 3
$script:EqLevels = New-Object double[] 30
$script:EqLast = 0.0

function Equalizer-Lines([double]$T) {
    $count = [Math]::Min($script:EqLevels.Count, [int](($Width - 4) / 2))
    $dt = [Math]::Max(0.0, [Math]::Min(0.1, $T - $script:EqLast))
    $script:EqLast = $T

    # Two beats a second, felt most in the bass on the left
    $beat = [Math]::Pow([Math]::Max(0.0, [Math]::Sin($T * 2 * [Math]::PI * 2)), 3)
    $rows = New-Object string[] $EqRows

    for ($b = 0; $b -lt $count; $b++) {
        $x = $b / ($count - 1)

        # A spectrum-like shape: a slow swell rolling across, a quick shimmer per bar, and the beat
        $swell = 0.62 * (1 - 0.45 * $x) * (0.5 + 0.5 * [Math]::Sin($T * 1.9 - $x * 4.2))
        $shimmer = 0.11 * [Math]::Sin($T * (3.4 + $b * 0.19) + $b * 1.7) + 0.05 * [Math]::Sin($T * (6.1 + $b * 0.13) + $b * 0.6)
        $target = [Math]::Max(0.04, [Math]::Min(1.0, 0.1 + $swell + $shimmer + 0.24 * $beat * (1 - 0.7 * $x)))

        # Quick to rise, slow to fall, like a real meter, but never a hard jump
        $rate = 4.5
        if ($target -gt $script:EqLevels[$b]) { $rate = 11.0 }
        $script:EqLevels[$b] += ($target - $script:EqLevels[$b]) * (1 - [Math]::Exp(-$rate * $dt))

        $eighths = [int][Math]::Round($script:EqLevels[$b] * 8 * $EqRows)

        for ($r = 0; $r -lt $EqRows; $r++) {
            $fill = [Math]::Max(0, [Math]::Min(8, $eighths - ($EqRows - 1 - $r) * 8))
            $char = ' '
            if ($fill -gt 0) { $char = $Levels[$fill - 1] }
            $rows[$r] += "$char "
        }
    }

    # Pink at the top, purple at the foot
    $lines = @()
    for ($r = 0; $r -lt $EqRows; $r++) {
        $lines += Paint @("   ", $C.Shadow, $rows[$r], (Mix 'Accent' 'Pink' (1 - $r / ($EqRows - 1))))
    }
    return $lines
}

# A bar of cells lit from one half-cell to another, so it moves half a cell at a time
function Bar-Parts([int]$Cells, [int]$From, [int]$To, [string]$Lit) {
    $parts = @()
    $run = ""
    $runLit = $false

    for ($cell = 0; $cell -lt $Cells; $cell++) {
        $left = (2 * $cell) -ge $From -and (2 * $cell) -lt $To
        $right = (2 * $cell + 1) -ge $From -and (2 * $cell + 1) -lt $To

        $char = $Line
        $isLit = $true
        if ($left -and $right) { $char = $Heavy }
        elseif ($left) { $char = $HeavyLeft }
        elseif ($right) { $char = $HeavyRight }
        else { $isLit = $false }

        if ($cell -gt 0 -and $isLit -ne $runLit) {
            $parts += @($run, $(if ($runLit) { $Lit } else { $C.Edge }))
            $run = ""
        }

        $run += $char
        $runLit = $isLit
    }

    $parts += @($run, $(if ($runLit) { $Lit } else { $C.Edge }))
    return $parts
}

# The spinner, the label, the bar and the numbers after it; a gliding segment when the size is unknown
function Status-Line([string]$Label, [double]$T, [double]$Done, [double]$Total) {
    $cells = 18
    $glyph = $Spin[[int]($T * 12) % $Spin.Length]
    $parts = @("   $glyph  ", $C.Accent, $Label.PadRight(28), $C.White)

    if ($Total -gt 0) {
        $fraction = [Math]::Max(0.0, [Math]::Min(1.0, $Done / $Total))
        $parts += Bar-Parts $cells 0 ([int][Math]::Round($fraction * $cells * 2)) $C.Accent
        $parts += @(("  {0,3}%  {1:0.0}/{2:0.0} MB" -f [int]($fraction * 100), ($Done / 1MB), ($Total / 1MB)), $C.Gray)
    }
    else {
        $at = [int][Math]::Round(($cells * 2 - 6) * (0.5 - 0.5 * [Math]::Cos($T * 2.6)))
        $parts += Bar-Parts $cells $at ($at + 6) $C.Accent
        if ($Done -gt 0) { $parts += @(("  {0:0.0} MB" -f ($Done / 1MB)), $C.Gray) }
    }

    return Paint $parts
}

function Draw-Step([int]$Row, [string]$Label, [double]$T, [double]$Done, [double]$Total, [int]$FirstTip) {
    $lines = @(Equalizer-Lines $T)
    $lines += Status-Line $Label $T $Done $Total

    # A new tip every few seconds, fading through rather than swapping
    $period = 5.0
    $index = ($FirstTip + [int][Math]::Floor($T / $period)) % $Tips.Count
    $into = $T % $period
    $fade = [Math]::Min(1.0, [Math]::Min($into / 0.4, ($period - $into) / 0.4))
    $lines += Paint @("      tip: ", (Mix 'Shadow' 'Edge' $fade), $Tips[$index], (Mix 'Shadow' 'Dim' $fade))

    Draw $Row $lines
}

# Runs the work in the background while the equalizer plays, then reports how it went
function Invoke-Step {
    param(
        [string]$Label,
        [scriptblock]$Work,
        [object[]]$Arguments = @(),
        [scriptblock]$Measure = $null,
        [double]$Total = 0,
        [int]$MinimumMs = 0
    )

    $shell = [PowerShell]::Create()
    # The work's own param() has to come first in its block, so it runs nested and gets the arguments passed on
    [void]$shell.AddScript("`$ErrorActionPreference = 'Stop'; `$ProgressPreference = 'SilentlyContinue'; & {" + $Work.ToString() + "} @args")
    foreach ($argument in $Arguments) { [void]$shell.AddArgument($argument) }

    $clock = [Diagnostics.Stopwatch]::StartNew()
    $async = $shell.BeginInvoke()

    if ($Fancy) {
        $row = Reserve ($EqRows + 2)
        $firstTip = Get-Random -Maximum $Tips.Count
        $shown = 0.0
        $sample = 0.0
        $sampledAt = -1.0
        $last = 0.0

        while (-not $async.IsCompleted -or $clock.ElapsedMilliseconds -lt $MinimumMs) {
            $t = $clock.Elapsed.TotalSeconds

            # Measuring can mean walking a whole folder, so it's done a few times a second, not every frame
            if ($Measure -and $t - $sampledAt -ge 0.2) {
                $sampledAt = $t
                try { $sample = [double](& $Measure) } catch { }
            }

            # The bar glides towards each new measurement instead of jumping to it
            $shown += ($sample - $shown) * (1 - [Math]::Exp(-9 * ($t - $last)))
            $last = $t

            Draw-Step $row $Label $t $shown $Total $firstTip
            Start-Sleep -Milliseconds 15
        }
    }
    else {
        Write-Host "  $Label..." -ForegroundColor Gray
        while (-not $async.IsCompleted) { Start-Sleep -Milliseconds 100 }
    }

    try {
        $result = $shell.EndInvoke($async)
        if ($shell.Streams.Error.Count -gt 0) { throw $shell.Streams.Error[0] }

        # Let the bar run to the end rather than vanish short of it
        if ($Fancy -and $Total -gt 0) {
            $from = $shown
            Animate 260 { param($p) Draw-Step $row $Label $clock.Elapsed.TotalSeconds ($from + ($Total - $from) * (Ease-Out $p)) $Total $firstTip }
        }
    }
    finally {
        $shell.Dispose()
        if ($Fancy) {
            # Fold the equalizer away, leaving room for just the finished line
            Draw $row @(1..($EqRows + 2) | ForEach-Object { "" })
            [Console]::SetCursorPosition(0, $row)
        }
    }

    return $result
}

function Done([string]$Text, [string]$Detail = "") {
    if (-not $Fancy) {
        Write-Host "  $Note " -NoNewline -ForegroundColor Green
        Write-Host $Text -NoNewline -ForegroundColor White
        Write-Host "  $Detail" -ForegroundColor DarkGray
        return
    }

    # Fades in rather than popping up
    $row = Reserve 1
    Animate 180 {
        param($p)
        $a = Ease-Out $p
        Draw $row @(Paint @("  $Note ", (Mix 'Shadow' 'Green' $a), $Text, (Mix 'Shadow' 'White' $a), "  $Detail", (Mix 'Shadow' 'Dim' $a)))
    }
    [Console]::SetCursorPosition(0, $row + 1)
}

# ================= Hello =================

function Show-Intro {
    if (-not $Fancy) {
        Write-Host ""
        Write-Host "  $Note  Islet uninstaller" -ForegroundColor Magenta
        Write-Host ""
        return
    }

    Write-Host ""
    $row = Reserve 4
    $title = "I S L E T"
    $full = 44

    Animate 1500 {
        param($p)
        $ms = $p * 1500

        # The island springs open from a little pill, as it does on screen
        $inner = [int][Math]::Round(4 + ($full - 4) * (Ease-OutBack ([Math]::Min(1.0, $ms / 650))))
        $content = @()

        if ($inner -ge 22) {
            $content += @("  $Note   ", (Mix 'Shadow' 'Accent' (($ms - 250) / 250)))

            for ($i = 0; $i -lt $title.Length; $i++) {
                $content += @([string]$title[$i], (Mix 'Shadow' 'White' (($ms - 380 - $i * 40) / 220)))
            }

            # Quieter bars than the installer's: the music's winding down
            $bars = ""
            for ($b = 0; $b -lt 4; $b++) { $bars += $Levels[[int](2 + 1.8 * [Math]::Sin($ms / 1000 * 6 + $b * 1.9))] }
            $content += @((' ' * ($inner - 21)), $C.Shadow, $bars, (Mix 'Shadow' 'Accent' (($ms - 650) / 300)), "  ", $C.Shadow)
        }

        $lines = @(Island-Lines $inner $content (Mix 'Shadow' 'Edge' ($ms / 200)))
        $lines += Paint @("   taking Islet off this PC", (Mix 'Shadow' 'Dim' (($ms - 900) / 400)))
        Draw $row $lines
    }

    Write-Host ""
    Write-Host ""
}

# ================= Asking first =================

# Says what's about to go and waits for a yes. Enter counts as yes; n or Esc backs out.
function Confirm-Uninstall([bool]$Running, [string[]]$Removing) {
    if ($Yes) { return $true }

    $interactive = $false
    try { $interactive = [Environment]::UserInteractive -and $Host.Name -eq 'ConsoleHost' -and -not [Console]::IsInputRedirected } catch { }

    # Nobody can answer from a script or a redirected console, so carry on as before
    if (-not $interactive) {
        Write-Host "  Not in an interactive console, so uninstalling without asking." -ForegroundColor DarkGray
        return $true
    }

    Write-Host "  This will:" -ForegroundColor Gray
    if ($Running) { Write-Host "    - close the island that's running now" -ForegroundColor DarkGray }
    foreach ($thing in $Removing) { Write-Host "    - remove $thing" -ForegroundColor DarkGray }
    Write-Host "    - keep your settings in $SettingsDir" -ForegroundColor DarkGray
    Write-Host ""

    Write-Host "  Uninstall Islet? " -NoNewline -ForegroundColor White
    Write-Host "[Y/n] " -NoNewline -ForegroundColor DarkGray

    try {
        if ($Fancy) { [Console]::CursorVisible = $true }

        # Keys pressed before the question, like a pasted line break, must not answer it
        while ([Console]::KeyAvailable) { [void][Console]::ReadKey($true) }

        while ($true) {
            $key = [Console]::ReadKey($true)

            if ($key.Key -eq 'Enter' -or $key.KeyChar -eq 'y' -or $key.KeyChar -eq 'Y') {
                Write-Host "yes" -ForegroundColor Green
                Write-Host ""
                return $true
            }

            if ($key.Key -eq 'Escape' -or $key.KeyChar -eq 'n' -or $key.KeyChar -eq 'N') {
                Write-Host "no" -ForegroundColor Yellow
                return $false
            }
        }
    }
    finally {
        if ($Fancy) { [Console]::CursorVisible = $false }
    }
}

# ================= Goodbye =================

# The installer's island played backwards: the bars settle, the name fades away letter by
# letter, and the island folds back into a pill and goes
function Show-Goodbye([bool]$Clean) {
    $message = "Islet has been uninstalled."
    if (-not $Clean) { $message = "Islet is mostly uninstalled; see above for what's left." }

    if (-not $Fancy) {
        Write-Host ""
        if ($Clean) { Write-Host "  $message" -ForegroundColor Green }
        else { Write-Host "  $message" -ForegroundColor Yellow }
        return
    }

    Write-Host ""
    $row = Reserve 3
    $title = "I S L E T"
    $full = 44

    Animate 1500 {
        param($p)
        $ms = $p * 1500
        $content = @()

        # Shrinks once the name has gone, gathering speed like a spring letting go
        $shrink = [Math]::Max(0.0, [Math]::Min(1.0, ($ms - 700) / 550))
        $inner = [int][Math]::Round($full - ($full - 4) * $shrink * $shrink * $shrink)

        if ($inner -ge 22) {
            $content += @("  $Notes   ", (Mix 'Accent' 'Shadow' (($ms - 500) / 250)))

            for ($i = 0; $i -lt $title.Length; $i++) {
                $content += @([string]$title[$i], (Mix 'White' 'Shadow' (($ms - 150 - ($title.Length - 1 - $i) * 40) / 220)))
            }

            # The bars come to rest
            $settle = [Math]::Max(0.0, 1 - $ms / 500)
            $bars = ""
            for ($b = 0; $b -lt 4; $b++) { $bars += $Levels[[int]([Math]::Max(0, (3.5 + 3.4 * [Math]::Sin($ms / 1000 * 11 + $b * 1.9)) * $settle))] }
            $content += @((' ' * ($inner - 21)), $C.Shadow, $bars, (Mix 'Pink' 'Shadow' (($ms - 300) / 300)), "  ", $C.Shadow)
        }

        # The pill fades out as it closes
        Draw $row @(Island-Lines $inner $content (Mix 'Edge' 'Shadow' (($ms - 1050) / 400)))
    }

    # Then nothing's left where it was, and the parting words fade in
    Draw $row @("", "", "")
    $done = 'Green'
    if (-not $Clean) { $done = 'Pink' }

    Animate 350 {
        param($p)
        Draw $row @(Paint @("  $Note ", (Mix 'Shadow' $done (Ease-Out $p)), $message, (Mix 'Shadow' 'White' (Ease-Out $p))))
    }

    [Console]::SetCursorPosition(0, $row + 1)
}

# ================= Uninstall =================

function Folder-Size([string]$Path) {
    if (-not (Test-Path $Path)) { return 0.0 }
    $sum = (Get-ChildItem $Path -Recurse -File -Force -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
    if ($sum) { return [double]$sum }
    return 0.0
}

# Reports a step that didn't work, and remembers that it didn't
function Failed([string]$What, $Problem) {
    $script:Clean = $false
    Write-Host "  $What`: $Problem" -ForegroundColor Yellow
}

function Uninstall-Islet {
    Show-Intro

    $running = [bool](Get-Process -Name $ProcessName, $OldName -ErrorAction SilentlyContinue)
    $hasShortcuts = (Test-Path $StartMenuPath) -or (Test-Path $StartupPath)
    $hasOld = (Test-Path $OldInstallDir) -or (Test-Path $OldStartMenuPath) -or (Test-Path $OldStartupPath)

    $removing = @()
    if (Test-Path $InstallDir) { $removing += "the app in $InstallDir" }
    if ($hasShortcuts) { $removing += "its Start menu and sign-in shortcuts" }
    if ($hasOld) { $removing += "what's left of MusicIsland, Islet's old name" }

    if (-not $running -and $removing.Count -eq 0) {
        Write-Host "  Islet isn't installed here, so there's nothing to remove." -ForegroundColor Gray
        Write-Host ""
        return
    }

    if (-not (Confirm-Uninstall $running $removing)) {
        Write-Host ""
        Write-Host "  No worries, nothing was changed." -ForegroundColor Gray
        Write-Host ""
        return
    }

    $script:Clean = $true

    # 1. Stop a running copy (under either name) so its files can go
    if ($running) {
        try {
            [void](Invoke-Step "Asking the island to take a bow" { param($names) Stop-Process -Name $names -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 600 } @(, @($ProcessName, $OldName)) -MinimumMs 900)
            Done "Stopped the running island"
        }
        catch { Failed "Couldn't stop the running island" $_ }
    }

    # 2. The shortcuts
    if ($hasShortcuts) {
        $hadStartup = Test-Path $StartupPath
        $unpin = {
            param($paths)
            foreach ($path in $paths) { if (Test-Path $path) { Remove-Item -Path $path -Force } }
        }

        try {
            [void](Invoke-Step "Unpinning it from the Start menu" $unpin @(, @($StartMenuPath, $StartupPath)) -MinimumMs 700)
            Done "Removed from the Start menu"
            if ($hadStartup) { Done "Won't start when you sign in" }
        }
        catch { Failed "Couldn't remove the shortcuts" $_ }
    }

    # 3. The app itself, with the bar counting what's gone
    if (Test-Path $InstallDir) {
        $size = Folder-Size $InstallDir
        $measure = { $size - (Folder-Size $InstallDir) }

        try {
            [void](Invoke-Step "Packing up the vinyl" { param($folder) Remove-Item -Path $folder -Recurse -Force } @($InstallDir) $measure $size -MinimumMs 800)
            Done "Removed the app" ("{0:0.0} MB freed" -f ($size / 1MB))
        }
        catch { Failed "Couldn't remove everything in $InstallDir (is Islet still open?)" $_ }
    }

    # 4. The old name's leftovers
    if ($hasOld) {
        $cleanup = {
            param($paths)
            foreach ($path in $paths) { if (Test-Path $path) { Remove-Item -Path $path -Recurse -Force } }
        }

        try {
            [void](Invoke-Step "Tidying away MusicIsland" $cleanup @(, @($OldInstallDir, $OldStartMenuPath, $OldStartupPath)) -MinimumMs 500)
            Done "Removed MusicIsland's leftovers"
        }
        catch { Failed "Couldn't remove MusicIsland's leftovers" $_ }
    }

    Show-Goodbye $script:Clean
    Write-Host ""

    if (Test-Path $SettingsDir) {
        Write-Host "  Your settings are still in $SettingsDir, so a reinstall picks up where you left off." -ForegroundColor DarkGray
        Write-Host "  To forget them too, delete that folder." -ForegroundColor DarkGray
    }

    Write-Host "  Thanks for listening." -ForegroundColor DarkGray
    Write-Host ""
}

try {
    if ($Fancy) { [Console]::CursorVisible = $false }
    Uninstall-Islet
}
finally {
    if ($Fancy) { [Console]::CursorVisible = $true }
}
