# =====================================================================
#  PainelDeck · instala o ajudante de sensores (temperatura de verdade da CPU)
#  Rode uma vez (clique direito → Executar com o PowerShell). Pede admin UMA vez e faz:
#   1. instala o driver PawnIO (assinado; é o que o LibreHardwareMonitor usa hoje) pelo winget, se faltar
#   2. copia o PainelDeck pra %ProgramData%\PainelDeck\Sensores\app (o ajudante roda dessa cópia,
#      assim as atualizações do app não esbarram em arquivo travado)
#   3. cria a tarefa agendada "PainelDeck Sensores": abre no logon, como admin, sem janela e sem pedir nada
#   4. liga o ajudante agora e mostra a temperatura lida
#  O painel lê %ProgramData%\PainelDeck\Sensores\sensores.json (não precisa reiniciar o PainelDeck).
#  Desinstalar: rode com -Remover.
# =====================================================================
param(
  [switch]$Remover,
  [string]$Usuario = ""
)

$ErrorActionPreference = "Stop"
$task = "PainelDeck Sensores"
$base = Join-Path $env:ProgramData "PainelDeck\Sensores"
$dest = Join-Path $base "app"
$app = Split-Path -Parent $PSScriptRoot   # scripts\ fica dentro da pasta do app

# ---------- pede admin (uma vez) ----------
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
  $me = "$env:USERDOMAIN\$env:USERNAME"
  $argList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"", "-Usuario", "`"$me`"")
  if ($Remover) { $argList += "-Remover" }
  try { Start-Process powershell.exe -Verb RunAs -ArgumentList $argList }
  catch { Write-Host "Precisa aceitar o pedido de administrador pra instalar." -ForegroundColor Red; Read-Host "Enter pra fechar" | Out-Null }
  exit
}
if (-not $Usuario) { $Usuario = "$env:USERDOMAIN\$env:USERNAME" }

function Step([string]$m) { Write-Host "`n> $m" -ForegroundColor Cyan }
function StopHelper {
  Stop-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue
  Get-Process PainelDeck -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($dest, [StringComparison]::OrdinalIgnoreCase) } | Stop-Process -Force -ErrorAction SilentlyContinue
  Start-Sleep -Milliseconds 800
}

try {
  if ($Remover) {
    Step "Removendo o ajudante de sensores"
    StopHelper
    Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue
    if (Test-Path $base) { Remove-Item $base -Recurse -Force }
    Write-Host "Pronto. (O driver PawnIO fica; desinstale em Aplicativos se quiser.)" -ForegroundColor Green
    Read-Host "Enter pra fechar" | Out-Null
    exit
  }

  if (-not (Test-Path (Join-Path $app "PainelDeck.exe"))) { throw "Não achei o PainelDeck.exe em $app (rode o script de dentro da pasta scripts do app)." }

  # ---------- 1. driver PawnIO ----------
  Step "Driver PawnIO"
  $pawn = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO"
  if (Test-Path $pawn) { Write-Host "já instalado" }
  else {
    $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
    if (-not $winget) { throw "Sem winget. Instale o PawnIO na mão (https://pawnio.eu) e rode este script de novo." }
    & winget.exe install --id namazso.PawnIO -e --silent --accept-package-agreements --accept-source-agreements
    if (-not (Test-Path $pawn)) { throw "O PawnIO não instalou. Instale na mão (https://pawnio.eu) e rode este script de novo." }
    Write-Host "instalado"
  }

  # ---------- 2. cópia do app ----------
  Step "Copiando o PainelDeck pra $dest"
  StopHelper
  $null = New-Item -ItemType Directory -Force -Path $dest
  # /MIR espelha; sem dados pessoais, logs, backups nem capas
  & robocopy.exe $app $dest /MIR /NFL /NDL /NJH /NJS /NP /R:2 /W:1 /XD "$app\logs" "$app\.atualizacao" "$app\backups" "$app\tools" "$app\wwwroot\games" /XF settings.json deck.json "deck.backup*.json" playtime.json* version.json | Out-Null
  if ($LASTEXITCODE -ge 8) { throw "robocopy falhou (código $LASTEXITCODE)" }

  # ---------- 3. tarefa agendada ----------
  Step "Criando a tarefa '$task' (abre no logon, como admin)"
  $action = New-ScheduledTaskAction -Execute (Join-Path $dest "PainelDeck.exe") -Argument "--sensors" -WorkingDirectory $dest
  $trigger = New-ScheduledTaskTrigger -AtLogOn -User $Usuario
  $principal = New-ScheduledTaskPrincipal -UserId $Usuario -LogonType Interactive -RunLevel Highest
  $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -RestartCount 5 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew -Priority 7
  Register-ScheduledTask -TaskName $task -Action $action -Trigger $trigger -Principal $principal -Settings $settings `
    -Description "Lê a temperatura da CPU pro PainelDeck (LibreHardwareMonitorLib)." -Force | Out-Null

  # ---------- 4. liga e confere ----------
  Step "Ligando o ajudante"
  Start-ScheduledTask -TaskName $task
  $file = Join-Path $base "sensores.json"
  $ok = $false
  for ($i = 0; $i -lt 20; $i++) {
    Start-Sleep -Seconds 1
    if (Test-Path $file) {
      try { $j = Get-Content $file -Raw | ConvertFrom-Json } catch { continue }
      if ($j.temp) { Write-Host ("{0}: {1} °C ({2})" -f $j.cpu, $j.temp, $j.sensor) -ForegroundColor Green; $ok = $true; break }
      if ($j.error -and $i -ge 6) { Write-Host "O ajudante abriu, mas: $($j.error)" -ForegroundColor Yellow; break }
    }
  }
  if (-not $ok -and -not (Test-Path $file)) { Write-Host "O ajudante não respondeu. Veja $base\sensores.log" -ForegroundColor Yellow }
  if ($ok) { Write-Host "`nPronto! Em 2 segundos a temperatura aparece no painel." -ForegroundColor Green }
}
catch {
  Write-Host "`nErro: $($_.Exception.Message)" -ForegroundColor Red
}
Read-Host "`nEnter pra fechar" | Out-Null
