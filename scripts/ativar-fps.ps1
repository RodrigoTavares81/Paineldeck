# =====================================================================
#  PainelDeck · ativar o FPS do modo jogo (rode UMA vez: botão direito → "Executar com o PowerShell")
#
#  O FPS vem do PresentMon (ferramenta oficial e open source da Intel: github.com/GameTechDev/PresentMon).
#  Ele lê os eventos de "present" do próprio Windows, sem injetar nada no jogo (seguro com anti-cheat).
#  Este script:
#   1. baixa o PresentMon (versão de linha de comando) pra pasta tools\ do PainelDeck
#   2. coloca o seu usuário no grupo "Usuários de log de desempenho" do Windows, que é o que deixa
#      o PresentMon ler esses eventos sem rodar como administrador (pede confirmação do Windows, UAC)
#  Depois: saia e entre de novo no Windows (ou reinicie) pra permissão valer.
# =====================================================================
param([switch]$SoGrupo, [string]$Usuario = "")

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$PerfLogUsers = "S-1-5-32-559"   # "Performance Log Users" / "Usuários de log de desempenho" (o nome muda com o idioma, o SID não)

if ($SoGrupo) {
  # parte que roda como administrador
  try {
    $grupo = (New-Object Security.Principal.SecurityIdentifier $PerfLogUsers).Translate([Security.Principal.NTAccount]).Value.Split('\')[-1]
    & net localgroup "$grupo" "$Usuario" /add | Out-Null
    Write-Host "Pronto: $Usuario está no grupo '$grupo'." -ForegroundColor Green
  } catch {
    Write-Host "Não consegui pôr no grupo: $($_.Exception.Message)" -ForegroundColor Red
  }
  Start-Sleep -Seconds 3
  exit
}

$root = Split-Path -Parent $PSScriptRoot
$tools = Join-Path $root "tools"
$null = New-Item -ItemType Directory -Force -Path $tools

# ---------- 1. PresentMon ----------
$existing = Get-ChildItem $tools -Filter "PresentMon*.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($existing) {
  Write-Host "PresentMon já está em tools\: $($existing.Name)"
} else {
  Write-Host "Baixando o PresentMon do GitHub da Intel…"
  $rel = Invoke-RestMethod "https://api.github.com/repos/GameTechDev/PresentMon/releases/latest" -Headers @{ "User-Agent" = "PainelDeck" }
  $asset = $rel.assets | Where-Object { $_.name -match '^PresentMon-[\d.]+-x64\.exe$' } | Select-Object -First 1
  if (-not $asset) { $asset = $rel.assets | Where-Object { $_.name -match '^PresentMon.*x64.*\.exe$' -and $_.name -notmatch 'Application|Service|Setup' } | Select-Object -First 1 }
  if (-not $asset) {
    Write-Host "Não achei o .exe na versão $($rel.tag_name). Baixe na mão em github.com/GameTechDev/PresentMon/releases e ponha em $tools" -ForegroundColor Yellow
  } else {
    Invoke-WebRequest $asset.browser_download_url -OutFile (Join-Path $tools $asset.name) -UseBasicParsing
    Write-Host "Baixado: tools\$($asset.name) ($($rel.tag_name))" -ForegroundColor Green
  }
}

# ---------- 2. permissão ----------
$me = [Security.Principal.WindowsIdentity]::GetCurrent()
$inGroup = $me.Groups | Where-Object { $_.Value -eq $PerfLogUsers }
if ($inGroup) {
  Write-Host "Você já tem a permissão. O FPS aparece quando abrir um jogo." -ForegroundColor Green
} else {
  Write-Host "Agora o Windows vai pedir confirmação pra dar a permissão de ler o FPS…"
  $argList = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -SoGrupo -Usuario `"$($me.Name)`""
  try {
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argList -Wait
    Write-Host ""
    Write-Host "Saia e entre de novo no Windows (ou reinicie) pra valer. Depois disso o FPS aparece sozinho no modo jogo." -ForegroundColor Cyan
  } catch {
    Write-Host "Cancelado. Sem a permissão o FPS não aparece (o resto do modo jogo funciona normal)." -ForegroundColor Yellow
  }
}
Write-Host ""
Read-Host "Enter pra fechar"
