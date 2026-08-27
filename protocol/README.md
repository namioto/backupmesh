# BackupMesh Control Protocol

This directory is the language-neutral contract shared by the Go Source Agent
and the .NET Storage Agent. [`openapi.yaml`](openapi.yaml) is the normative API
definition; generated language models are build artifacts, not the source of
truth.

## Versioning and compatibility

- The URL major version (`/api/v1`) changes only for breaking wire changes.
- `info.version` follows semantic versioning for the specification itself.
- Within v1, optional fields and new endpoints may be added. Receivers must
  ignore unknown response fields, while senders must not send undeclared request
  fields (`additionalProperties: false`).
- Enum additions are potentially breaking for generated clients. They require a
  minor spec release and clients must handle unknown enum values as an
  unsupported peer response, never silently reinterpret them.
- Existing required fields, meanings, bounds, and enum values cannot change in
  v1. Deprecation precedes removal, and removal requires `/api/v2`.
- Agents should expose their product and protocol versions through diagnostics;
  HTTP compatibility is determined by the URL major version, not product version.
- Source Agents publish stable Backup Set UUIDs through `/source/catalog`.
  Storage UIs read `/source/catalogs`; mappings retain those UUIDs when display
  names or source paths change.

## Security invariants

1. Every endpoint uses TLS 1.3 with mutual certificate authentication. Plain HTTP
   and anonymous fallback are forbidden.
2. A valid certificate is not sufficient authorization. The certificate identity
   must be paired with the claimed agent, and authorization is checked per
   endpoint and job ownership.
3. `X-Request-ID` is a UUID v4 correlation identifier. It is logged and returned,
   but it is not an authentication secret.
4. Mutating calls require an `Idempotency-Key`. Keys are scoped to authenticated
   identity plus operation, retained with outcomes for at least 24 hours, and
   cannot be reused with different canonical request bodies.
5. `X-BackupMesh-Sent-At` must be within five minutes of the receiver clock.
   Event UUIDs and monotonically increasing per-job sequences prevent accepted
   messages from being replayed or reordered.
6. Job access is restricted to the paired Source Agent that created the job and
   the Storage Agent serving it. Logs must not contain credentials, repository
   passwords, private keys, or raw bearer material.
7. Implementations enforce the schema bounds before persistence or process
   invocation and return the common `application/problem+json` error model.

## Validation

Run the dependency-free structural check from the repository root:

```powershell
powershell -NoProfile -File protocol/validate.ps1
```

The script checks required OpenAPI sections, paths, operations, references, and
security declarations. For full OpenAPI 3.1 schema validation in CI, also run:

```text
npx --yes @redocly/cli lint protocol/openapi.yaml
```

The latter command downloads a third-party validator and therefore is not used
by the local dependency-free script.
