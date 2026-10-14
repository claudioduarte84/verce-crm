#requires -Version 7.0
<#
LIVE-SCAN-01 (docs/S8C2-IMPLEMENTATION-HANDOFF.md, "Reusable live procedures" and D12(b)) — leak scan of S8C.2 live-validation evidence.

Validation-only. Never shipped, never run against anything but disposable TP-4 artifacts. It reads three surfaces (log file, the disposable database, saved API
responses), prints ONE JSON summary on stdout with counts only, and keeps stderr empty:

  {"result":"PASS|FAIL|REFUSED|INPUT_INVALID","errorCode":<code or null>,"surfaces":[{"surface":"LOGS|DATABASE|API","artifactsScanned":n,"unitsScanned":n,"counts":{...}}]}

Exit codes: 0 PASS, 1 FAIL (>= 1 match), 2 REFUSED or INPUT_INVALID (a guard or argument error; nothing certified).

A PASS needs every requested surface to have actually been read (units scanned > 0) and every count to be 0. Anything that prevents a complete read — a missing or
unreadable file, an unparsable response, a database that cannot be identified or queried — ends in exit 2, never in 0.

The script never prints, logs or writes a matched value, the known secret, its fingerprint or any excerpt, and creates no file. The known client secret is read into
memory from the User Secrets store; only its SHA-256 fingerprint, length and first character are kept for comparison, and none of them is ever emitted.

The database is reached only through `<DockerExecutable> exec -i -e PGOPTIONS=... <container> psql -U <user> -d <database> -At` with
default_transaction_read_only=on, after the frozen guard sequence (all before any database access):
  1. -PostgresContainer equals verce-postgres (case-insensitive)            -> OPERATIONAL_CONTAINER_TARGET_FORBIDDEN
  2. -Database equals verce / does not match ^verce_s8c2_live$              -> DATABASE_TARGET_FORBIDDEN / DATABASE_TARGET_NOT_DISPOSABLE
  3. no -ConfirmDisposable                                                  -> DISPOSABLE_CONFIRMATION_REQUIRED
  4. `inspect --format {{.Name}}` resolves to /verce-postgres (ID or alias)  -> OPERATIONAL_CONTAINER_TARGET_FORBIDDEN;
     an identity that cannot be proven at all                               -> CONTAINER_IDENTITY_UNPROVEN
#>
param(
    [string]$LogFile,
    [string]$ApiResponseDirectory,
    [string]$PostgresContainer,
    [string]$Database,
    [string]$PostgresUser = 'postgres',
    [string]$UserSecretsFile,
    [switch]$ConfirmDisposable,
    [string]$DockerExecutable = 'docker',
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$UnexpectedArguments
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version 3.0
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)

$CountKeys = @('AUTH_HEADER', 'BEARER', 'ACCESS_TOKEN', 'REFRESH_TOKEN', 'AUTH_CODE', 'CLIENT_SECRET', 'TOKEN_ENDPOINT', 'RAW_REQUEST', 'RAW_RESPONSE',
    'STRUCTURAL.DB_COLUMN', 'STRUCTURAL.HTTPCLIENT_LOG', 'STRUCTURAL.LOG_BODY', 'STRUCTURAL.API_PROPERTY')
$ScannedTables = @('commerce.marketplace_authorization_session', 'commerce.marketplace_account', 'commerce.marketplace_account_connection', 'commerce.marketplace_account_capability',
    'commerce.marketplace_account_operation', 'commerce.marketplace_listing', 'commerce.marketplace_listing_observation', 'commerce.marketplace_listing_sync_run',
    'commerce.marketplace_listing_sync_run_item', 'commerce.marketplace_account_sku_mapping', 'platform.audit_log')

function Write-Summary {
    param([string]$Result, [string]$ErrorCode, [object[]]$Surfaces = @())
    $summary = [ordered]@{ result = $Result; errorCode = $(if ($ErrorCode) { $ErrorCode } else { $null }); surfaces = @($Surfaces) }
    [Console]::Out.Write(($summary | ConvertTo-Json -Compress -Depth 6))
    [Console]::Out.Flush()
}

function Stop-Scan {
    param([ValidateSet('REFUSED', 'INPUT_INVALID')][string]$Result, [string]$ErrorCode)
    Write-Summary -Result $Result -ErrorCode $ErrorCode
    exit 2
}

try {
    # ---- argument sanity (before anything is touched) ----
    if ($UnexpectedArguments -and $UnexpectedArguments.Count -gt 0) { Stop-Scan 'INPUT_INVALID' 'UNKNOWN_ARGUMENT' }

    $databaseRequested = -not [string]::IsNullOrWhiteSpace($PostgresContainer) -or -not [string]::IsNullOrWhiteSpace($Database)

    # ---- database guard: every refusal happens before any database access, and before the docker executable is even resolved ----
    if ($databaseRequested) {
        $containerNormalized = if ($PostgresContainer) { $PostgresContainer.Trim().TrimStart('/').ToLowerInvariant() } else { '' }
        if ($containerNormalized -eq 'verce-postgres') { Stop-Scan 'REFUSED' 'OPERATIONAL_CONTAINER_TARGET_FORBIDDEN' }
        $databaseNormalized = if ($Database) { $Database.Trim().ToLowerInvariant() } else { '' }
        if ($databaseNormalized -eq 'verce') { Stop-Scan 'REFUSED' 'DATABASE_TARGET_FORBIDDEN' }
        if ($Database -cnotmatch '^verce_s8c2_live$') { Stop-Scan 'REFUSED' 'DATABASE_TARGET_NOT_DISPOSABLE' }
        if (-not $ConfirmDisposable) { Stop-Scan 'REFUSED' 'DISPOSABLE_CONFIRMATION_REQUIRED' }
        if ([string]::IsNullOrWhiteSpace($PostgresContainer)) { Stop-Scan 'REFUSED' 'CONTAINER_IDENTITY_UNPROVEN' }
        if ($PostgresContainer -cnotmatch '^/?[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$') { Stop-Scan 'INPUT_INVALID' 'CONTAINER_NAME_INVALID' }
        if ($PostgresUser -cnotmatch '^[A-Za-z_][A-Za-z0-9_]{0,62}$') { Stop-Scan 'INPUT_INVALID' 'POSTGRES_USER_INVALID' }
    }

    $dockerCommand = $null
    if ($databaseRequested) {
        $dockerCommand = Get-Command -Name $DockerExecutable -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -eq $dockerCommand) { Stop-Scan 'INPUT_INVALID' 'DOCKER_EXECUTABLE_NOT_FOUND' }
    }

    function Invoke-Docker {
        param([string[]]$Arguments, [string]$Stdin = $null)
        # Arguments are passed as an array (never interpolated into a command line); stderr is discarded so the scanner's own stderr stays empty.
        if ($null -ne $Stdin) { $output = $Stdin | & $dockerCommand.Source @Arguments 2>$null } else { $output = & $dockerCommand.Source @Arguments 2>$null }
        [object[]]$captured = if ($null -eq $output) { @() } else { @($output) }
        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Lines = $captured }
    }

    if ($databaseRequested) {
        $inspect = Invoke-Docker -Arguments @('inspect', '--format', '{{.Name}}', $PostgresContainer)
        $resolved = if ($inspect.ExitCode -eq 0 -and $inspect.Lines.Count -gt 0) { ([string]$inspect.Lines[0]).Trim() } else { '' }
        if ([string]::IsNullOrWhiteSpace($resolved)) { Stop-Scan 'REFUSED' 'CONTAINER_IDENTITY_UNPROVEN' }
        if ($resolved.TrimStart('/').ToLowerInvariant() -eq 'verce-postgres') { Stop-Scan 'REFUSED' 'OPERATIONAL_CONTAINER_TARGET_FORBIDDEN' }
    }

    # ---- inputs ----
    $logRequested = -not [string]::IsNullOrWhiteSpace($LogFile)
    $apiRequested = -not [string]::IsNullOrWhiteSpace($ApiResponseDirectory)
    if (-not ($logRequested -or $apiRequested -or $databaseRequested)) { Stop-Scan 'INPUT_INVALID' 'NO_SURFACE_SELECTED' }
    if ([string]::IsNullOrWhiteSpace($UserSecretsFile)) { Stop-Scan 'INPUT_INVALID' 'USER_SECRETS_FILE_REQUIRED' }

    $secret = $null
    try {
        $secretsText = [System.IO.File]::ReadAllText($UserSecretsFile, [System.Text.Encoding]::UTF8)
        $secretsDocument = [System.Text.Json.JsonDocument]::Parse($secretsText)
        $root = $secretsDocument.RootElement
        $element = [System.Text.Json.JsonElement]::new()
        if ($root.TryGetProperty('Marketplaces:MercadoLivre:ClientSecret', [ref]$element) -and $element.ValueKind -eq 'String') { $secret = $element.GetString() }
        elseif ($root.TryGetProperty('Marketplaces', [ref]$element) -and $element.TryGetProperty('MercadoLivre', [ref]$element) -and $element.TryGetProperty('ClientSecret', [ref]$element) -and $element.ValueKind -eq 'String') { $secret = $element.GetString() }
        $secretsDocument.Dispose(); $secretsText = $null
    }
    catch { Stop-Scan 'INPUT_INVALID' 'USER_SECRETS_UNREADABLE' }
    if ([string]::IsNullOrEmpty($secret)) { Stop-Scan 'INPUT_INVALID' 'CLIENT_SECRET_NOT_FOUND' }
    # A very short value would match ordinary words and turn the fingerprint check into noise, so it is not accepted as a secret to look for.
    if ($secret.Length -lt 8) { Stop-Scan 'INPUT_INVALID' 'CLIENT_SECRET_TOO_SHORT' }

    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace S8C2LeakScan
{
    public sealed class InvalidApiJsonException : Exception { }
    public sealed class InvalidRowJsonException : Exception { }

    /// <summary>Counts per category and per structural sub-counter. Never holds a matched value.</summary>
    public sealed class Counts
    {
        public static readonly string[] Keys = {
            "AUTH_HEADER", "BEARER", "ACCESS_TOKEN", "REFRESH_TOKEN", "AUTH_CODE", "CLIENT_SECRET", "TOKEN_ENDPOINT", "RAW_REQUEST", "RAW_RESPONSE",
            "STRUCTURAL.DB_COLUMN", "STRUCTURAL.HTTPCLIENT_LOG", "STRUCTURAL.LOG_BODY", "STRUCTURAL.API_PROPERTY" };
        private readonly Dictionary<string, int> _values = new Dictionary<string, int>();
        public Counts() { foreach (var key in Keys) _values[key] = 0; }
        public void Add(string key, int n = 1) { _values[key] += n; }
        public int Get(string key) { return _values[key]; }
        public int Total() { int total = 0; foreach (var v in _values.Values) total += v; return total; }
    }

    /// <summary>Known-secret detection without exposure: the secret is reduced to (SHA-256, length, first char); each candidate window is hashed and compared.</summary>
    public sealed class KnownSecret
    {
        private readonly byte[] _fingerprint;
        private readonly int _length;
        private readonly char _first;

        public KnownSecret(string secret)
        {
            _length = secret.Length;
            _first = secret[0];
            _fingerprint = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        }

        /// <summary>Number of windows of the secret's length whose SHA-256 equals the fingerprint. The first-character test only skips windows that cannot match.</summary>
        public int Count(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length < _length) return 0;
            int hits = 0;
            for (int i = 0; i + _length <= text.Length; i++)
            {
                if (text[i] != _first) continue;
                var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.Substring(i, _length)));
                if (CryptographicOperations.FixedTimeEquals(hash, _fingerprint)) hits++;
            }
            return hits;
        }
    }

    public static class Engine
    {
        private static Regex R(string pattern) { return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(5)); }

        // Frozen runtime patterns (LIVE-SCAN-01). Case-insensitive. VERCE audit/DTO JSON uses its own property names, so provider key serializations are the signal.
        private static readonly KeyValuePair<string, Regex[]>[] Patterns = {
            new KeyValuePair<string, Regex[]>("AUTH_HEADER", new[] { R(@"authorization\s*[:=]\s*\S"), R("\"authorization\"\\s*:"), R(@"\bAuthorization\s*\[") }),
            new KeyValuePair<string, Regex[]>("BEARER", new[] { R(@"\bbearer\s+[A-Za-z0-9._~+/=-]{8,}"), R(@"APP_USR-") }),
            new KeyValuePair<string, Regex[]>("ACCESS_TOKEN", new[] { R(@"access_token"), R(@"accessToken") }),
            new KeyValuePair<string, Regex[]>("REFRESH_TOKEN", new[] { R(@"refresh_token"), R(@"refreshToken"), R(@"\bTG-[0-9a-f]{6,}") }),
            new KeyValuePair<string, Regex[]>("AUTH_CODE", new[] { R(@"[?&]code="), R(@"grant_type=authorization_code"), R("\"code\"\\s*:\\s*\"TG-") }),
            new KeyValuePair<string, Regex[]>("CLIENT_SECRET", new[] { R(@"client_secret"), R(@"clientSecret"), R(@"ClientSecret\s*[:=]") }),
            new KeyValuePair<string, Regex[]>("TOKEN_ENDPOINT", new[] { R(@"/oauth/token"), R(@"grant_type=") }),
            new KeyValuePair<string, Regex[]>("RAW_REQUEST", new[] { R(@"search_type=scan"), R(@"scroll_id"), R(@"scrollId"), R(@"ids=MLB"), R(@"attributes=body\."), R(@"api\.mercadolibre\.com/") }),
            new KeyValuePair<string, Regex[]>("RAW_RESPONSE", new[] {
                R("\"seller_id\"\\s*:"), R("\"seller_address\""), R("\"available_quantity\""), R("\"sub_status\"\\s*:"), R("\"paging\"\\s*:"), R("\"results\"\\s*:\\s*\\["),
                R("\"status_code\"\\s*:"), R("\"body\"\\s*:\\s*\\{"), R("\"site_id\"\\s*:"), R("\"nickname\"\\s*:"), R("\"email\"\\s*:"), R("\"identification\"\\s*:") }),
        };

        // Property names (compared case-insensitively after removing '_' and '-') that may never appear anywhere in a saved API response.
        private static readonly HashSet<string> ForbiddenProperties = new HashSet<string>(StringComparer.Ordinal) {
            "raw", "rawbody", "rawpayload", "providerpayload", "payload", "body", "requestbody", "responsebody", "token", "accesstoken", "refreshtoken", "secret",
            "clientsecret", "credentialreference", "credentialversion", "authorization", "scrollid", "statehash", "browserbindinghash", "email" };

        private static readonly HashSet<string> ProviderCallKeys = new HashSet<string>(StringComparer.Ordinal) {
            "provider", "operation", "status", "classification", "certainty", "attempt", "durationMs", "account", "correlation", "requestedIdCount", "envelopeCount", "usableEnvelopeCount", "histogram" };

        private static readonly Regex EventHeader = new Regex(@"^(trce|dbug|info|warn|fail|crit): (?<category>[^\[]+)\[\d+\]", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(5));
        private static readonly Regex JsonLiteral = new Regex("[\\{\\[]\\s*\"[^\"]+\"\\s*:", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(5));
        private static readonly Regex KeyToken = new Regex(@"(?<![A-Za-z0-9_.])(?<key>[A-Za-z][A-Za-z0-9_]*)=", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(5));

        /// <summary>Pattern categories: a unit (line, value or response) counts once per category in which at least one frozen pattern matches.</summary>
        public static void ScanPatterns(Counts counts, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (var category in Patterns)
                foreach (var pattern in category.Value)
                    if (pattern.IsMatch(text)) { counts.Add(category.Key); break; }
        }

        private static void ScanKnown(Counts counts, KnownSecret known, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            int hits = known.Count(text);
            if (hits > 0) counts.Add("CLIENT_SECRET", hits);
        }

        /// <summary>Logs: every line against the patterns and the known secret, then the event-level structural checks over the default ASP.NET console format.</summary>
        public static void ScanLog(Counts counts, KnownSecret known, string[] lines)
        {
            string category = null;
            var message = new StringBuilder();
            foreach (var line in lines)
            {
                ScanPatterns(counts, line);
                ScanKnown(counts, known, line);
                var header = EventHeader.Match(line);
                if (header.Success)
                {
                    FinishEvent(counts, category, message.ToString());
                    category = header.Groups["category"].Value.Trim();
                    message.Clear();
                }
                else if (category != null) message.Append(line).Append('\n');
            }
            FinishEvent(counts, category, message.ToString());
        }

        private static void FinishEvent(Counts counts, string category, string message)
        {
            if (category == null) return;
            var text = message.Trim();
            if (category.StartsWith("System.Net.Http.HttpClient.MercadoLivre", StringComparison.Ordinal)) counts.Add("STRUCTURAL.HTTPCLIENT_LOG");
            bool body = false;
            if (text.StartsWith("MarketplaceProviderCall ", StringComparison.Ordinal))
            {
                if (text.IndexOfAny(new[] { '{', '[', '"' }) >= 0) body = true;
                else foreach (Match m in KeyToken.Matches(text)) if (!ProviderCallKeys.Contains(m.Groups["key"].Value)) { body = true; break; }
            }
            if ((category.StartsWith("Verce.Infrastructure.Marketplaces", StringComparison.Ordinal) || category.StartsWith("Verce.Modules.Commerce", StringComparison.Ordinal)) && JsonLiteral.IsMatch(text)) body = true;
            if (body) counts.Add("STRUCTURAL.LOG_BODY");
        }

        private static string Normalize(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (var c in name) if (c != '_' && c != '-') sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        /// <summary>One saved API response: patterns over the text, then a walk of every property name and string value at any depth (arrays of arrays included).</summary>
        public static void ScanApi(Counts counts, KnownSecret known, string raw)
        {
            ScanPatterns(counts, raw);
            JsonDocument document;
            try { document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 512 }); }
            catch (JsonException) { throw new InvalidApiJsonException(); }
            using (document)
            {
                var stack = new Stack<JsonElement>();
                stack.Push(document.RootElement);
                while (stack.Count > 0)
                {
                    var element = stack.Pop();
                    switch (element.ValueKind)
                    {
                        case JsonValueKind.Object:
                            foreach (var property in element.EnumerateObject())
                            {
                                if (ForbiddenProperties.Contains(Normalize(property.Name))) counts.Add("STRUCTURAL.API_PROPERTY");
                                ScanKnown(counts, known, property.Name);
                                stack.Push(property.Value);
                            }
                            break;
                        case JsonValueKind.Array:
                            foreach (var item in element.EnumerateArray()) stack.Push(item);
                            break;
                        case JsonValueKind.String:
                            ScanKnown(counts, known, element.GetString());
                            break;
                    }
                }
            }
        }

        /// <summary>One database row, delivered as a JSON object of text values: every non-null value is scanned on its own.</summary>
        public static void ScanRow(Counts counts, KnownSecret known, string rowJson)
        {
            JsonDocument document;
            try { document = JsonDocument.Parse(rowJson); }
            catch (JsonException) { throw new InvalidRowJsonException(); }
            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidRowJsonException();
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.String) continue;
                    var value = property.Value.GetString();
                    ScanPatterns(counts, value);
                    ScanKnown(counts, known, value);
                }
            }
        }
    }
}
'@

    $known = [S8C2LeakScan.KnownSecret]::new($secret)
    $secret = $null

    $surfaces = New-Object System.Collections.Generic.List[object]
    function New-Surface {
        param([string]$Name, [int]$Artifacts, [int]$Units, [S8C2LeakScan.Counts]$Counts)
        $ordered = [ordered]@{}
        foreach ($key in $CountKeys) { $ordered[$key] = $Counts.Get($key) }
        [ordered]@{ surface = $Name; artifactsScanned = $Artifacts; unitsScanned = $Units; counts = $ordered }
    }

    # ---- surface A: application log ----
    if ($logRequested) {
        if (-not (Test-Path -LiteralPath $LogFile -PathType Leaf)) { Stop-Scan 'INPUT_INVALID' 'LOG_FILE_NOT_FOUND' }
        try { $lines = [System.IO.File]::ReadAllLines($LogFile, [System.Text.Encoding]::UTF8) } catch { Stop-Scan 'INPUT_INVALID' 'LOG_FILE_UNREADABLE' }
        if ($lines.Count -eq 0) { Stop-Scan 'INPUT_INVALID' 'LOG_FILE_EMPTY' }
        $counts = [S8C2LeakScan.Counts]::new()
        [S8C2LeakScan.Engine]::ScanLog($counts, $known, $lines)
        $surfaces.Add((New-Surface 'LOGS' 1 $lines.Count $counts))
        $lines = $null
    }

    # ---- surface B: disposable database (read-only through psql in the already-identified container) ----
    if ($databaseRequested) {
        function Invoke-Psql {
            param([string]$Sql)
            $result = Invoke-Docker -Arguments @('exec', '-i', '-e', 'PGOPTIONS=-c default_transaction_read_only=on', $PostgresContainer, 'psql', '-U', $PostgresUser, '-d', $Database, '-At', '-X', '-v', 'ON_ERROR_STOP=1') -Stdin $Sql
            if ($result.ExitCode -ne 0) { Stop-Scan 'INPUT_INVALID' 'DATABASE_READ_FAILED' }
            $result.Lines
        }

        $tableList = ($ScannedTables | ForEach-Object { "'$_'" }) -join ','
        $textColumns = Invoke-Psql "SELECT table_schema || '.' || table_name || '|' || column_name FROM information_schema.columns WHERE data_type IN ('text','character varying','character','jsonb') AND (table_schema || '.' || table_name) IN ($tableList) ORDER BY table_schema, table_name, ordinal_position;"
        $columnsByTable = @{}
        foreach ($entry in $textColumns) {
            $text = [string]$entry
            if ([string]::IsNullOrWhiteSpace($text)) { continue }
            $parts = $text.Split('|', 2)
            if ($parts.Count -ne 2 -or $parts[1] -cnotmatch '^[a-z0-9_]+$') { Stop-Scan 'INPUT_INVALID' 'DATABASE_SCHEMA_UNEXPECTED' }
            if (-not $columnsByTable.ContainsKey($parts[0])) { $columnsByTable[$parts[0]] = New-Object System.Collections.Generic.List[string] }
            $columnsByTable[$parts[0]].Add($parts[1])
        }
        foreach ($table in $ScannedTables) { if (-not $columnsByTable.ContainsKey($table)) { Stop-Scan 'INPUT_INVALID' 'DATABASE_TABLE_MISSING' } }

        $counts = [S8C2LeakScan.Counts]::new()
        # Structural: no raw/payload/body/token/secret/scroll column anywhere in the scanned schemas (frozen query).
        $structural = Invoke-Psql "SELECT table_schema, table_name, column_name FROM information_schema.columns WHERE (table_schema = 'commerce' OR (table_schema = 'platform' AND table_name = 'audit_log')) AND column_name ~* '(^raw_|payload|_body$|^body$|response_body|request_body|access_token|refresh_token|client_secret|scroll_id)';"
        foreach ($entry in $structural) { if (-not [string]::IsNullOrWhiteSpace([string]$entry)) { $counts.Add('STRUCTURAL.DB_COLUMN') } }

        $rows = 0
        foreach ($table in $ScannedTables) {
            $columns = $columnsByTable[$table]
            $tableRows = 0
            for ($offset = 0; $offset -lt $columns.Count; $offset += 40) {
                $chunk = $columns.GetRange($offset, [Math]::Min(40, $columns.Count - $offset))
                $pairs = ($chunk | ForEach-Object { "'$_', `"$_`"::text" }) -join ', '
                $output = Invoke-Psql "SELECT json_build_object($pairs)::text FROM $table;"
                $chunkRows = 0
                foreach ($line in $output) {
                    $rowJson = [string]$line
                    if ([string]::IsNullOrWhiteSpace($rowJson)) { continue }
                    try { [S8C2LeakScan.Engine]::ScanRow($counts, $known, $rowJson) } catch [S8C2LeakScan.InvalidRowJsonException] { Stop-Scan 'INPUT_INVALID' 'DATABASE_ROW_UNPARSEABLE' }
                    $chunkRows++
                }
                if ($offset -eq 0) { $tableRows = $chunkRows }
            }
            $rows += $tableRows
        }
        if ($rows -eq 0) { Stop-Scan 'INPUT_INVALID' 'DATABASE_NO_ROWS' }
        $surfaces.Add((New-Surface 'DATABASE' $ScannedTables.Count $rows $counts))
    }

    # ---- surface C: saved API responses ----
    if ($apiRequested) {
        if (-not (Test-Path -LiteralPath $ApiResponseDirectory -PathType Container)) { Stop-Scan 'INPUT_INVALID' 'API_DIRECTORY_NOT_FOUND' }
        $files = @(Get-ChildItem -LiteralPath $ApiResponseDirectory -Filter '*.json' -File)
        if ($files.Count -eq 0) { Stop-Scan 'INPUT_INVALID' 'API_DIRECTORY_EMPTY' }
        $counts = [S8C2LeakScan.Counts]::new()
        foreach ($file in $files) {
            try { $raw = [System.IO.File]::ReadAllText($file.FullName, [System.Text.Encoding]::UTF8) } catch { Stop-Scan 'INPUT_INVALID' 'API_RESPONSE_UNREADABLE' }
            try { [S8C2LeakScan.Engine]::ScanApi($counts, $known, $raw) } catch [S8C2LeakScan.InvalidApiJsonException] { Stop-Scan 'INPUT_INVALID' 'API_RESPONSE_NOT_JSON' }
            $raw = $null
        }
        $surfaces.Add((New-Surface 'API' $files.Count $files.Count $counts))
    }

    $known = $null
    [GC]::Collect()

    $failed = $false
    foreach ($surface in $surfaces) { foreach ($key in $CountKeys) { if ([int]$surface.counts[$key] -gt 0) { $failed = $true } } }
    if ($failed) { Write-Summary -Result 'FAIL' -ErrorCode $null -Surfaces $surfaces.ToArray(); exit 1 }
    Write-Summary -Result 'PASS' -ErrorCode $null -Surfaces $surfaces.ToArray()
    exit 0
}
catch {
    # No exception text is ever printed: it could carry scanned content.
    Write-Summary -Result 'INPUT_INVALID' -ErrorCode 'UNEXPECTED_ERROR'
    exit 2
}
