$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-CodexPropertyValue {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [object]$InputObject,

        [Parameter(Mandatory)]
        [string]$Name
    )

    if ($null -eq $InputObject) {
        return $null
    }

    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function ConvertTo-CodexCyberRecord {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]$Event,

        [string]$SourcePath = '',

        [switch]$IncludeNormalCompletion
    )

    if ((Get-CodexPropertyValue -InputObject $Event -Name 'type') -ne 'event_msg') {
        return
    }

    $payload = Get-CodexPropertyValue -InputObject $Event -Name 'payload'
    if ($null -eq $payload) {
        return
    }

    $payloadType = [string](Get-CodexPropertyValue -InputObject $payload -Name 'type')
    $timestampValue = Get-CodexPropertyValue -InputObject $Event -Name 'timestamp'
    $timestamp = if ($timestampValue -is [DateTime]) {
        $timestampValue.ToString('o')
    }
    elseif ($timestampValue -is [DateTimeOffset]) {
        $timestampValue.ToString('o')
    }
    else {
        [string]$timestampValue
    }
    $turnId = [string](Get-CodexPropertyValue -InputObject $payload -Name 'turn_id')
    $result = $null
    $detail = $null
    $isCyber = $false

    switch ($payloadType) {
        'task_complete' {
            $errorObject = Get-CodexPropertyValue -InputObject $payload -Name 'error'
            $errorInfo = [string](Get-CodexPropertyValue -InputObject $errorObject -Name 'codex_error_info')

            if ($errorInfo -eq 'cyber_policy') {
                $result = 'CYBER_BLOCK'
                $detail = 'codex_error_info=cyber_policy'
                $isCyber = $true
            }
            elseif ($IncludeNormalCompletion) {
                if ([string]::IsNullOrWhiteSpace($errorInfo)) {
                    $result = 'NO_RECORDED_CYBER_POLICY'
                    $detail = 'task_complete 未记录 cyber_policy'
                }
                else {
                    $result = 'OTHER_ERROR'
                    $detail = "codex_error_info=$errorInfo"
                }
            }
        }

        'model_reroute' {
            $reason = [string](Get-CodexPropertyValue -InputObject $payload -Name 'reason')
            if ($reason -eq 'high_risk_cyber_activity') {
                $result = 'CYBER_REROUTE'
                $detail = 'reason=high_risk_cyber_activity'
                $isCyber = $true
            }
        }

        'model_verification' {
            $verifications = @(Get-CodexPropertyValue -InputObject $payload -Name 'verifications')
            if ($verifications -contains 'trusted_access_for_cyber') {
                $result = 'CYBER_VERIFICATION'
                $detail = 'verification=trusted_access_for_cyber'
                $isCyber = $true
            }
        }

        'safety_buffering' {
            $useCases = @(Get-CodexPropertyValue -InputObject $payload -Name 'use_cases')
            if ($useCases -contains 'cyber') {
                $reasons = @(Get-CodexPropertyValue -InputObject $payload -Name 'reasons')
                $result = 'CYBER_BUFFERING'
                $detail = if ($reasons.Count -gt 0) {
                    'reasons=' + ($reasons -join ',')
                }
                else {
                    'use_cases=cyber'
                }
                $isCyber = $true
            }
        }
    }

    if ($null -eq $result) {
        return
    }

    [pscustomobject]@{
        Timestamp  = $timestamp
        TurnId     = $turnId
        Result     = $result
        Detail     = $detail
        IsCyber    = $isCyber
        SourcePath = $SourcePath
    }
}
