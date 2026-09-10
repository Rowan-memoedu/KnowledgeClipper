Set-StrictMode -Version Latest
$candidateProcess = $null
$uiaRoot = $null
$pipeClient = $null
$ankiAdapter = $null
$sourceWindow = $null
$logWriter = $null
$settingsArgument = '--open-settings'
try {
    Assert-PreTaskWriteCountsZero
    $candidateProcess = Start-FakeChild $settingsArgument
}
finally {
    Close-OwnedResource $ankiAdapter
    Close-OwnedResource $pipeClient
    Close-OwnedResource $uiaRoot
    Close-OwnedResource $candidateProcess
}
