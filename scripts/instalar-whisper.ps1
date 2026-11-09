# =====================================================================
#  PainelDeck · instalar o Whisper (rode UMA vez: botão direito → "Executar com o PowerShell")
#
#  O Whisper transforma os seus áudios em texto pras sessões do Claude, 100% no PC (nada vai pra internet).
#  É o whisper.cpp (open source: github.com/ggml-org/whisper.cpp). Este script:
#   1. baixa o programa (whisper-cli.exe) pra pasta whisper\ do PainelDeck
#   2. baixa o modelo (padrão: large-v3-turbo q5, ~550 MB, ótimo em português)
#  Não precisa reiniciar o PainelDeck: o próximo áudio já usa.
#
#  Opções:
#   -Gpu              usa a versão com CUDA (placa NVIDIA: bem mais rápido; se der erro, rode sem -Gpu)
#   -Modelo small     modelo menor/mais rápido (base, small, medium, large-v3-turbo-q5_0, large-v3-turbo)
# =====================================================================
param([switch]$Gpu, [string]$Modelo = "large-v3-turbo-q5_0")

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = Split-Path -Parent $PSScriptRoot
$dir = Join-Path $root "whisper"
$bin = Join-Path $dir "bin"
$models = Join-Path $dir "models"
$null = New-Item -ItemType Directory -Force -Path $bin, $models

try {
  # ---------- 1. programa ----------
  $exe = Get-ChildItem $bin -Recurse -Filter "whisper-cli.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
  if ($exe -and -not $Gpu) {
    Write-Host "Whisper já está em whisper\bin: $($exe.FullName)"
  } else {
    Write-Host "Procurando a última versão do whisper.cpp..."
    $rel = Invoke-RestMethod "https://api.github.com/repos/ggml-org/whisper.cpp/releases/latest" -Headers @{ "User-Agent" = "PainelDeck" }
    $asset = if ($Gpu) {
      $rel.assets | Where-Object { $_.name -match '^whisper-cublas-12.*-bin-x64\.zip$' } | Sort-Object name -Descending | Select-Object -First 1
    } else {
      $rel.assets | Where-Object { $_.name -eq 'whisper-bin-x64.zip' } | Select-Object -First 1
    }
    if (-not $asset) { throw "Não achei o arquivo certo na versão $($rel.tag_name) do whisper.cpp." }
    $zip = Join-Path $env:TEMP $asset.name
    Write-Host "Baixando $($asset.name) ($([math]::Round($asset.size / 1MB)) MB)..."
    Invoke-WebRequest $asset.browser_download_url -OutFile $zip
    if (Test-Path $bin) { Remove-Item $bin -Recurse -Force }
    Expand-Archive $zip -DestinationPath $bin -Force
    Remove-Item $zip -Force
    $exe = Get-ChildItem $bin -Recurse -Filter "whisper-cli.exe" | Select-Object -First 1
    if (-not $exe) { throw "O pacote não veio com whisper-cli.exe." }
    Write-Host "Programa pronto: $($exe.FullName)" -ForegroundColor Green
  }

  # ---------- 2. modelo ----------
  $file = Join-Path $models "ggml-$Modelo.bin"
  if (Test-Path $file) {
    Write-Host "Modelo já baixado: ggml-$Modelo.bin"
  } else {
    Write-Host "Baixando o modelo ggml-$Modelo.bin (pode levar uns minutos)..."
    $tmp = "$file.part"
    Invoke-WebRequest "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-$Modelo.bin" -OutFile $tmp
    Move-Item $tmp $file -Force
    Write-Host "Modelo pronto." -ForegroundColor Green
  }
  # o painel usa o maior modelo da pasta: se tiver outro maior sobrando, avisa
  $other = Get-ChildItem $models -Filter "ggml-*.bin" | Where-Object { $_.FullName -ne $file -and $_.Length -gt (Get-Item $file).Length }
  if ($other) { Write-Host "Atenção: o painel usa o MAIOR modelo da pasta ($($other[0].Name)). Apague os que não quiser." -ForegroundColor Yellow }

  Write-Host ""
  Write-Host "Tudo certo! Toque no microfone de uma sessão do Claude no painel e fale." -ForegroundColor Green
} catch {
  Write-Host "Deu erro: $($_.Exception.Message)" -ForegroundColor Red
}
Start-Sleep -Seconds 4
