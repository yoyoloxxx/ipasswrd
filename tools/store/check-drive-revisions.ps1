# Lists Google Drive revisions of vault.ipvault and compares record sizes across them.
# Reads ONLY ciphertext metadata (record id, updatedAt, base64 length) - nothing is decrypted,
# the master password is never touched. Downloads go to a scratch dir, never over the real vault.
# Tokens stay in process memory and are never printed.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security

$dir = Join-Path $env:LOCALAPPDATA 'IPasswrd'
$tokenPath = Join-Path $dir 'gdrive_token.bin'
if (-not (Test-Path $tokenPath)) { throw "no gdrive_token.bin - Google not connected" }

# Same DPAPI unprotect the app itself uses (CurrentUser scope).
$refresh = [Text.Encoding]::UTF8.GetString(
    [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($tokenPath), $null, 'CurrentUser'))

# OAuth client shipped inside the app (PKCE desktop client - not a secret).
$cid = '520945928883-8l31pocdehdrgagp3e8dh0sie6ocsjol.apps.googleusercontent.com'
$csec = $env:IPASSWRD_GOOGLE_SECRET; if (-not $csec) { $csec = (Get-Content (Join-Path $PSScriptRoot '..\..\google_oauth.json') -Raw | ConvertFrom-Json).client_secret }

$tok = Invoke-RestMethod -Method Post -Uri 'https://oauth2.googleapis.com/token' -Body @{
    client_id = $cid; client_secret = $csec; refresh_token = $refresh; grant_type = 'refresh_token'
}
$h = @{ Authorization = "Bearer $($tok.access_token)" }

$q = [uri]::EscapeDataString("name = 'vault.ipvault' and trashed = false")
$found = Invoke-RestMethod -Headers $h -Uri "https://www.googleapis.com/drive/v3/files?spaces=drive&fields=files(id,name,modifiedTime,size)&q=$q"
if (-not $found.files -or $found.files.Count -eq 0) { throw "vault.ipvault not found on Drive" }
$fid = $found.files[0].id
"file: $($found.files[0].name)  size=$($found.files[0].size)  modified=$($found.files[0].modifiedTime)"

$revs = (Invoke-RestMethod -Headers $h -Uri "https://www.googleapis.com/drive/v3/files/$fid/revisions?fields=revisions(id,modifiedTime,size)&pageSize=200").revisions
"revisions: $($revs.Count)"
$revs | ForEach-Object { "  {0}  {1,10} bytes  rev={2}" -f $_.modifiedTime, $_.size, $_.id }

# ---- download every revision to scratch and index records ----
$scratch = Join-Path $env:TEMP 'ipw-revcheck'
if (Test-Path $scratch) { Remove-Item $scratch -Recurse -Force }
New-Item -ItemType Directory -Force -Path $scratch | Out-Null

function Read-Records([string]$path) {
    $doc = Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json
    $map = @{}
    foreach ($r in $doc.records) {
        $map[$r.id] = [pscustomobject]@{ Len = $r.ciphertext.Length; Upd = $r.updatedAt; Del = [bool]$r.deleted }
    }
    ,$map
}

$i = 0
$revData = @()
foreach ($r in $revs) {
    $i++
    $out = Join-Path $scratch ("rev{0:d2}-{1}.ipvault" -f $i, ($r.modifiedTime -replace '[:]','-'))
    Invoke-WebRequest -Headers $h -Uri "https://www.googleapis.com/drive/v3/files/$fid/revisions/$($r.id)?alt=media" -OutFile $out | Out-Null
    $revData += [pscustomobject]@{ N = $i; Time = $r.modifiedTime; Path = $out; Map = (Read-Records $out) }
}

# Current local vault as the baseline "now".
$nowMap = Read-Records (Join-Path $dir 'vault.ipvault')
"local now: $($nowMap.Count) records, total b64 = $((($nowMap.Values | Measure-Object Len -Sum).Sum))"

# ---- loss report: records that were once markedly bigger than they are now, or vanished ----
$THRESH = 60000   # base64 chars ~ 45 KB of payload; attachments are way past this
$suspects = @{}
foreach ($rd in $revData) {
    foreach ($kv in $rd.Map.GetEnumerator()) {
        $id = $kv.Key; $old = $kv.Value
        if ($old.Del) { continue }
        $curLen = 0; $curDel = $false
        if ($nowMap.ContainsKey($id)) { $curLen = $nowMap[$id].Len; $curDel = $nowMap[$id].Del }
        if ($old.Len - $curLen -gt $THRESH) {
            $key = $id
            if (-not $suspects.ContainsKey($key) -or $suspects[$key].OldLen -lt $old.Len) {
                $suspects[$key] = [pscustomobject]@{
                    Id = $id.Substring(0,8); OldLen = $old.Len; NowLen = $curLen; NowDeleted = $curDel
                    BestRev = $rd.N; BestTime = $rd.Time; OldUpd = $old.Upd
                }
            }
        }
    }
}

""
if ($suspects.Count -eq 0) {
    "OK: no record in any revision is markedly larger than it is now - nothing looks lost."
} else {
    "SUSPECTS (record bigger in an old revision than now):"
    $suspects.Values | Sort-Object OldLen -Descending | ForEach-Object {
        "  id={0}  was={1:n0} b64 (rev #{2}, {3})  now={4:n0}{5}" -f $_.Id, $_.OldLen, $_.BestRev, $_.BestTime, $_.NowLen, $(if ($_.NowDeleted) { ' DELETED' } else { '' })
    }
    ""
    "revision files kept in: $scratch"
}
