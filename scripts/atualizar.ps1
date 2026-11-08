# =====================================================================
#  PainelDeck · atualizador (chamado pelo app: botão "Atualizar" do painel ou bandeja → Atualizar agora)
#  Roda escondido. Passos:
#   1. baixa o código do GitHub (repo/branch do settings.json, "update")
#   2. compila com dotnet publish numa pasta à parte  → se falhar, nada muda e o painel mostra o erro
#   3. avisa o app pra fechar ("instalando") e espera ele sair
#   4. faz backup da pasta do app (.atualizacao\backups, guarda os 5 últimos)
#   5. copia a versão nova por cima, SEM perder o que é seu:
#        - deck.json, settings.json, playtime.json e as capas em wwwroot\games nunca são substituídos
#        - arquivos do wwwroot\, scripts\ e CLAUDE.md que você (ou o ✨ Claude) mudou no PC ficam como estão;
#          se a versão nova também mudou o mesmo arquivo, ela fica ao lado como "arquivo.novo"
#   6. abre o PainelDeck de novo. Se algo der errado na cópia, volta o backup.
#  Progresso em .atualizacao\status.json, log em .atualizacao\atualizar.log
# =====================================================================
param(
  [Parameter(Mandatory = $true)][string]$Root,
  [string]$Repo = "RodrigoTavares81/paineldeck",
  [string]$Branch = "main",
  [int]$WaitPid = 0,
  [string]$Exe = ""
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"   # a barra de progresso do Invoke-WebRequest deixa o download muito lento
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$work = Join-Path $Root ".atualizacao"
$null = New-Item -ItemType Directory -Force -Path $work
$log = Join-Path $work "atualizar.log"
$statusFile = Join-Path $work "status.json"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$temp = Join-Path $env:TEMP "PainelDeck-atualizacao-$stamp"
if (-not $Exe) { $Exe = Join-Path $Root "PainelDeck.exe" }

function Log([string]$m) { Add-Content -Path $log -Value ("[{0}] {1}" -f (Get-Date -Format "HH:mm:ss"), $m) -Encoding UTF8 }
function Status([string]$step, [string]$message) {
  Log "$step - $message"
  $json = @{ step = $step; message = $message; at = (Get-Date).ToString("o") } | ConvertTo-Json -Compress
  [IO.File]::WriteAllText($statusFile, $json, (New-Object Text.UTF8Encoding $false))
}
function Fail([string]$message) {
  Status "erro" $message
  exit 1
}
function Rel([string]$base, [string]$full) { return $full.Substring($base.TrimEnd('\').Length + 1) }
function SameFile([string]$a, [string]$b) {
  if (-not (Test-Path -LiteralPath $a) -or -not (Test-Path -LiteralPath $b)) { return $false }
  if ((Get-Item -LiteralPath $a).Length -ne (Get-Item -LiteralPath $b).Length) { return $false }
  return (Get-FileHash -LiteralPath $a -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $b -Algorithm SHA256).Hash
}

# nunca substituir (são seus); só copiados se ainda não existirem
$keep = @("deck.json", "settings.json", "playtime.json", "version.json")
# arquivos que dá pra editar no PC: substitui só se você não mexeu (comparando com a cópia original da última atualização)
function IsEditable([string]$rel) { return $rel -like "wwwroot\*" -or $rel -like "scripts\*" -or $rel -eq "CLAUDE.md" }

Set-Content -Path $log -Value "" -Encoding UTF8
Log "Atualizando $Root a partir de $Repo ($Branch)"

try {
  # ---------- 1. baixar ----------
  Status "baixando" "Baixando a versão nova…"
  $sha = $null
  try {
    $info = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/commits/$Branch" -Headers @{ "User-Agent" = "PainelDeck" }
    $sha = $info.sha
  } catch { Log "não consegui ler o commit: $($_.Exception.Message)" }
  $ref = if ($sha) { $sha } else { $Branch }
  $null = New-Item -ItemType Directory -Force -Path $temp
  $zip = Join-Path $temp "codigo.zip"
  Invoke-WebRequest -Uri "https://codeload.github.com/$Repo/zip/$ref" -OutFile $zip -UseBasicParsing -Headers @{ "User-Agent" = "PainelDeck" }
  Expand-Archive -Path $zip -DestinationPath (Join-Path $temp "codigo") -Force
  $src = Get-ChildItem (Join-Path $temp "codigo") -Directory | Select-Object -First 1
  $csproj = Join-Path $src.FullName "PainelDeck.csproj"
  if (-not (Test-Path $csproj)) { Fail "O código baixado não tem o PainelDeck.csproj" }

  # ---------- 2. compilar ----------
  Status "compilando" "Compilando (uns 30 segundos)…"
  if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail "Não achei o dotnet (instale o .NET 8 SDK)" }
  $out = Join-Path $temp "publish"
  # no PowerShell 5.1, texto no stderr de um programa vira "erro" e o Stop pararia o script: aqui só o código de saída importa
  $ErrorActionPreference = "Continue"
  $build = & dotnet publish $csproj -c Release -o $out --nologo -v quiet 2>&1 | ForEach-Object { "$_" }
  $ErrorActionPreference = "Stop"
  $build | ForEach-Object { Log "  $_" }
  if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $out "PainelDeck.exe"))) {
    $first = ($build | Where-Object { $_ -match "error" } | Select-Object -First 1)
    Fail ("A compilação falhou, nada mudou. " + $(if ($first) { "$first" } else { "Veja .atualizacao\atualizar.log" }))
  }

  # ---------- 3. fechar o app ----------
  Status "instalando" "Instalando… o painel volta em instantes"
  if ($WaitPid -gt 0) {
    try { Wait-Process -Id $WaitPid -Timeout 30 -ErrorAction Stop } catch { }
    if (Get-Process -Id $WaitPid -ErrorAction SilentlyContinue) { Fail "O PainelDeck não fechou; tente de novo" }
  }
  Start-Sleep -Milliseconds 500
} catch {
  Fail ("Deu erro: " + $_.Exception.Message)
}

# ---------- 4. backup ----------
$backups = Join-Path $work "backups"
$null = New-Item -ItemType Directory -Force -Path $backups
$backup = Join-Path $backups "app-$stamp"
try {
  $null = New-Item -ItemType Directory -Force -Path $backup
  Get-ChildItem -LiteralPath $Root -Force | Where-Object { $_.Name -notin @(".atualizacao", "logs") } |
    ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $backup -Recurse -Force }
  Get-ChildItem $backups -Directory | Sort-Object Name -Descending | Select-Object -Skip 5 | Remove-Item -Recurse -Force
  Log "backup em $backup"
} catch {
  Log "backup falhou: $($_.Exception.Message)"
  Status "erro" "Não consegui fazer o backup, nada mudou"
  if (Test-Path $Exe) { Start-Process -FilePath $Exe -WorkingDirectory $Root }
  exit 1
}

# ---------- 5. instalar ----------
$base = Join-Path $work "original"   # como cada arquivo editável veio na última atualização
$kept = New-Object System.Collections.ArrayList
$conflicts = New-Object System.Collections.ArrayList
try {
  foreach ($f in Get-ChildItem -LiteralPath $out -Recurse -File -Force) {
    $rel = Rel $out $f.FullName
    $dest = Join-Path $Root $rel
    $null = New-Item -ItemType Directory -Force -Path (Split-Path $dest)

    if ($keep -contains $rel -or $rel -like "wwwroot\games\*") {
      if (-not (Test-Path -LiteralPath $dest)) { Copy-Item -LiteralPath $f.FullName -Destination $dest -Force }
      continue
    }
    if ((IsEditable $rel) -and (Test-Path -LiteralPath $dest) -and -not (SameFile $dest $f.FullName)) {
      $orig = Join-Path $base $rel
      $hasOrig = Test-Path -LiteralPath $orig
      $mineChanged = $hasOrig -and -not (SameFile $dest $orig)
      $theirsChanged = -not $hasOrig -or -not (SameFile $f.FullName $orig)
      if ($mineChanged -and -not $theirsChanged) { [void]$kept.Add($rel); continue }   # só você mudou: fica o seu
      if ($mineChanged -and $theirsChanged) {                                          # os dois mudaram: o novo fica ao lado
        Copy-Item -LiteralPath $f.FullName -Destination ($dest + ".novo") -Force
        [void]$conflicts.Add($rel)
        continue
      }
      # 1ª atualização (sem a cópia original): substitui, mas o seu fica salvo no backup
    }
    Copy-Item -LiteralPath $f.FullName -Destination $dest -Force
  }

  # guarda a cópia original desta versão pra próxima comparação
  if (Test-Path $base) { Remove-Item $base -Recurse -Force }
  foreach ($f in Get-ChildItem -LiteralPath $out -Recurse -File -Force) {
    $rel = Rel $out $f.FullName
    if (IsEditable $rel) {
      $d = Join-Path $base $rel
      $null = New-Item -ItemType Directory -Force -Path (Split-Path $d)
      Copy-Item -LiteralPath $f.FullName -Destination $d -Force
    }
  }

  $short = if ($sha) { $sha.Substring(0, 7) } else { $Branch }
  @{ sha = $sha; branch = $Branch; repo = $Repo; installedAt = (Get-Date).ToString("o") } | ConvertTo-Json |
    Set-Content -Path (Join-Path $Root "version.json") -Encoding UTF8
  @{ ok = $true; sha = $sha; message = "Atualizado pra versão $short"; kept = @($kept); conflicts = @($conflicts); backup = $backup } |
    ConvertTo-Json | Set-Content -Path (Join-Path $work "result.json") -Encoding UTF8
  Status "pronto" "Atualizado pra versão $short"
} catch {
  Log "instalação falhou: $($_.Exception.Message) — voltando o backup"
  try {
    Get-ChildItem -LiteralPath $backup -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $Root -Recurse -Force }
  } catch { Log "não consegui voltar o backup: $($_.Exception.Message)" }
  @{ ok = $false; message = "A atualização falhou e voltei a versão anterior: $($_.Exception.Message)" } |
    ConvertTo-Json | Set-Content -Path (Join-Path $work "result.json") -Encoding UTF8
  Status "erro" "A atualização falhou e voltei a versão anterior"
}

# ---------- 6. abrir de novo ----------
try { Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue } catch { }
if (Test-Path $Exe) { Start-Process -FilePath $Exe -WorkingDirectory $Root }
