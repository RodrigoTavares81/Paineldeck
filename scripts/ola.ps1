# Exemplo de script chamado pelo painel: { type: "script", file: "ola.ps1" }
Add-Type -AssemblyName PresentationFramework
[System.Windows.MessageBox]::Show("Olá do PainelDeck! Faltam poucos dias pras férias 🌴", "PainelDeck") | Out-Null
