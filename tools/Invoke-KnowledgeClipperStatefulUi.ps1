param(
    [Parameter(Mandatory = $true)][int]$ProcessId,
    [Parameter(Mandatory = $true)][string]$ScenarioPath,
    [Parameter(Mandatory = $true)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

function Get-ProcessTopLevelWindows {
    param([int]$Id, [DateTime]$Deadline)
    $result = New-Object System.Collections.Generic.List[System.Windows.Automation.AutomationElement]
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $item = $walker.GetFirstChild([System.Windows.Automation.AutomationElement]::RootElement)
    while ($null -ne $item -and [DateTime]::UtcNow -lt $Deadline) {
        try {
            if ($item.Current.ProcessId -eq $Id) { $result.Add($item) }
            $item = $walker.GetNextSibling($item)
        }
        catch [System.Windows.Automation.ElementNotAvailableException] {
            $item = $walker.GetFirstChild([System.Windows.Automation.AutomationElement]::RootElement)
        }
    }
    return $result
}

function Find-Element {
    param(
        [int]$Id,
        [string]$AutomationId,
        [int]$TimeoutMs = 15000,
        [int]$MaxNodesPerPass = 4096
    )
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    do {
        foreach ($window in (Get-ProcessTopLevelWindows -Id $Id -Deadline $deadline)) {
            $queue = New-Object 'System.Collections.Generic.Queue[System.Windows.Automation.AutomationElement]'
            $queue.Enqueue($window)
            $visited = 0
            while ($queue.Count -gt 0 -and $visited -lt $MaxNodesPerPass -and [DateTime]::UtcNow -lt $deadline) {
                $item = $queue.Dequeue()
                $visited += 1
                try {
                    if ($item.Current.ProcessId -eq $Id -and $item.Current.AutomationId -eq $AutomationId) { return $item }
                    $child = $walker.GetFirstChild($item)
                    while ($null -ne $child -and $queue.Count -lt $MaxNodesPerPass -and [DateTime]::UtcNow -lt $deadline) {
                        try {
                            if ($child.Current.ProcessId -eq $Id) { $queue.Enqueue($child) }
                            $child = $walker.GetNextSibling($child)
                        }
                        catch [System.Windows.Automation.ElementNotAvailableException] {
                            $child = $null
                        }
                    }
                }
                catch [System.Windows.Automation.ElementNotAvailableException] {
                    continue
                }
            }
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "ui.element-not-found:$AutomationId"
}

function Get-Root {
    param([int]$Id, [int]$TimeoutMs = 30000)
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
    do {
        $roots = @(Get-ProcessTopLevelWindows -Id $Id -Deadline $deadline)
        if ($roots.Count -gt 0) { return $roots[0] }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "ui.process-window-not-found:$Id"
}

function Read-Value {
    param([System.Windows.Automation.AutomationElement]$Element)
    $pattern = $null
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) { return $pattern.Current.Value }
    $values = New-Object System.Collections.Generic.List[string]
    $legacy = $null
    $legacyPattern = [System.Windows.Automation.AutomationPattern]::LookupById(10018)
    if ($null -ne $legacyPattern -and $Element.TryGetCurrentPattern($legacyPattern, [ref]$legacy)) {
        if (-not [string]::IsNullOrWhiteSpace([string]$legacy.Current.Value)) { $values.Add([string]$legacy.Current.Value) }
        if (-not [string]::IsNullOrWhiteSpace([string]$legacy.Current.Name)) { $values.Add([string]$legacy.Current.Name) }
    }
    if (-not [string]::IsNullOrWhiteSpace([string]$Element.Current.HelpText)) { $values.Add([string]$Element.Current.HelpText) }
    if (-not [string]::IsNullOrWhiteSpace([string]$Element.Current.Name)) { $values.Add([string]$Element.Current.Name) }
    return (($values | Select-Object -Unique) -join ' | ')
}

$scenario = Get-Content -LiteralPath $ScenarioPath -Raw -Encoding UTF8 | ConvertFrom-Json
$events = New-Object System.Collections.Generic.List[object]
$status = 'passed'
$failure = $null
$failureObserved = $null

try {
    $root = Get-Root -Id $ProcessId -TimeoutMs ([int]($scenario.windowTimeoutMs ?? 30000))
    foreach ($step in $scenario.steps) {
        $started = [DateTime]::UtcNow.ToString('o')
        $operation = [string]$step.operation
        $element = $null
        if ($step.automationId) { $element = Find-Element -Id $ProcessId -AutomationId ([string]$step.automationId) -TimeoutMs ([int]($step.timeoutMs ?? 15000)) }
        switch ($operation) {
            'wait' { }
            'focus' { $element.SetFocus() }
            'invoke' {
                $pattern = $null
                if (-not $element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { throw "ui.invoke-pattern-missing:$($step.automationId)" }
                $pattern.Invoke()
            }
            'set-value' {
                $pattern = $null
                if (-not $element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) { throw "ui.value-pattern-missing:$($step.automationId)" }
                $pattern.SetValue([string]$step.value)
            }
            'select' {
                $pattern = $null
                if (-not $element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { throw "ui.selection-pattern-missing:$($step.automationId)" }
                $pattern.Select()
            }
            'select-item' {
                $itemName = [string]$step.itemName
                if ([string]::IsNullOrWhiteSpace($itemName)) { throw "ui.selection-item-name-missing:$($step.automationId)" }
                $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $itemName)
                $choice = $element.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
                if ($null -eq $choice) { throw "ui.selection-item-not-found:$($step.automationId):$itemName" }
                $pattern = $null
                if (-not $choice.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { throw "ui.selection-item-pattern-missing:$($step.automationId):$itemName" }
                $pattern.Select()
            }
            'toggle' {
                $pattern = $null
                if (-not $element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) { throw "ui.toggle-pattern-missing:$($step.automationId)" }
                $pattern.Toggle()
            }
            'send-keys' {
                $element.SetFocus()
                [System.Windows.Forms.SendKeys]::SendWait([string]$step.keys)
            }
            'assert-value' {
                $assertDeadline = [DateTime]::UtcNow.AddMilliseconds([int]($step.timeoutMs ?? 15000))
                $matched = $false
                do {
                    $value = Read-Value -Element $element
                    if ($value -eq [string]$step.expected) { $matched = $true; break }
                    Start-Sleep -Milliseconds 100
                    try { $element = Find-Element -Id $ProcessId -AutomationId ([string]$step.automationId) -TimeoutMs 500 } catch { }
                } while ([DateTime]::UtcNow -lt $assertDeadline)
                if (-not $matched) { throw "ui.value-mismatch:$($step.automationId)" }
            }
            'assert-contains' {
                $assertDeadline = [DateTime]::UtcNow.AddMilliseconds([int]($step.timeoutMs ?? 15000))
                $matched = $false
                do {
                    $value = Read-Value -Element $element
                    if ($value.Contains([string]$step.expected)) { $matched = $true; break }
                    Start-Sleep -Milliseconds 100
                    try { $element = Find-Element -Id $ProcessId -AutomationId ([string]$step.automationId) -TimeoutMs 500 } catch { }
                } while ([DateTime]::UtcNow -lt $assertDeadline)
                if (-not $matched) { throw "ui.value-missing:$($step.automationId)" }
            }
            'close-window' {
                $pattern = $null
                if (-not $root.TryGetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern, [ref]$pattern)) { throw 'ui.window-pattern-missing' }
                $pattern.Close()
            }
            default { throw "ui.operation-unsupported:$operation" }
        }
        if ($step.waitAfterMs) { Start-Sleep -Milliseconds ([int]$step.waitAfterMs) }
        $observed = if ($null -ne $element -and -not $step.redact) { Read-Value -Element $element } else { $null }
        $events.Add([ordered]@{ id = [string]$step.id; operation = $operation; automationId = [string]$step.automationId; startedAt = $started; completedAt = [DateTime]::UtcNow.ToString('o'); observed = $observed; source = 'windows-ui-automation-production-process' })
        if ($operation -ne 'close-window') { $root = Get-Root -Id $ProcessId -TimeoutMs 15000 }
    }
}
catch {
    $status = 'failed'
    $failure = $_.Exception.Message
    if ($null -ne $element -and -not $step.redact) {
        try { $failureObserved = Read-Value -Element $element } catch { }
    }
}

$receipt = [ordered]@{
    schemaVersion = 'knowledge-clipper-native-ui-driver-receipt.v1'
    status = $status
    processId = $ProcessId
    scenarioPath = [System.IO.Path]::GetFullPath($ScenarioPath)
    driver = 'System.Windows.Automation + SendKeys'
    injectedHost = $false
    sharedTestCoreDll = $false
    manualDisplayState = $false
    events = $events
    failure = $failure
    failureObserved = $failureObserved
}
$parent = Split-Path -Parent $OutputPath
if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
if (Test-Path -LiteralPath $OutputPath) { throw "append-only output exists: $OutputPath" }
[System.IO.File]::WriteAllText([System.IO.Path]::GetFullPath($OutputPath), ($receipt | ConvertTo-Json -Depth 12), [System.Text.UTF8Encoding]::new($false))
if ($status -ne 'passed') { exit 1 }
