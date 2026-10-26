#requires -Version 5.1
<#
.SYNOPSIS
    Sends one agent event to MiMi over its local named-pipe bridge (mimi-agent-bridge-<username>).

.DESCRIPTION
    Simulates what a real agent adapter (Codex CLI, Claude Code hooks, ...) does in Phase 3:
    connects to MiMi's bridge pipe, authenticates with the shared token from
    %LOCALAPPDATA%\MiMi\bridge-token, and pushes a single normalized AgentEvent.

    Wire protocol (must match MiMi.Bridge.BridgeProtocol):
      - each frame is a 4-byte little-endian Int32 payload length followed by UTF-8 JSON bytes;
      - the FIRST frame must be {"kind":"hello","token":"...","client":"..."} and is acked;
      - subsequent {"kind":"event","event":{...}} frames are each acked with
        {"kind":"ack","ok":true} or {"kind":"ack","ok":false,"error":"..."};
      - MiMi pushes {"kind":"approval_resolved",...} frames back over the same pipe when the
        user decides an approval that arrived from this client (use -Listen to observe them).

    Exit code 0 = event accepted (ok:true), 1 = rejected or transport failure.
    A duplicate event_id is acked with ok:true and error "duplicate".

.PARAMETER EventType
    One of: agent.started, agent.status, agent.tool_started, agent.approval_requested,
    agent.approval_resolved, agent.completed, agent.failed, agent.cancelled.

.PARAMETER Provider
    Source label shown in the UI, e.g. claude-code, codex.

.PARAMETER SessionId
    Agent session id used to correlate events; defaults to a fresh "ps-xxxxxxxx" value.

.PARAMETER Project
    Optional project name shown on cards.

.PARAMETER Title
    Card title. When empty, MiMi derives one from the event type.

.PARAMETER Message
    Card body text (display-only; MiMi never executes any payload content).

.PARAMETER ApprovalRequestId
    Required for agent.approval_requested; auto-generated when omitted. The same id is what
    MiMi echoes back in the approval_resolved frame.

.PARAMETER Metadata
    Optional hashtable of string -> string extras. Useful keys for approvals:
    action_kind (e.g. "Execute Command") and command (e.g. "npm test").

.PARAMETER PipeName
    Optional pipe name override; default is "mimi-agent-bridge-" + $env:USERNAME.

.PARAMETER Listen
    After the ack, keep the pipe open for up to 120 seconds and print every frame MiMi sends
    back (e.g. the approval_resolved push when the user clicks Approve or Deny).

.EXAMPLE
    PS> .\tools\Invoke-MiMiEvent.ps1 -EventType agent.status -Provider claude-code `
        -Project "MiMi" -Title "Refactoring bridge" -Message "Step 3 of 5"

    Sends a simple status event; the island shows a transient card.

.EXAMPLE
    PS> .\tools\Invoke-MiMiEvent.ps1 -EventType agent.approval_requested -Provider codex `
        -Project "web" -Title "Run test suite" -Message "npm test" `
        -Metadata @{ action_kind = "Execute Command"; command = "npm test" }

    Sends an approval request; MiMi shows a sticky approval card with Approve/Deny.

.EXAMPLE
    PS> .\tools\Invoke-MiMiEvent.ps1 -EventType agent.approval_requested -Provider claude-code `
        -Title "Edit app.py" -Message "About to modify 2 files" -Listen

    Sends an approval request and keeps listening for up to 120 s so you can see the
    approval_resolved frame MiMi pushes back after you click Approve or Deny.

.NOTES
    PowerShell 5.1 compatible. Windows-only (named pipes). The bridge must be enabled in
    MiMi settings and MiMi must be running.
#>
[CmdletBinding()]
param(
    [ValidateSet(
        'agent.started', 'agent.status', 'agent.tool_started', 'agent.approval_requested',
        'agent.approval_resolved', 'agent.completed', 'agent.failed', 'agent.cancelled'
    )]
    [string]$EventType = "agent.status",

    [string]$Provider = "claude-code",

    [string]$SessionId = ("ps-" + [guid]::NewGuid().ToString("N").Substring(0, 8)),

    [string]$Project = "",

    [string]$Title = "",

    [string]$Message = "",

    [string]$ApprovalRequestId = "",

    [hashtable]$Metadata = @{},

    [string]$PipeName = "",

    [switch]$Listen
)

$ErrorActionPreference = "Stop"
$MaxFrameBytes = 64 * 1024

# ---- helpers -----------------------------------------------------------------

function Fail([string]$Message) {
    Write-Host "ERROR: $Message" -ForegroundColor Red
    exit 1
}

function Read-Exact {
    # Reads exactly $Count bytes with a per-call timeout. Returns the byte array,
    # or $null on a clean EOF at a frame boundary. Throws TimeoutException on timeout.
    param([System.IO.Stream]$Stream, [int]$Count, [int]$TimeoutMs)

    $buffer = New-Object byte[] $Count
    $read = 0
    while ($read -lt $Count) {
        $task = $Stream.ReadAsync($buffer, $read, $Count - $read)
        if (-not $task.Wait($TimeoutMs)) {
            throw [System.TimeoutException]::new("Timed out after ${TimeoutMs} ms reading from the pipe.")
        }
        # .Result rethrows the underlying exception wrapped in AggregateException on failure.
        $n = $task.Result
        if ($n -le 0) {
            if ($read -eq 0) { return $null }   # clean EOF between frames
            throw (New-Object System.IO.IOException "Connection closed inside a frame body.")
        }
        $read += $n
    }
    return ,$buffer
}

function Read-MiMiFrame {
    # Reads one 4-byte little-endian length prefix + UTF-8 JSON payload.
    param([System.IO.Pipes.NamedPipeClientStream]$Stream, [int]$TimeoutMs = 5000)

    $header = Read-Exact -Stream $Stream -Count 4 -TimeoutMs $TimeoutMs
    if ($null -eq $header) { return $null }

    $length = [BitConverter]::ToInt32($header, 0)
    if ($length -lt 0 -or $length -gt $MaxFrameBytes) {
        throw (New-Object System.IO.IOException "Frame too large: $length bytes (limit $MaxFrameBytes).")
    }
    if ($length -eq 0) { return "" }

    $body = Read-Exact -Stream $Stream -Count $length -TimeoutMs $TimeoutMs
    return [System.Text.Encoding]::UTF8.GetString($body)
}

function Write-MiMiFrame {
    # Writes one 4-byte little-endian length prefix + UTF-8 JSON payload.
    param([System.IO.Stream]$Stream, [string]$Json)

    $payload = [System.Text.Encoding]::UTF8.GetBytes($Json)
    if ($payload.Length -gt $MaxFrameBytes) {
        throw (New-Object System.IO.IOException "Outgoing frame too large: $($payload.Length) bytes.")
    }
    # BitConverter.GetBytes(Int32) is little-endian on all current Windows platforms.
    $header = [BitConverter]::GetBytes([Int32]$payload.Length)
    $Stream.Write($header, 0, 4)
    $Stream.Write($payload, 0, $payload.Length)
    $Stream.Flush()
}

function Read-Ack {
    # Reads and prints the next frame; expects {"kind":"ack",...}. Returns the parsed ack.
    param([System.IO.Pipes.NamedPipeClientStream]$Stream, [string]$What)

    $json = Read-MiMiFrame -Stream $Stream -TimeoutMs 5000
    if ($null -eq $json) { Fail "Connection closed by MiMi while waiting for $What." }
    $ack = $null
    try { $ack = $json | ConvertFrom-Json }
    catch { Fail "Unparseable ack frame for ${What}: $json" }
    if ($ack.kind -ne "ack") { Fail "Expected an ack frame for ${What}, got: $json" }
    return $ack
}

# ---- resolve inputs ------------------------------------------------------------

$TokenPath = Join-Path $env:LOCALAPPDATA "MiMi\bridge-token"
if (-not (Test-Path -LiteralPath $TokenPath)) {
    Write-Host "ERROR: Bridge token not found. Is MiMi running with the bridge enabled?" -ForegroundColor Red
    Write-Host "Expected token file: $TokenPath" -ForegroundColor Red
    exit 1
}
$Token = (Get-Content -LiteralPath $TokenPath -Raw).Trim()

if ($PipeName -eq "") {
    $PipeName = "mimi-agent-bridge-" + $env:USERNAME
}

# agent.approval_requested REQUIRES approval_request_id (MiMi rejects the event otherwise).
if ($EventType -eq "agent.approval_requested" -and $ApprovalRequestId -eq "") {
    $ApprovalRequestId = "apr-" + [guid]::NewGuid().ToString("N").Substring(0, 12)
    Write-Verbose "Generated approval_request_id: $ApprovalRequestId"
}

$EventId = [guid]::NewGuid().ToString("N")

# Coerce metadata values to strings (the wire schema is string -> string) and cap entries.
$md = [ordered]@{}
$i = 0
foreach ($key in $Metadata.Keys) {
    if ($i -ge 16) { Write-Warning "More than 16 metadata entries supplied; extras dropped."; break }
    $md[[string]$key] = [string]$Metadata[$key]
    $i++
}

# ---- build frames ----------------------------------------------------------------

$helloJson = ConvertTo-Json -InputObject ([ordered]@{
    kind   = "hello"
    token  = $Token
    client = "mimi-ps-simulator"
}) -Compress -Depth 3

$event = [ordered]@{
    event_id   = $EventId
    provider   = $Provider
    session_id = $SessionId
    project    = $Project
    type       = $EventType
    title      = $Title
    message    = $Message
    timestamp  = [DateTimeOffset]::UtcNow.ToString("o")
    priority   = "Normal"
    metadata   = $md
}
# Omit approval_request_id entirely when empty so it deserializes as null server-side;
# agent.approval_requested / agent.approval_resolved always carry it (see above).
if ($ApprovalRequestId -ne "") {
    $event.approval_request_id = $ApprovalRequestId
}
$eventJson = ConvertTo-Json -InputObject ([ordered]@{
    kind  = "event"
    event = $event
}) -Compress -Depth 6

# ---- connect, handshake, send ------------------------------------------------------

$pipe = New-Object System.IO.Pipes.NamedPipeClientStream(".", $PipeName, [System.IO.Pipes.PipeDirection]::InOut)
try {
    try { $pipe.Connect(5000) }
    catch { Fail "Could not connect to pipe '$PipeName' within 5 s. Is MiMi running with the bridge enabled?" }
    Write-Verbose "Connected to pipe '$PipeName'."

    # Frame 1: hello + token, then wait for the ack.
    Write-MiMiFrame -Stream $pipe -Json $helloJson
    $helloAck = Read-Ack -Stream $pipe -What "the hello handshake"
    if (-not $helloAck.ok) {
        Fail "Handshake rejected: $($helloAck.error)"
    }
    Write-Verbose "Handshake ok."

    # Frame 2: the event, then wait for the ack.
    Write-MiMiFrame -Stream $pipe -Json $eventJson
    $ack = Read-Ack -Stream $pipe -What "the event ack"

    if ($ack.ok) {
        if ($ack.error -eq "duplicate") {
            Write-Host "Accepted as DUPLICATE (event_id already seen by MiMi): $EventId"
        }
        else {
            Write-Host "Event accepted: type=$EventType event_id=$EventId session=$SessionId"
        }
        if ($ApprovalRequestId -ne "") {
            Write-Host "approval_request_id: $ApprovalRequestId"
        }
    }
    else {
        Write-Host "Event rejected by MiMi: $($ack.error)" -ForegroundColor Red
        exit 1
    }

    # ---- optional listen loop -------------------------------------------------------
    if ($Listen) {
        Write-Host "Listening for frames from MiMi for up to 120 s (Ctrl+C to stop)..."
        Write-Host "(Click Approve/Deny on the island card to see an approval_resolved frame.)"
        $deadline = [DateTime]::UtcNow.AddSeconds(120)
        while ($true) {
            $remainingMs = [int]($deadline - [DateTime]::UtcNow).TotalMilliseconds
            if ($remainingMs -le 0) { Write-Host "Listen window elapsed."; break }
            try {
                $frame = Read-MiMiFrame -Stream $pipe -TimeoutMs $remainingMs
            }
            catch [System.TimeoutException] {
                Write-Host "Listen window elapsed."
                break
            }
            catch [System.IO.IOException] {
                Write-Host "Pipe closed by MiMi."
                break
            }
            catch {
                # Unwrap AggregateException from the awaited read.
                $inner = $_.Exception.InnerException
                if ($inner -is [System.TimeoutException] -or $inner -is [System.IO.IOException]) {
                    Write-Host "Listen ended: $($inner.Message)"
                }
                else {
                    Write-Host "Listen ended unexpectedly: $($_.Exception.Message)" -ForegroundColor Yellow
                }
                break
            }
            if ($null -eq $frame) { Write-Host "Pipe closed by MiMi."; break }
            Write-Host ("<<< {0}" -f $frame)
        }
    }

    exit 0
}
finally {
    $pipe.Dispose()
}
