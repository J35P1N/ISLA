# JSON and SQLite acceptance testing

Use the current checkout and dedicated test volumes. These procedures preserve your normal installation. SQLite and JSON each support one application process on a local persistent filesystem.

## Automated gates

From the repository root, with .NET 10 and Node.js 24 installed:

```sh
dotnet test ISLA.sln -c Release
node tests/browser/shopping-mode.test.mjs
npm ci --prefix tests/browser
npx --prefix tests/browser playwright install chromium
npm test --prefix tests/browser
```

On a Linux CI runner, install Chromium system dependencies with `npx --prefix tests/browser playwright install --with-deps chromium` instead. Browser tests use random temporary directories and launch the Release assembly; build/test .NET first. They cover both wizard selections, household imports and edits, leftovers, purchase pricing, administrator/member permissions, anonymous direct requests, password reset and session revocation, application replacement, authenticated Blazor reconnection, stopped-directory backup/restore, and SQLite → JSON → SQLite transfers.

Run the same acceptance suite against actual containers and local named Docker volumes:

```sh
docker build -t isla:acceptance .
bash scripts/ci/container-smoke.sh isla:acceptance linux/amd64
ISLA_TEST_IMAGE=isla:acceptance ISLA_TEST_PLATFORM=linux/amd64 ISLA_TEST_COMPOSE=1 npm test --prefix tests/browser
```

Compose acceptance requires Docker Compose 2.24.4 or newer. It uses the repository Compose service and volume definitions with an isolated project, the selected image, a private token and a random loopback port. Omit `ISLA_TEST_COMPOSE=1` to test direct Docker containers instead.

Each browser-test installation owns a randomly named `isla-acceptance-*` container and volume; the test removes those resources afterwards. It removes/recreates containers against the same volume, and restores a stopped volume into a separate volume. Chromium runs on the host; the application runs in the requested container architecture. For ARM64, build a `linux/arm64` image with Buildx, make emulation available if needed, and set `ISLA_TEST_PLATFORM=linux/arm64`. CI gates both architectures. Reports and failure screenshots are in `TestResults/`.

## Fresh interactive SQLite installation

Choose project names that are not already in use. The examples run sequentially on port 8080; stop any existing application using that port first.

```sh
docker compose -p isla-sqlite-qa -f compose.yaml up -d --build
docker compose -p isla-sqlite-qa -f compose.yaml logs isla
```

Read `ISLA first-run setup token:` from the Docker logs and open http://localhost:8080. Without an operator-supplied token, ISLA generates and prints a random token for the current unconfigured process. It rotates on restart and stops working when setup completes. Configured installations never generate another token. The Compose file also accepts `ISLA_SETUP_TOKEN` as `Setup__Token`; operators can mount a restricted secret file using `Setup__TokenFile`. Explicit tokens are never printed.

1. Enter the token and create an administrator with a password of 12–256 characters.
2. Confirm the wizard offers **Simple — JSON files** and **Medium — SQLite**.
3. Select SQLite, validate, review and initialise; sign in with “Remember this browser”.
4. Open Settings. Verify the active provider is `Sqlite` and the provider selector is enabled. It schedules changes for the next restart.
5. Complete the shared checklist below and export the household.
6. Run the following commands to recreate the container while retaining its volume:

```sh
docker compose -p isla-sqlite-qa -f compose.yaml down
docker compose -p isla-sqlite-qa -f compose.yaml up -d --no-build
```

Setup must remain closed; your remembered login, other accounts, recipes, planning, stock and prices must survive. A restart alone is insufficient: this check replaces the container.

Stop the SQLite test project with `down` before starting the JSON test. Keep the volume; do not use `down -v` when retaining data.

## Fresh interactive JSON installation

```sh
docker compose -p isla-json-qa -f compose.yaml up -d --build
```

Read this installation’s generated token from its Docker logs (or use your explicit operator token), create a destination administrator, select **Simple — JSON files**, and verify Settings reports `Json`. Import the SQLite export before adding any household data. Accounts do not transfer. Complete the shared checklist again, then recreate this project's container using its own project name.

Export the resulting JSON household. Initialise another empty SQLite project, import that export and compare recipes, goods, meals, extras, quantity overrides, Pantry, purchases and prices. Compare numeric values and relationships rather than JSON formatting. Keep source volumes until verification is complete.

To test deployment-controlled selection, use an unused project name and the override:

```sh
docker compose -p isla-fixed-sqlite-qa -f compose.yaml -f compose.sqlite.yaml up -d --build
```

SQLite must be selected and read-only in both setup and Settings. Stop the other test project first to release port 8080. Environment and command-line provider configuration override an operator-mounted configuration file, which overrides the locally saved provider.

## Shared functional checklist

Run against both providers, starting with the same fixture or a household export:

- Create/edit a recipe with `0.125 kg` carrots and `2 g` salt. Verify decimal quantities, ingredient order and notes survive reopening.
- Plan one fresh cook and two later leftovers in the same week. Only the fresh cook adds ingredients. Remove it; the first leftover becomes the fresh cook and later leftovers reference it.
- Add food and non-food goods. Verify case-insensitive reuse/duplicate handling, and rejection of known non-food recipe ingredients.
- Add/remove extra items; save and reset quantities. Put an item in Pantry and return it to the list. Verify persisted totals and exclusions.
- Buy two peppers for £2.40 and plan three next week: the estimate should be £3.60. Unticking retains price history. Zero prices work; negative prices and values with more than two decimal places are rejected without saving a purchase.
- Reopen the shopping page with in-store mode enabled. Verify its week, mode, bought items and price drafts restore.
- Create a Member and another Administrator. Members can use household pages and change their password, but cannot manage users/storage or import/export. A signed-in Member opening `/admin/export` sees Access denied (HTTP 403 after a redirect), remains signed in and can return to the planner. Denied POST actions return HTTP 403 directly. Anonymous or expired sessions go to sign-in. Check direct requests as well as the visible UI.
- Reset a member password, revoke sessions and disable the account. An already signed-in browser loses access. The last active administrator cannot be disabled or demoted.
- Open household routes anonymously: sign-in is required. Logout revokes the current session. Ordinary sign-in cookies are session cookies; remembered sign-ins have a fixed 90-day expiry.
- Export all household collections. Accounts, password hashes, sessions, setup tokens and keys must be absent.
- Attempt malformed/unsupported imports and import into a populated household. All fail without modifying the destination.

## Backup, recovery and provider switching

The automated container suite tests a stopped backup/restore, including identity and Data Protection keys. For an interactive exercise, stop the source container, copy the **entire** named volume into a separate empty volume, and start an isolated instance against that copy. Verify both sign-in and household data. Keep the source stopped while copying. SQLite backups include configuration, `isla.db`, its journals if present, the installation marker and `keys/`.

For a JSON-to-SQLite move in the same test volume: export, choose SQLite in Settings, confirm JSON remains active until restart, restart, initialise the empty SQLite destination, then import. JSON files remain intact. Switching back through Settings is refused if destination JSON files already exist; restore the source backup or explicitly select JSON through deployment configuration for rollback. Changes made in SQLite are not copied back automatically.

Use only disposable restored copies for destructive recovery checks: remove the configured database, corrupt its schema, or remove JSON identity/household data, then restart. Expect HTTP 503 recovery and closed setup; never a fresh empty household or automatic provider fallback. A second process on the same data directory must fail with an exclusive-access message.

Back up and stop the service before `--upgrade-storage` or `--reset-admin`; see README for those operator commands. The initial SQLite schema has a single migration, so current upgrade tests validate the current schema rather than a future release's migration.

## Release sign-off

Require .NET, JavaScript and real-browser gates, completed-install container recreation and stopped-volume restore for both providers, bidirectional household transfer, and container tests for each supported architecture. Record the checkout/image revision, architecture and test results. A local pass does not establish that a published image contains uncommitted changes.

## Verification record — 28 October 2026

Verified the Stage 2 working tree, including its uncommitted changes:

| Check | Result |
| --- | --- |
| Release .NET regression suite | 76 passed, none failed or skipped |
| Shopping-mode JavaScript suite | 5 passed |
| Chromium acceptance against local processes | All 6 scenarios passed |
| Chromium acceptance against direct Docker containers | All 6 scenarios passed on AMD64 and ARM64 |
| Chromium acceptance through the repository Compose service | All 6 scenarios passed on AMD64 and ARM64 |
| Container setup and Blazor startup assets | JSON and SQLite passed on both architectures |
| SQLite Compose configuration and workflow YAML | Validated |

ARM64 execution used QEMU emulation on an AMD64 host. The tested local image IDs were `sha256:3d0fff1dc6e45bff414110fc97fb15f0fdd3f91e965020a3d31075ac195e3435` (AMD64) and `sha256:b376bc53bc2ec1e7d66e7cd80e13cb96a6d19e7cb7fe72738cef929753c84826` (ARM64). This record does not imply publication to a registry or execution of a GitHub Actions run. Future schema upgrades remain subject to their own migration tests.

Console-token behaviour was subsequently restored at the user’s request: unconfigured installations generate a token when none is supplied; supplied tokens remain unlogged. The verification record above describes the earlier image builds.
