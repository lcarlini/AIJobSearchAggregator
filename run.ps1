$Host.UI.RawUI.WindowTitle = "Vagas"
chcp 65001 > $null
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Set-Location $PSScriptRoot
Write-Host "Subindo a busca de vagas em http://localhost:5088"
dotnet run --project "$PSScriptRoot\AIJobSearchAggregator.csproj" --urls "http://localhost:5088"
