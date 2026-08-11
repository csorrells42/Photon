# Java / Eclipse JDT lane

This folder is an isolated trusted-host lane for the catalog's existing `java-jdt` capability.

The implementation currently fails closed. The repository does not contain a release-owned lock
that authoritatively binds both an official Eclipse JDT LS distribution and an app-owned Java
runtime, including their full payload manifests, license/notice material, and receipt digest.
Accordingly, this lane contains no guessed version, URL, digest, provisioning script, or production
process launcher. `JavaJdtLanguageToolingProvider.CreateUnprovisioned` reports the exact blocker and
cannot start a process.

The typed session and provider lifecycle are implemented behind an internal trusted-runtime
authority seam so fake protocol smoke can exercise negotiation, document-version freshness,
bounded results, diagnostic freshness, structured start/stop, and cleanup without PATH discovery or
a machine Java installation.
