# BLS Public Macro API Evidence

- Checked: 2026-07-26 (Asia/Tokyo)
- Official API endpoint: <https://api.bls.gov/publicAPI/v2/timeseries/data/>
- Official API documentation attempted: <https://www.bls.gov/developers/api_signature_v2.htm>
- Access mode tested: unauthenticated, read-only HTTPS POST
- Series tested: `CUUR0000SA0` (US CPI, all urban consumers, all items)

## Observed result

The official API returned `REQUEST_SUCCEEDED` with monthly CPI observations for 2025-2026 without an API key. The payload identifies the series, year, period, value, latest marker, and footnotes. It does not include the authoritative publication timestamp for each observation.

## Implementation conclusion

WPE may ingest allowlisted BLS series without a language model or user credential. The client must pin the exact official endpoint, cap response size, validate the requested series and numeric monthly observation, hash the raw response, and fail closed on missing or malformed data.

Because the payload does not expose a release timestamp, WPE records `releaseTimeBasis=official-endpoint-first-observed` and uses the fetch time as `releasedAtUtc`. This is an observation bound, not a claim about the formal BLS release time. Formal release-calendar correlation and revision history remain separate work.

## Uncertainty

The BLS documentation page returned an automated-access denial from this environment, while the public API endpoint itself responded successfully. Endpoint behavior, unauthenticated quotas, and payload fields may change; production refresh must remain bounded, cached, and fail closed.

## Release-calendar follow-up

- Checked: 2026-07-27 (Asia/Tokyo)
- Official calendar endpoint: <https://www.bls.gov/schedule/news_release/bls.ics>
- Attempted access: read-only HTTPS GET with a named local WPE user agent
- Observed result: the official site returned an automated-access denial from this development network.

WPE therefore implements the official endpoint, bounded ICS parser, UTC conversion, event correlation, raw-evidence hash, append-only provenance upgrade and fail-closed health contract, but does not claim live calendar availability. Runtime observations remain explicitly `official-endpoint-first-observed` unless a valid official calendar uniquely correlates the release; Macro remains `partial` until the target machine produces independent live evidence.
