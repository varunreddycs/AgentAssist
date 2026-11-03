<#
.SYNOPSIS
Creates the maps-tools toolbox in the Foundry project of the active azd environment.

.DESCRIPTION
Reads AZURE_AI_PROJECT_ID and FOUNDRY_PROJECT_ENDPOINT from `azd env get-values`, fills the
${AZURE_AI_PROJECT_ID} placeholder in toolbox/maps.toolbox.yaml, runs `azd ai toolbox create`,
then sets TOOLBOX_NAME in the azd environment so `azd deploy` passes it to the container.
Works in Windows PowerShell 5.1 and PowerShell 7.

.PARAMETER Name
Toolbox name (default maps-tools).

.PARAMETER Replace
Delete an existing toolbox of that name first (toolbox versions are immutable).

.PARAMETER RenderOnly
Only write toolbox/.rendered/maps.toolbox.yaml and print its path.
#>
param(
    [string]$Name = "maps-tools",
    [switch]$Replace,
    [switch]$RenderOnly
)

$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

$values = @{}
azd env get-values | ForEach-Object {
    if ($_ -match '^([^=]+)="(.*)"$') { $values[$Matches[1]] = $Matches[2] }
}
$projectId = $values["AZURE_AI_PROJECT_ID"]
$endpoint = $values["FOUNDRY_PROJECT_ENDPOINT"]
if (-not $projectId -or -not $endpoint) {
    throw "Set AZURE_AI_PROJECT_ID and FOUNDRY_PROJECT_ENDPOINT in the azd environment first (azd env set ...)."
}

New-Item -ItemType Directory -Force -Path "toolbox/.rendered" | Out-Null
$rendered = Join-Path (Get-Location) "toolbox/.rendered/maps.toolbox.yaml"
$template = Get-Content -Path "toolbox/maps.toolbox.yaml" -Raw
# UTF-8 without a byte order mark: azd rejects YAML that starts with one.
[System.IO.File]::WriteAllText($rendered, $template.Replace('${AZURE_AI_PROJECT_ID}', $projectId), (New-Object System.Text.UTF8Encoding $false))
Write-Host "Rendered $rendered for $projectId"
if ($RenderOnly) { exit 0 }

if ($Replace) {
    azd ai toolbox delete $Name --force --project-endpoint $endpoint --no-prompt
}
azd ai toolbox create $Name --from-file $rendered --project-endpoint $endpoint --no-prompt
if ($LASTEXITCODE -ne 0) { throw "azd ai toolbox create failed with exit code $LASTEXITCODE" }
azd env set TOOLBOX_NAME $Name
if ($LASTEXITCODE -ne 0) { throw "azd env set failed with exit code $LASTEXITCODE" }
Write-Host "TOOLBOX_NAME=$Name set in the azd environment; run azd deploy next."
