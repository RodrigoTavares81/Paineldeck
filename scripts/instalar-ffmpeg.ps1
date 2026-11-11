# =====================================================================
#  PainelDeck · instalar o ffmpeg (rode UMA vez: botão direito → "Executar com o PowerShell")
#
#  O replay do OBS sempre salva o buffer inteiro (ex.: 5 minutos). Pra salvar só os últimos 30 s ou 1 min,
#  o painel corta o arquivo com o ffmpeg (cópia direta, sem recodificar: leva menos de 1 segundo).
#  Este script baixa o ffmpeg (build "essentials" do gyan.dev, o link oficial do ffmpeg.org pra Windows)
#  e deixa só o ffmpeg.exe na pasta tools\ do PainelDeck. Não instala nada no Windows nem mexe no PATH.
#  Sem o ffmpeg tudo funciona igual, só que o replay vem sempre com o buffer inteiro.
# =====================================================================
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = Split-Path -Parent $PSScriptRoot
$tools = Join-Path $root "tools"
$null = New-Item -ItemType Directory -Force -Path $tools
$dest = Join-Path $tools "ffmpeg.exe"

if (Test-Path $dest) {
  Write-Host "O ffmpeg já está em tools\ffmpeg.exe" -ForegroundColor Green
} else {
  $zip = Join-Path $env:TEMP "paineldeck-ffmpeg.zip"
  $tmp = Join-Path $env:TEMP "paineldeck-ffmpeg"
  try {
    Write-Host "Baixando o ffmpeg (uns 100 MB)..."
    Invoke-WebRequest "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip" -OutFile $zip -UseBasicParsing
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    Expand-Archive $zip -DestinationPath $tmp -Force
    $exe = Get-ChildItem $tmp -Recurse -Filter "ffmpeg.exe" | Select-Object -First 1
    if (-not $exe) { throw "não achei o ffmpeg.exe dentro do zip" }
    Copy-Item $exe.FullName $dest -Force
    Write-Host "Pronto: tools\ffmpeg.exe. O painel já passa a cortar os replays (não precisa reiniciar)." -ForegroundColor Green
  } catch {
    Write-Host "Não deu: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "Dá pra fazer na mão: baixe o ffmpeg em ffmpeg.org/download.html e ponha o ffmpeg.exe em $tools" -ForegroundColor Yellow
  } finally {
    Remove-Item $zip -Force -ErrorAction SilentlyContinue
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
  }
}
Write-Host ""
Read-Host "Enter pra fechar"
