param([ValidateSet('List','Update')][string]$Action='List',[string]$Name,[string]$TargetVersion,[string]$ReviewedPath)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.Encoding]::UTF8
$modules=@(Get-InstalledModule -ErrorAction Stop)
$items=@()
foreach($module in $modules) {
    $location=[IO.Path]::GetFullPath($module.InstalledLocation)
    if($location.StartsWith($PSHOME,[StringComparison]::OrdinalIgnoreCase) -or $location.StartsWith($env:WINDIR,[StringComparison]::OrdinalIgnoreCase)){continue}
    if($Action -eq 'Update') {
        if($module.Name -eq $Name -and $location -eq $ReviewedPath){Update-Module -Name $Name -RequiredVersion $TargetVersion -ErrorAction Stop;exit 0}
        continue
    }
    try {
        $latest=Find-Module -Name $module.Name -Repository $module.Repository -ErrorAction Stop
        $target=$null
        if($latest.Version -gt $module.Version){$target=$latest.Version.ToString()}
        $items+=@{name=$module.Name;current=$module.Version.ToString();latest=$target;location=$location;repository=$module.Repository;error=$null}
    } catch {$items+=@{name=$module.Name;current=$module.Version.ToString();latest=$null;location=$location;repository=$module.Repository;error='Repository metadata could not be checked.'}}
}
if($Action -eq 'Update'){throw 'The reviewed module installation is no longer present.'}
ConvertTo-Json -Depth 5 -Compress -InputObject @{items=$items}
