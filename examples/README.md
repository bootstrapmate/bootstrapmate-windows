# Manifest examples

`bootstrapmate.json` is a minimal, working manifest. Point the CLI at a copy of it:

```powershell
managedbootstrapinstall.exe --url https://example.com/bootstrap/bootstrapmate.json
```

## Item keys

Every item **must** carry `name`, `url`, `file` and `type` — the parser reads all four
with `GetProperty`, so an item missing any of them aborts that item. `name` is the key
the parser reads for the display name; there is no `displayname` key.

Optional keys: `arguments`, `target` (sbin-installer `pkg` items), `condition`
(`architecture_x64` / `architecture_arm64`), `expectedPublisher` and `allowUnsigned`.

## Payload hashing is not implemented

There is no `hash` key. Downloaded payloads are not hash-verified today; the work is
tracked in issue #33. Adding a `hash` field to a manifest has no effect — it is ignored,
not enforced — so the example does not carry one. Authenticode verification of `msi` and
`exe` installers is a separate mechanism and *is* implemented; see the signature
verification section of the top-level README.

## Detection scripts

`detection-scripts/` holds Intune Win32 detection scripts for the setup-assistant phase,
the userland phase and a generic "did the last run succeed" check.
