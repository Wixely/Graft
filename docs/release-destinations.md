# Publishing a release to an application store

A project can publish its releases to an application store that takes builds through upload sessions
([ADR 0052](decisions/0052-release-destinations.md)). The files attached to a release are uploaded as builds, the
store validates them, and, if you ask for it, they are published on the store's stable or beta channel.

Every value on this page in angle brackets is a placeholder. Real endpoints, application identifiers and keys
belong in the destination's settings and nowhere else: not in the repository, not in a pipeline file, not in an
issue.

## What the operator does

Publishing makes the server call an address a Maintainer chose, so it is off until the operator turns it on:

| Switch | Container variable | Meaning |
|---|---|---|
| `--release-publishing off` | `GRAFT_RELEASE_PUBLISHING=` | No publishing. The default. |
| `--release-publishing public` | `GRAFT_RELEASE_PUBLISHING=public` | Https endpoints on the public internet only. |
| `--release-publishing any` | `GRAFT_RELEASE_PUBLISHING=any` | Also the server's own network, and plain http. For a store beside the forge. |

The forge prints which on startup as `RELEASE PUBLISHING:`. Requests go directly, never through a proxy, and
never follow a redirect.

Back up the repositories directory together with the database. Release files live there, under each
repository's `release-files/`, and a destination's stored endpoint and key can only be read with the
`data-protection-keys` directory beside the database.

## What to ask the store's operator for

- The complete address of the store's API for this application, ending in `/api/v1`.
- The application's UUID.
- An API key with the `read` and `upload:test` scopes, and `promote:production` if Graft is to publish to a
  channel.
- Whether this destination may publish to stable or beta at all.
- The Android package name, if a release carries an `.apk`.

## Adding a destination

In the project's settings, under **Release destinations**, as a Maintainer. Or with a token:

```sh
curl --request PUT "https://<forge>/api/graft/projects/<project-id>/release-destinations/<name>" \
  --header "PRIVATE-TOKEN: <maintainer-token>" --header "Content-Type: application/json" \
  --data '{
    "endpoint": "https://<store-host>/<store-path>/api/v1",
    "api_key": "<store-api-key>",
    "application_id": "<application-uuid>",
    "channel": "none",
    "allow_production_promotion": false,
    "package_name": "<android.package.name>",
    "rules": "<pattern> <platform> <architecture> main\n<pattern> skip"
  }'
```

| Field | Meaning |
|---|---|
| `endpoint` | Required for a new destination. Stored protected; only its host is ever shown again. |
| `api_key` | Required for a new destination. Stored protected; never shown again. |
| `application_id` | The application's UUID, with dashes. |
| `channel` | `none` uploads builds and leaves them unpublished. `stable` or `beta` publishes them. |
| `allow_production_promotion` | Must be `true` for `stable` or `beta`. `false` by default. |
| `package_name` | Needed only for `.apk` files. |
| `rules` | How files are declared; see below. |

To change a destination, send the same request without `endpoint` and `api_key`: what is stored is kept. A
project has up to ten destinations, each with its own endpoint, application and key.

## How files are declared

Each file attached to the release becomes one build. The store wants a platform (`Android`, `Windows`, `Linux`,
`MacOS`, `iOS` or `Unknown`) and an architecture (`any`, `x64`, `x86`, `arm64`, `arm`, or another short
lower-case word) for each.

Graft guesses both from the file's name: `example-win-x64.zip` is Windows x64, `example-linux-arm64.tar.gz` is
Linux arm64, `example.apk` is an Android package, `checksums.txt` is Unknown. Rules correct the guess. One to a
line, first match wins:

```text
example-windows-*.zip   Windows x64 main
*.apk                   Android any
*.sig                   skip
```

`skip` leaves a file out. `main` marks the file whose build the channel should report afterwards; it is
published last. A release's version is its tag without a leading `v`, and its notes are the release's
description, which the store limits to 8,000 characters.

## Publishing

On the release's page, a Maintainer sees each destination with **Dry run** and **Publish**. A dry run asks the
store what it accepts, shows what each file would be sent as, and uploads nothing. Do one first.

With a token, which is also how a pipeline job publishes:

```sh
# A dry run. Leave out dry_run to publish.
curl --include --request POST "https://<forge>/api/graft/projects/<project-id>/releases/<tag>/publications" \
  --header "PRIVATE-TOKEN: <maintainer-token>" --header "Content-Type: application/json" \
  --data '{"destination": "<name>", "dry_run": true}'
```

The answer is `202 Accepted` with a `Location` header. Read that address until `state` is `succeeded` or
`failed`:

```sh
curl "https://<forge>/api/graft/projects/<project-id>/release-publications/<id>" --header "PRIVATE-TOKEN: <maintainer-token>"
```

Each file is listed with its state (`accepted`, `published`, `rejected`, `failed`, `not-attempted`), the store's
build identifier, the SHA-256 that was sent and the SHA-256 the store holds. Those differ for an Android package
when the store signs it.

A job needs a token that belongs to a Maintainer and has the `api` scope. Graft does not hold secrets for jobs,
so the token comes from the runner's own environment.

## When it fails

Nothing is published unless every file of the release was accepted. The summary says what stopped it.

| What it says | What to do |
|---|---|
| The store did not accept the API key | Enter the key again in the destination's settings. |
| The API key lacks a scope | Ask the store's operator for the missing scope. |
| A file is larger than the store accepts, or its platform or format is not accepted | Nothing was uploaded. Fix the file or its rule. |
| The store rejected a file | The store's code and description are shown. The files accepted before it stay in the store, unpublished. |
| A revision conflict | Something else changed the application while Graft was publishing. Look at it in the store, then publish again. Graft does not retry this on its own. |
| The store already holds an upload under this file's key with different details | The file's notes, version or declaration changed after an earlier attempt. Attach it under a new name or make a new release. |
| The upload session expired | The store gave up on an unfinished upload. If publishing again reports the same, the file needs a new name. |
| The server stopped while this was under way | Publish again. What the store already holds is not sent twice. |

Publishing again is always safe: an accepted file is found and not uploaded again, an interrupted upload carries
on, and a build already on the channel is left as it is.

Graft never shows or logs the key, the endpoint beyond its host, or any upload address the store issues.
