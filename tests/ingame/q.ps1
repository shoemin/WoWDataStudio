# q.ps1 -- read-only query helper for the in-game visual test (uses the decomp harness's Q function and credentials).
#   .\q.ps1 mangos "select ..."
param([Parameter(Mandatory)][string]$Database, [Parameter(Mandatory)][string]$Sql)
. "<your WoW Custom Client folder>\WoWDecomp\tools\lib_wowtest.ps1"
if ($Sql -notmatch '^\s*(select|show)\b') { throw 'q.ps1 is read-only (select/show)' }
Q $Database $Sql
