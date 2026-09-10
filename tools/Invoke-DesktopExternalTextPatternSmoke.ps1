param([Parameter(Mandatory=$true)][string]$CandidateRoot,[Parameter(Mandatory=$true)][string]$DataRoot)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Windows.Forms,System.Drawing,UIAutomationClient,UIAutomationTypes
[IO.Directory]::CreateDirectory($DataRoot)|Out-Null
$contracts=Get-ChildItem -LiteralPath (Join-Path $CandidateRoot 'contracts\desktop-automation') -Filter 'desktop-automation.contract.json' -Recurse | Sort-Object FullName
$controls=@{}
foreach($file in $contracts){$contract=Get-Content -Raw -LiteralPath $file.FullName|ConvertFrom-Json;foreach($control in @($contract.automationIds)){if($control.semanticAction){$controls[[string]$control.semanticAction]=[string]$control.automationId}}}
foreach($action in @('mode.context.set','read.capture','clip.capture')){if(-not $controls.ContainsKey($action)){throw "automation-contract-action-missing:$action"}}
if($controls.Values -contains 'knowledge-clipper.desktop.context-mode'){throw 'automation-contract-legacy-wrong-id-present'}
$cardContractActions=@('card-resolution.accept-candidate','card-resolution.submit-correction','card-resolution.cancel')
foreach($action in $cardContractActions){if(-not $controls.ContainsKey($action)){throw "automation-contract-action-missing:$action"}}
$rootRoles=@($contracts|ForEach-Object {$value=Get-Content -Raw -LiteralPath $_.FullName|ConvertFrom-Json;if($value.controlRoles -and $value.controlRoles.floatingRoot){[string]$value.controlRoles.floatingRoot}})
if($rootRoles.Count -ne 1){throw 'automation-contract-floating-root-not-unique'}
$floatingRootId=$rootRoles[0]
$hostForm=[Windows.Forms.Form]@{Text='KnowledgeClipper TextPattern Canary Host';Width=720;Height=240;StartPosition='CenterScreen'}
$text=[Windows.Forms.TextBox]@{Multiline=$true;Dock='Fill';Text='The reviewer selected deliberate from this complete sentence. A second sentence remains outside the selection.';Name='external-context-source'}
$hostForm.Controls.Add($text);$hostForm.Show();$hostForm.Activate();$start=$text.Text.IndexOf('deliberate');$text.Focus();$text.Select($start,'deliberate'.Length)
$psi=[Diagnostics.ProcessStartInfo]::new((Join-Path $CandidateRoot 'KnowledgeClipper.exe'));$psi.UseShellExecute=$false;$psi.Environment['LOCALAPPDATA']=$DataRoot
$process=[Diagnostics.Process]::Start($psi)
try{
  $desktop=[Windows.Automation.AutomationElement]::RootElement;$window=$null
  for($i=0;$i-lt 100 -and $null-eq $window;$i++){
    [Windows.Forms.Application]::DoEvents();Start-Sleep -Milliseconds 100
    $processWindows=@($desktop.FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$process.Id)))
    $matching=@($processWindows|Where-Object {$_.Current.AutomationId -eq $floatingRootId})
    if($matching.Count -gt 1){throw 'actual-ui-floating-root-ambiguous'}
    if($matching.Count -eq 1){$window=$matching[0]}
  }
  if($null-eq $window){throw 'actual-ui-floating-root-not-found'}
  $context=$null;$read=$null
  for($ready=0;$ready-lt 100 -and ($null-eq $context -or $null-eq $read);$ready++){Start-Sleep -Milliseconds 100;$context=$window.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$controls['mode.context.set']));$read=$window.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$controls['read.capture']))}
  if($null-eq $context -or $null-eq $read){throw 'actual-ui-semantic-controls-missing'}
  $context.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke();Start-Sleep -Milliseconds 300
  $hostForm.Activate();$text.Focus();$text.Select($start,'deliberate'.Length);Start-Sleep -Milliseconds 200
  $read.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Seconds 8;[Windows.Forms.Application]::DoEvents()
  $status=$window.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'knowledge-clipper.desktop.status'))
  $value=if($status){$status.Current.Name}else{'status-missing'}
  [ordered]@{status='observed';candidate=$CandidateRoot;processId=$process.Id;selectedText='deliberate';source='external-winforms-textpattern';visibleStatus=$value;dataRoot=$DataRoot}|ConvertTo-Json -Compress
  if($value -match '失败|错误|required|unavailable'){exit 2}
}finally{if(-not $process.HasExited){$process.CloseMainWindow()|Out-Null;$process.WaitForExit(3000)|Out-Null};$hostForm.Close();$hostForm.Dispose()}
