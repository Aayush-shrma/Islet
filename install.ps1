param (
    [string]$Repo = "Prathambist/Islet",
    [switch]$NoStartup,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13

$InstallDir = "$env:LOCALAPPDATA\Islet"
$TempZip = "$env:TEMP\Islet-win-x64.zip"
$DownloadUrl = "https://github.com/$Repo/releases/latest/download/Islet-win-x64.zip"
$StartMenuPath = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Islet.lnk"
$StartupPath = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup\Islet.lnk"
$ProcessName = "Islet"

# Islet used to be called MusicIsland; an install under the old name is replaced, and its
# settings come along the first time Islet starts
# Releases are signed; anything without a valid signature from this key is refused.
# Islet's updater carries the same key (src/Islet/Services/ReleaseSignature.cs).
$ReleaseKey = "<RSAKeyValue><Modulus>pNGdcQG3b4/+tGyz8nOOuKAg5bt235ti/0gbMawJifyY9cAMgk66W4/OnbUbXT9wc/2Gj7i+l9L1stssQHLdjWrfTC+H9luGw8QBkjTUkbbbs4WwixZi3Uihdi4F9RYk3I4pexhGh4m7drgZgP6COcrnPca66cc4HPbGigoNKCUHl7cdoDg+cy0oZyWV1ikJeBI1fqmA13de0rxS2kp8EkQw7SM/LSuJE1qXVy9M0aO5z3quChpasZurkoDM/tPThDJlZtQAVtXuUP4uqXMOu4kJa9KAdxvcNu1B+hEpuSYLDYN3/7d8pKA3Ux/hsIwP9y0CqHMOGpahGoehvvJqPEGgJm9NjqqvWi8d21j0ARqnUhQ+53OAgqxOdmrXmCCel7u5/Hf90vSAVDSGzhovoKnZRlcbHkHZVs6Nb6ql1CyU2Rux9ZOWHvi1lmE+zGJPr8dm9T1NzECIe7pp2lCOeePwWglghT0pwl1qZvd7HAf6eDA6x02DsRqmHxLyAuhJ</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>"

$OldName = "MusicIsland"
$OldInstallDir = "$env:LOCALAPPDATA\$OldName"
$OldStartMenuPath = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\$OldName.lnk"
$OldStartupPath = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup\$OldName.lnk"

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
    "Hover the island to open the player.",
    "Scroll over the island to change the volume.",
    "Right-click the tray icon to start a timer.",
    "Lyrics show up under the song when you open the island.",
    "When a timer ends, the Glyph lights flicker on the island.",
    "Pick Low, Medium or High under Appearance to trade looks for battery.",
    "The island hides itself in full-screen games, unless you tell it not to.",
    "Click a song title to find it in Spotify."
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

function Show-Intro {
    if (-not $Fancy) {
        Write-Host ""
        Write-Host "  $Note  Islet installer" -ForegroundColor Magenta
        Write-Host ""
        return
    }

    Write-Host ""
    $row = Reserve 4
    $title = "I S L E T"
    $full = 44

    Animate 1700 {
        param($p)
        $ms = $p * 1700

        # The island springs open from a little pill, as it does on screen
        $inner = [int][Math]::Round(4 + ($full - 4) * (Ease-OutBack ([Math]::Min(1.0, $ms / 650))))
        $content = @()

        # Once it's wide enough, the note, then the name letter by letter, then the bars fade in
        if ($inner -ge 22) {
            $content += @("  $Note   ", (Mix 'Shadow' 'Accent' (($ms - 250) / 250)))

            for ($i = 0; $i -lt $title.Length; $i++) {
                $content += @([string]$title[$i], (Mix 'Shadow' 'White' (($ms - 380 - $i * 40) / 220)))
            }

            $bars = ""
            for ($b = 0; $b -lt 4; $b++) { $bars += $Levels[[int](3.5 + 3.4 * [Math]::Sin($ms / 1000 * 11 + $b * 1.9))] }
            $content += @((' ' * ($inner - 21)), $C.Shadow, $bars, (Mix 'Shadow' 'Pink' (($ms - 650) / 300)), "  ", $C.Shadow)
        }

        $lines = @(Island-Lines $inner $content (Mix 'Shadow' 'Edge' ($ms / 200)))
        $lines += Paint @("   a Dynamic Island for your Windows desktop", (Mix 'Shadow' 'Dim' (($ms - 900) / 400)))
        Draw $row $lines
    }

    Write-Host ""
    Write-Host ""
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

# ================= Finale =================

function Show-Finale([string]$Version, [bool]$Updated) {
    $verb = "Installed"
    if ($Updated) { $verb = "Updated" }

    if (-not $Fancy) {
        Write-Host ""
        Write-Host "  $verb Islet $Version" -ForegroundColor Green
        return
    }

    Write-Host ""
    $row = Reserve 5
    $inner = 46
    $name = "Islet $Version"

    function Card([double]$Fraction, [string]$Edge, [double]$Alpha) {
        $cells = 26
        $at = [int][Math]::Round($Fraction * ($cells - 1))
        $elapsed = [int]($Fraction * 214)
        $time = " {0}:{1:00}  -{2}:{3:00}" -f [int][Math]::Floor($elapsed / 60), ($elapsed % 60), [int][Math]::Floor((214 - $elapsed) / 60), ((214 - $elapsed) % 60)

        $text = Mix 'Shadow' 'White' $Alpha
        $faint = Mix 'Shadow' 'Dim' $Alpha
        $accent = Mix 'Shadow' 'Accent' $Alpha
        $track = Mix 'Shadow' 'Edge' $Alpha
        $left = "  " + $Side

        Draw $row @(
            (Paint @(("  " + $Corner[0] + ($Line * $inner) + $Corner[1]), $Edge)),
            (Paint @($left, $Edge, "  $Notes  ", $accent, "Now playing".PadRight($inner - 5), $faint, $Side, $Edge)),
            (Paint @($left, $Edge, "     ", $text, $name.PadRight($inner - 5), $text, $Side, $Edge)),
            (Paint @($left, $Edge, "     ", $text, ($Heavy * $at), $accent, $Knob, $text, ($Line * ($cells - $at - 1)), $track, $time.PadRight($inner - 5 - $cells), $faint, $Side, $Edge)),
            (Paint @(("  " + $Corner[2] + ($Line * $inner) + $Corner[3]), $Edge))
        )
    }

    # The card fades in while its song plays through, easing in and out like a seek
    Animate 1100 {
        param($p)
        $a = Ease-Out ([Math]::Min(1.0, $p / 0.3))
        Card (Ease-InOut $p) (Mix 'Shadow' 'Edge' $a) $a
    }

    # Then its edge glows a few times and settles: the timer's Glyph flicker, softened
    Animate 900 {
        param($p)
        $glow = [Math]::Abs([Math]::Sin($p * [Math]::PI * 3)) * (1 - 0.5 * $p)
        Card 1 (Mix 'Edge' 'Accent' $glow) 1
    }

    [Console]::SetCursorPosition(0, $row + 5)
    Write-Host ""
    Write-Host "  $verb. " -NoNewline -ForegroundColor Green
    Write-Host "Look up: the island is at the top of your screen." -ForegroundColor White
}

# ================= Asking first =================

# Says what's about to happen and waits for a yes. Enter counts as yes; n or Esc backs out.
function Confirm-Install([bool]$Updating, [bool]$Running, [double]$Size) {
    if ($Yes) { return $true }

    $interactive = $false
    try { $interactive = [Environment]::UserInteractive -and $Host.Name -eq 'ConsoleHost' -and -not [Console]::IsInputRedirected } catch { }

    # Nobody can answer from a script or a redirected console, so carry on as before
    if (-not $interactive) {
        Write-Host "  Not in an interactive console, so installing without asking." -ForegroundColor DarkGray
        return $true
    }

    $amount = "the latest release"
    if ($Size -gt 0) { $amount = "the latest release ({0:0} MB)" -f ($Size / 1MB) }

    Write-Host "  This will:" -ForegroundColor Gray
    if ($Running) { Write-Host "    - close the island that's running now" -ForegroundColor DarkGray }
    Write-Host "    - download $amount from GitHub" -ForegroundColor DarkGray
    if (Test-Path $OldInstallDir) { Write-Host "    - replace MusicIsland, Islet's old name, keeping your settings" -ForegroundColor DarkGray }
    if ($Updating) { Write-Host "    - replace the copy in $InstallDir" -ForegroundColor DarkGray }
    else { Write-Host "    - install it in $InstallDir" -ForegroundColor DarkGray }
    if ($NoStartup) { Write-Host "    - add it to the Start menu" -ForegroundColor DarkGray }
    else { Write-Host "    - add it to the Start menu and start it when you sign in" -ForegroundColor DarkGray }
    Write-Host ""

    $question = "Install Islet?"
    if ($Updating) { $question = "Update Islet?" }
    Write-Host "  $question " -NoNewline -ForegroundColor White
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

# ================= Install =================

function Install-Islet {
    Show-Intro
    $updated = Test-Path $InstallDir

    $size = 0
    try { $size = [double](Invoke-WebRequest -Uri $DownloadUrl -Method Head -UseBasicParsing -MaximumRedirection 10).Headers['Content-Length'] } catch { }

    $isRunning = [bool](Get-Process -Name $ProcessName, $OldName -ErrorAction SilentlyContinue)

    if (-not (Confirm-Install $updated $isRunning $size)) {
        Write-Host ""
        Write-Host "  No worries, nothing was changed." -ForegroundColor Gray
        Write-Host ""
        return
    }

    # 1. Stop a running copy (under either name) so its files can be replaced
    $running = Get-Process -Name $ProcessName, $OldName -ErrorAction SilentlyContinue
    if ($running) {
        [void](Invoke-Step "Asking the island to take a bow" { param($names) Stop-Process -Name $names -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 800 } @(, @($ProcessName, $OldName)))
        Done "Stopped the running island"
    }

    # 2. Download the latest release, with the size known up front when GitHub says it
    $download = {
        param($url, $path)
        if (Test-Path $path) { Remove-Item $path -Force }
        Invoke-WebRequest -Uri $url -OutFile $path -UseBasicParsing -MaximumRedirection 10
    }
    $measure = { if (Test-Path $TempZip) { (Get-Item $TempZip).Length } else { 0 } }

    try {
        [void](Invoke-Step "Fetching the latest drop" $download @($DownloadUrl, $TempZip) $measure $size)
    }
    catch {
        if ($Fancy) { Write-Host "  Direct download failed, asking the GitHub API instead..." -ForegroundColor Yellow }
        else { Write-Host "  Direct download failed, querying GitHub API..." -ForegroundColor Yellow }

        try {
            $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -UseBasicParsing
            $asset = $release.assets | Where-Object { $_.name -like "*.zip" } | Select-Object -First 1
            if (-not $asset) { throw "No zip asset found in release." }
            [void](Invoke-Step "Fetching the latest drop" $download @($asset.browser_download_url, $TempZip) $measure ([double]$asset.size))
        }
        catch {
            Write-Host ""
            Write-Host "  Couldn't download Islet: $_" -ForegroundColor Red
            Write-Host "  Check your connection and run the installer again." -ForegroundColor DarkGray
            return
        }
    }

    Done "Downloaded" ("{0:0.0} MB" -f ((Get-Item $TempZip).Length / 1MB))

    # 2b. Check it was signed with Islet's release key before unpacking anything
    try {
        $signature = (Invoke-WebRequest -Uri "$DownloadUrl.sig" -UseBasicParsing -MaximumRedirection 10).Content
        if ($signature -is [byte[]]) { $signature = [Text.Encoding]::ASCII.GetString($signature) }

        $rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider
        $rsa.FromXmlString($ReleaseKey)
        $valid = $rsa.VerifyData([IO.File]::ReadAllBytes($TempZip), "SHA256", [Convert]::FromBase64String($signature.Trim()))
    }
    catch {
        $valid = $false
    }

    if (-not $valid) {
        Remove-Item -Path $TempZip -Force -ErrorAction SilentlyContinue
        Write-Host ""
        Write-Host "  This download isn't signed with Islet's release key, so it wasn't installed." -ForegroundColor Red
        Write-Host "  Try again later; if it keeps happening, please open an issue on GitHub." -ForegroundColor DarkGray
        return
    }

    Done "Signature checked" "signed by Islet's release key"

    # 3. Unpack into LocalAppData
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($TempZip)
    try { $unpacked = [double]($archive.Entries | Measure-Object -Property Length -Sum).Sum } finally { $archive.Dispose() }

    $unpack = {
        param($zip, $folder)
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        if (Test-Path $folder) { Remove-Item -Path $folder -Recurse -Force }
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
        [IO.Compression.ZipFile]::ExtractToDirectory($zip, $folder)
    }
    $measureFolder = { if (Test-Path $InstallDir) { (Get-ChildItem $InstallDir -Recurse -File | Measure-Object -Property Length -Sum).Sum } else { 0 } }

    try {
        [void](Invoke-Step "Unboxing the vinyl" $unpack @($TempZip, $InstallDir) $measureFolder $unpacked)
    }
    catch {
        Write-Host ""
        Write-Host "  Couldn't unpack Islet into $InstallDir`: $_" -ForegroundColor Red
        Write-Host "  If Islet is still open somewhere, close it and run the installer again." -ForegroundColor DarkGray
        return
    }
    finally {
        Remove-Item -Path $TempZip -Force -ErrorAction SilentlyContinue
    }

    $ExePath = Join-Path $InstallDir "Islet.exe"
    if (-not (Test-Path $ExePath)) {
        Write-Host "  Islet.exe was not found in the extracted files." -ForegroundColor Red
        return
    }

    # The release carries a stand-in MusicIsland.exe for old installs updating themselves; a fresh install doesn't need it
    Remove-Item -Path (Join-Path $InstallDir "$OldName.exe") -Force -ErrorAction SilentlyContinue

    $Version = ""
    try { $Version = "v" + ((Get-Item $ExePath).VersionInfo.ProductVersion -split '\+')[0] } catch { }
    Done "Unpacked" $InstallDir

    # 4. Shortcuts, with the app's own icon
    $shortcuts = {
        param($exe, $folder, $startMenu, $startup)
        $shell = New-Object -ComObject WScript.Shell

        $link = $shell.CreateShortcut($startMenu)
        $link.TargetPath = $exe
        $link.WorkingDirectory = $folder
        $link.Description = "A Dynamic Island for Windows: music, messages, a timer and a file shelf"
        $link.Save()

        if ($startup) {
            $link = $shell.CreateShortcut($startup)
            $link.TargetPath = $exe
            $link.WorkingDirectory = $folder
            $link.Description = "Islet Startup"
            $link.Save()
        }
    }

    $startupLink = $StartupPath
    if ($NoStartup) { $startupLink = $null }
    [void](Invoke-Step "Pinning it to the Start menu" $shortcuts @($ExePath, $InstallDir, $StartMenuPath, $startupLink) -MinimumMs 700)
    Done "Added to the Start menu"
    if (-not $NoStartup) { Done "Starts when you sign in" "turn it off in Settings > Advanced" }

    # 5. The old name's leftovers: its folder and shortcuts
    if ((Test-Path $OldInstallDir) -or (Test-Path $OldStartMenuPath) -or (Test-Path $OldStartupPath)) {
        $cleanup = {
            param($paths)
            foreach ($path in $paths) { if (Test-Path $path) { Remove-Item -Path $path -Recurse -Force } }
        }
        [void](Invoke-Step "Tidying away MusicIsland" $cleanup @(, @($OldInstallDir, $OldStartMenuPath, $OldStartupPath)) -MinimumMs 500)
        Done "Replaced MusicIsland" "your settings move over when Islet starts"
    }

    # 6. Launch
    [void](Invoke-Step "Warming up the amp" { param($exe) Start-Process -FilePath $exe } @($ExePath) -MinimumMs 900)

    Show-Finale $Version $updated
    Write-Host ""
    Write-Host "  Hover it to open the player. Right-click the tray icon for timers and Settings." -ForegroundColor DarkGray
    Write-Host "  Installed in $InstallDir" -ForegroundColor DarkGray
    Write-Host ""
}

try {
    if ($Fancy) { [Console]::CursorVisible = $false }
    Install-Islet
}
finally {
    if ($Fancy) { [Console]::CursorVisible = $true }
}
