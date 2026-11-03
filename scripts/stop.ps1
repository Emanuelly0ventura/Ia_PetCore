$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
docker compose --project-directory $root down
