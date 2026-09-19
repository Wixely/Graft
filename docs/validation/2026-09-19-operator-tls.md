# Operator-managed TLS validation

Date: 2026-09-19. Status: PASS within the recorded local scope. Owner: Implementation agent. Review when certificate loading, Kestrel, crypto libraries or deployment secrets change.

Graft now accepts an operator PKCS#12 certificate and a separate one-line UTF-8 password file. The password value is absent from process arguments, environment variables, service units and container metadata. Non-loopback listeners require an operator certificate. Startup rejects a missing private key, an expired/not-yet-valid certificate, an incompatible enhanced-key-usage extension, or a subject alternative name that does not match the configured public origin.

The C# acceptance harness generated a synthetic self-signed server certificate, pinned its SHA-256 fingerprint as the client, restarted with the same files, replaced the certificate, restarted again and verified the new fingerprint. It also verified refusal of a non-loopback listener without a certificate. Results:

| Artifact/mode | Result |
| --- | --- |
| Windows managed server | PASS, 6 TLS lifecycle/validation assertions |
| Windows x64 NativeAOT server | PASS, 6 TLS lifecycle/validation assertions; 64 Git/API and 36 operations regressions |
| Linux x64 self-contained CoreCLR | PASS, 6 TLS lifecycle/validation assertions |
| Linux static-musl NativeAOT | PASS, 6 TLS lifecycle/validation assertions; 64 Git/API and 36 operations regressions |
| systemd `Type=notify` | PASS, 6 lifecycle assertions using operator certificate files |
| Rootless Podman | PASS, 7 lifecycle assertions using read-only certificate/password mounts |

The test certificate was deliberately private and untrusted; clients pinned its exact fingerprint. This proves certificate selection, private-key loading, restart behavior, renewal replacement and deployment secret plumbing. It does not prove a public/private CA chain, DNS, revocation, automated issuance or a real client trust store. Those belong to the operator environment and must be verified before exposing a listener.

Recommended next action: Owner: User + implementation agent — execute the elevated Windows Service acceptance with disposable certificate files and run the final static-musl artifacts on Linux 3.10.108. Owner: Operator — provide the intended CA-issued certificate/DNS/trust environment for deployment acceptance.
