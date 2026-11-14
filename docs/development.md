# Development guide

Build and test ISLA from source. For installation and everyday use, see the [README](../README.md).

## Layout

```text
ISLA.sln        Solution
src/ISLA/       Blazor application
  Components/              Layout, navigation, and Razor pages
  Styles/app.css           Tailwind entry point and Blazor state styles
  wwwroot/                 Static assets (app.css is generated)
  package.json             CSS build and watch commands
Dockerfile                 Multi-stage Node/.NET build and non-root runtime
compose.yaml               Local production container
```

## Run locally

Install Git, the .NET 10 SDK, and Node.js 24 LTS (including npm).

Clone the repository:

```sh
git clone https://github.com/J35P1N/ISLA.git
cd ISLA
```

Build and launch the application from the repository root:

```sh
dotnet build ISLA.sln
```

Launch ISLA:

```sh
dotnet run --project src/ISLA --launch-profile http
```

On an unconfigured installation, ISLA generates a random first-run setup token and prints `ISLA first-run setup token: ...` in the server console. Enter it on the setup page. It stays in memory, changes if the process restarts before setup completes, and is rejected once setup completes. Configured installations do not generate or print another token.

Operators can instead supply `Setup__Token` or `Setup__TokenFile` (a restricted mounted secret file). Supplied tokens are never printed. Never put tokens in URLs or committed files.

Setup verifies the token, asks for administrator credentials, offers **Simple JSON** or **Medium SQLite** storage, validates the data directory, then shows a final review before initialisation. Deployment configuration can fix the provider in advance.

Open http://localhost:5080. The .NET build automatically installs locked npm dependencies when missing and compiles Tailwind before static assets are discovered. Commit `package-lock.json` when dependencies change; run `npm ci --prefix src/ISLA` to synchronize an existing install.

For HTTPS, trust a development certificate with `dotnet dev-certs https --trust`, then select `--launch-profile https` (https://localhost:7080).

For development with automatic updates, run these in separate terminals:

```sh
npm run css:watch --prefix src/ISLA
dotnet watch --project src/ISLA --launch-profile http
```

Edit `Styles/app.css` for theme/global styles and use Tailwind utilities directly in Razor components. Class scanning is explicitly scoped to `Components/`; use complete class names so Tailwind can discover them. The template reconnect component keeps its isolated CSS for Blazor connection state transitions.

## Build and publish

```sh
dotnet build ISLA.sln -c Release
dotnet publish src/ISLA -c Release
```

Both commands generate minified CSS. Generated CSS, build outputs, and npm dependencies are ignored by source control. `SkipTailwind=true` is reserved for pipelines that have already generated `wwwroot/app.css`, such as the Docker build.

## Verification

```sh
dotnet test ISLA.sln -c Release
node tests/browser/shopping-mode.test.mjs
npm ci --prefix tests/browser
npx --prefix tests/browser playwright install chromium
npm test --prefix tests/browser
```

The .NET suite covers purchase persistence and pricing, setup-token validation and setup races, legacy-data preservation, exclusive JSON ownership, authentication and revocation, fixed session expiry, role boundaries, export/import validation and round trips, operator password recovery, and HTTP recovery responses for missing or incompatible storage. Purchase tests run against both JSON and SQLite. SQLite tests additionally cover schema compatibility, transactional rollback, independent concurrent connections, persisted authentication, provider configuration precedence, explicit upgrades, and a full JSON-to-SQLite transfer with all household collections. The JavaScript suite checks mode persistence, clearing the preference on exit, and unavailable or malformed browser storage.

## Fresh-install acceptance testing

See [the JSON and SQLite acceptance guide](storage-acceptance.md) for isolated Compose installations, provider selection, recreation, backup/restore, transfer and recovery steps. The guide includes commands for running the same real-browser tests against local processes or disposable Docker volumes. Build from this checkout when validating unpublished changes.

