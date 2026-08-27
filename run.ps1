# Development run helper for the agent.
# Usage:  .\run.ps1              interactive agent
#         .\run.ps1 eval         eval suite -> eval_output.txt
#         .\run.ps1 script FILE  scripted session (transcript.txt)
#         .\run.ps1 selftest     tool selftests (no API key needed)
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Args
)

$project = $PSScriptRoot
if (-not $Args) { $Args = @() }

Set-Location $project
dotnet build -v q --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --no-build -- @Args
exit $LASTEXITCODE
