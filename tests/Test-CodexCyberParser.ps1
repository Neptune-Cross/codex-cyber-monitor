$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path (Split-Path -Parent $PSScriptRoot) 'CodexCyber.Common.ps1')

$fixtures = @(
    @{
        Name = '用户文本不误报'
        Json = '{"timestamp":"2026-08-26T00:00:00Z","type":"response_item","payload":{"type":"message","content":"codex_error_info: cyber_policy"}}'
        Expected = $null
    },
    @{
        Name = 'Cyber 阻断'
        Json = '{"timestamp":"2026-08-26T00:00:01Z","type":"event_msg","payload":{"type":"task_complete","turn_id":"turn-block","error":{"message":"redacted","codex_error_info":"cyber_policy"}}}'
        Expected = 'CYBER_BLOCK'
    },
    @{
        Name = '普通完成'
        Json = '{"timestamp":"2026-08-26T00:00:02Z","type":"event_msg","payload":{"type":"task_complete","turn_id":"turn-ok","error":null}}'
        Expected = 'NO_RECORDED_CYBER_POLICY'
    },
    @{
        Name = '其他错误'
        Json = '{"timestamp":"2026-08-26T00:00:03Z","type":"event_msg","payload":{"type":"task_complete","turn_id":"turn-other","error":{"message":"redacted","codex_error_info":"context_window_exceeded"}}}'
        Expected = 'OTHER_ERROR'
    },
    @{
        Name = 'Cyber 改路由'
        Json = '{"timestamp":"2026-08-26T00:00:04Z","type":"event_msg","payload":{"type":"model_reroute","turn_id":"turn-route","reason":"high_risk_cyber_activity"}}'
        Expected = 'CYBER_REROUTE'
    },
    @{
        Name = 'Cyber 验证'
        Json = '{"timestamp":"2026-08-26T00:00:05Z","type":"event_msg","payload":{"type":"model_verification","turn_id":"turn-verify","verifications":["trusted_access_for_cyber"]}}'
        Expected = 'CYBER_VERIFICATION'
    },
    @{
        Name = 'Cyber 等待检查'
        Json = '{"timestamp":"2026-08-26T00:00:06Z","type":"event_msg","payload":{"type":"safety_buffering","turn_id":"turn-buffer","use_cases":["cyber"],"reasons":["user_risk"]}}'
        Expected = 'CYBER_BUFFERING'
    }
)

foreach ($fixture in $fixtures) {
    $event = $fixture.Json | ConvertFrom-Json -Depth 100 -ErrorAction Stop
    $record = ConvertTo-CodexCyberRecord -Event $event -SourcePath 'fixture.jsonl' -IncludeNormalCompletion
    $actual = if ($null -eq $record) { $null } else { $record.Result }

    if ($actual -ne $fixture.Expected) {
        throw "测试失败：$($fixture.Name)，期望=$($fixture.Expected)，实际=$actual"
    }

    Write-Host "通过：$($fixture.Name)" -ForegroundColor Green
}

Write-Host "全部测试通过：$($fixtures.Count) 项。" -ForegroundColor Cyan

