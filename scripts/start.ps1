$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $root '.env'))) { Copy-Item (Join-Path $root '.env.example') (Join-Path $root '.env'); Write-Host 'Edite o arquivo .env e execute novamente.'; exit 1 }
docker compose --project-directory $root up --build -d
Write-Host 'Dashboard: http://localhost:8080'
Write-Host 'Swagger:   http://localhost:5080/swagger'
